using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    public partial class ClassCabinetSyncWindow : BorderlessWindow
    {
        private const int AutoProbeBudget = 30;
        private readonly string _classId;
        private readonly string _className;
        private readonly ObservableCollection<ClassCabinetSyncRow> _rows = new();
        private readonly CancellationTokenSource _cts = new();
        private List<User> _students = new();
        private string[] _knownDeviceIds = Array.Empty<string>();

        public ClassCabinetSyncWindow(string classId, string className)
        {
            InitializeComponent();
            _classId = classId;
            _className = className;
            TitleText.Text = $"{className} · 柜机分配同步";
            CabinetGrid.ItemsSource = _rows;
            Loaded += async (_, _) => await LoadAndCheckAsync();
        }

        private async Task LoadAndCheckAsync()
        {
            SetBusy(true, "正在读取班级学生与柜机");
            try
            {
                await LoadRowsAsync();
                var autoRows = new List<ClassCabinetSyncRow>();
                int probes = 0;
                foreach (ClassCabinetSyncRow row in _rows.Where(row => row.IsOnline && row.SyncableCount > 0))
                {
                    if (probes + row.SyncableCount > AutoProbeBudget)
                    {
                        row.Status = "待检测";
                        row.Detail = "批量检测已限流";
                        continue;
                    }
                    probes += row.SyncableCount;
                    autoRows.Add(row);
                }
                await CheckRowsAsync(autoRows);
                UpdateSummary();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PageStatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task LoadRowsAsync()
        {
            _students = (await Task.Run(App.UserService.GetAllUsers))
                .Where(user => string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(user.ClassId, _classId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(user => user.UserId).ToList();
            var devices = (await Task.Run(App.DeviceService.GetAllDevices))
                .Where(device => !DeviceService.IsTrueRoot(device) &&
                    !string.IsNullOrWhiteSpace(device.DeviceId))
                .OrderBy(device => device.DeviceNumber).ThenBy(device => device.DeviceName).ToList();
            _knownDeviceIds = devices.Select(device => device.DeviceId).ToArray();

            var assignments = _students.ToDictionary(
                user => user.UserId,
                user => App.CabinetBindingService.GetAssignedDeviceIds(user, _knownDeviceIds),
                StringComparer.OrdinalIgnoreCase);
            _rows.Clear();
            foreach (Device device in devices)
            {
                string[] assignedUserIds = _students
                    .Where(user => assignments[user.UserId].Contains(device.DeviceId))
                    .Select(user => user.UserId).ToArray();
                _rows.Add(new ClassCabinetSyncRow(device, _students.Count, assignedUserIds,
                    GetSyncableStudents(assignedUserIds).Count));
            }
            SelectAllOnlineCheckBox.IsChecked = false;
            StudentCountText.Text = $"学生 {_students.Count}";
            FingerprintCountText.Text = $"可同步 {_students.Count(IsSyncable)}";
            CabinetCountText.Text = $"柜机 {_rows.Count}";
        }

        private async Task CheckRowsAsync(IReadOnlyList<ClassCabinetSyncRow> rows)
        {
            if (rows.Count == 0) return;
            using var gate = new SemaphoreSlim(3);
            int total = rows.Sum(row => Math.Max(1, row.SyncableCount));
            int completed = 0;
            await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(_cts.Token);
                try
                {
                    await CheckRowAsync(row, () =>
                    {
                        int done = Interlocked.Increment(ref completed);
                        Dispatcher.Invoke(() => UpdateProgress(done, total, $"检测 {row.DisplayName}"));
                    });
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        private async Task CheckRowAsync(ClassCabinetSyncRow row, Action? itemCompleted = null)
        {
            if (!row.IsOnline)
            {
                row.Status = "离线";
                row.Detail = row.AssignedCount > 0 ? "已保存分配，等待柜机上线" : "未分配";
                return;
            }
            if (row.AssignedCount == 0)
            {
                row.Status = "未分配";
                row.Detail = "无需同步";
                return;
            }

            row.Status = "检测中";
            row.InSyncCount = 0;
            row.NeedSyncCount = 0;
            var failures = new List<string>();
            List<User> syncable = GetSyncableStudents(row.AssignedUserIds);
            if (syncable.Count == 0) itemCompleted?.Invoke();
            foreach (User student in syncable)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    UserCabinetSyncResult result = await App.CabinetSyncService
                        .CheckUserOnCabinetAsync(student, row.DeviceId, _cts.Token);
                    if (!result.Success) failures.Add($"{student.Name}: {result.ErrorMessage}");
                    else if (result.NeedsUpdate) row.NeedSyncCount++;
                    else row.InSyncCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{student.Name}: {ex.Message}");
                }
                finally
                {
                    itemCompleted?.Invoke();
                }
            }
            int missing = GetAssignedStudents(row.AssignedUserIds)
                .Count(user => user.Enabled && !user.FingerprintId.HasValue);
            SetCompletedStatus(row, failures, missing, "指纹与权限一致");
        }

        private async Task SyncRowsAsync(
            IReadOnlyList<ClassCabinetSyncRow> rows, bool refreshPermissionTable = true)
        {
            rows = rows.Where(row => row.IsOnline).Distinct().ToList();
            if (rows.Count == 0) return;
            SetBusy(true, "正在同步班级柜机数据");
            try
            {
                int total = rows.Sum(row => Math.Max(1,
                    (refreshPermissionTable ? 1 : 0) + row.SyncableCount));
                int completed = 0;
                using var gate = new SemaphoreSlim(2);
                await Task.WhenAll(rows.Select(async row =>
                {
                    await gate.WaitAsync(_cts.Token);
                    try
                    {
                        row.Status = "同步中";
                        row.InSyncCount = 0;
                        row.NeedSyncCount = 0;
                        var failures = new List<string>();
                        BroadcastCommandResult permissionSync = BroadcastCommandResult.Succeeded(
                            new[] { row.DeviceId });
                        int current = completed;
                        if (refreshPermissionTable)
                        {
                            permissionSync = await Task.Run(
                                () => App.CabinetSyncService.SyncCabinetPermissions(row.DeviceId),
                                _cts.Token);
                            current = Interlocked.Increment(ref completed);
                            Dispatcher.Invoke(() => UpdateProgress(current, total,
                                $"{row.DisplayName} · 权限表"));
                        }
                        List<User> syncable = GetSyncableStudents(row.AssignedUserIds);
                        if (!permissionSync.Success)
                        {
                            failures.Add(permissionSync.ErrorMessage);
                            if (syncable.Count > 0)
                            {
                                current = Interlocked.Add(ref completed, syncable.Count);
                                Dispatcher.Invoke(() => UpdateProgress(current, total,
                                    $"{row.DisplayName} · 同步未确认"));
                            }
                        }
                        else
                        {
                            if (syncable.Count == 0 && !refreshPermissionTable)
                            {
                                current = Interlocked.Increment(ref completed);
                                Dispatcher.Invoke(() => UpdateProgress(current, total,
                                    $"{row.DisplayName} · 无可同步指纹"));
                            }
                            foreach (User student in syncable)
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                IReadOnlyList<UserCabinetSyncResult> result = await App.CabinetSyncService
                                    .VerifyAndSyncUserAsync(student, new[] { row.DeviceId },
                                        cancellationToken: _cts.Token);
                                UserCabinetSyncResult? item = result.FirstOrDefault();
                                if (item == null || !item.Success)
                                    failures.Add($"{student.Name}: {item?.ErrorMessage ?? "未返回结果"}");
                                else row.InSyncCount++;
                                current = Interlocked.Increment(ref completed);
                                Dispatcher.Invoke(() => UpdateProgress(current, total,
                                    $"{row.DisplayName} · {student.Name}"));
                            }
                        }

                        int missing = GetAssignedStudents(row.AssignedUserIds)
                            .Count(user => user.Enabled && !user.FingerprintId.HasValue);
                        if (row.AssignedCount == 0 && failures.Count == 0)
                        {
                            row.Status = "未分配";
                            row.Detail = "柜机权限表已清理";
                        }
                        else
                        {
                            SetCompletedStatus(row, failures, missing, "分配、指纹与权限已同步");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        row.Status = "同步失败";
                        row.Detail = ex.Message;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
                UpdateSummary();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static void SetCompletedStatus(
            ClassCabinetSyncRow row, IReadOnlyList<string> failures, int missing, string successDetail)
        {
            row.Status = failures.Count > 0
                ? "部分失败"
                : row.NeedSyncCount > 0
                    ? "需同步"
                    : missing > 0 ? "缺少指纹" : "已同步";
            row.Detail = failures.Count > 0
                ? string.Join("；", failures.Where(text => !string.IsNullOrWhiteSpace(text)).Take(2))
                : row.NeedSyncCount > 0
                    ? $"{row.NeedSyncCount} 名学生需要更新"
                    : missing > 0 ? $"{missing} 名启用学生未录入指纹" : successDetail;
            if (string.IsNullOrWhiteSpace(row.Detail)) row.Detail = "柜机未确认同步";
        }

        private async Task ApplyAssignmentAsync(bool assigned)
        {
            string[] deviceIds = _rows.Where(row => row.IsSelected)
                .Select(row => row.DeviceId).ToArray();
            if (deviceIds.Length == 0)
            {
                MessageBox.Show("请先勾选柜机", "提示");
                return;
            }
            if (_students.Count == 0)
            {
                MessageBox.Show("当前班级没有学生", "提示");
                return;
            }
            if (!assigned && MessageBox.Show(
                    $"确认取消 {_className} 与选中 {deviceIds.Length} 台柜机的整班分配？",
                    "取消分配", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            SetBusy(true, assigned ? "正在保存整班分配" : "正在取消整班分配");
            try
            {
                bool saved = await Task.Run(() => App.CabinetBindingService.SetUsersAssignments(
                    deviceIds, _students.Select(user => user.UserId), assigned));
                if (!saved)
                {
                    MessageBox.Show("班级柜机分配保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                await LoadRowsAsync();
            }
            finally
            {
                SetBusy(false);
            }

            IReadOnlyList<ClassCabinetSyncRow> onlineRows = _rows
                .Where(row => deviceIds.Contains(row.DeviceId, StringComparer.OrdinalIgnoreCase) && row.IsOnline)
                .ToList();
            foreach (ClassCabinetSyncRow row in _rows.Where(row =>
                         deviceIds.Contains(row.DeviceId, StringComparer.OrdinalIgnoreCase) && !row.IsOnline))
            {
                row.Status = assigned ? "待上线" : "待清理";
                row.Detail = assigned ? "分配已保存" : "取消分配已保存";
            }
            await SyncRowsAsync(onlineRows, refreshPermissionTable: !assigned);
            UpdateSummary();
        }

        private List<User> GetAssignedStudents(IEnumerable<string> userIds)
        {
            HashSet<string> ids = userIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _students.Where(user => ids.Contains(user.UserId)).ToList();
        }

        private List<User> GetSyncableStudents(IEnumerable<string> userIds) =>
            GetAssignedStudents(userIds).Where(IsSyncable).ToList();

        private static bool IsSyncable(User user) => user.Enabled && user.FingerprintId.HasValue;

        private async void AssignSelectedButton_Click(object sender, RoutedEventArgs e) =>
            await ApplyAssignmentAsync(true);

        private async void RemoveSelectedButton_Click(object sender, RoutedEventArgs e) =>
            await ApplyAssignmentAsync(false);

        private async void CheckSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(row => row.IsSelected && row.IsOnline && row.AssignedCount > 0).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选已分配且在线的柜机", "提示");
                return;
            }
            SetBusy(true, "正在检测选中柜机");
            try
            {
                await CheckRowsAsync(selected);
                UpdateSummary();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void SyncSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(row => row.IsSelected && row.IsOnline).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选在线柜机", "提示");
                return;
            }
            await SyncRowsAsync(selected);
        }

        private async void SyncAllButton_Click(object sender, RoutedEventArgs e) =>
            await SyncRowsAsync(_rows.Where(row => row.IsOnline && row.AssignedCount > 0).ToList());

        private async void SyncOneButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ClassCabinetSyncRow { IsOnline: true } row)
                await SyncRowsAsync(new[] { row });
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadAndCheckAsync();

        private void SelectAllOnlineCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool selected = SelectAllOnlineCheckBox.IsChecked == true;
            foreach (ClassCabinetSyncRow row in _rows.Where(row => row.IsOnline))
                row.IsSelected = selected;
        }

        private void UpdateProgress(int completed, int total, string text)
        {
            SyncProgressBar.Value = total <= 0 ? 0 : completed * 100.0 / total;
            ProgressText.Text = total <= 0 ? text : $"{text} · {completed}/{total}";
        }

        private void UpdateSummary()
        {
            int ready = _rows.Count(row => row.Status == "已同步");
            ReadyCountText.Text = $"已同步 {ready}";
            PageStatusText.Text = $"在线 {_rows.Count(row => row.IsOnline)} 台 · 已分配 {_rows.Count(row => row.AssignedCount > 0)} 台 · 已同步 {ready} 台";
        }

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshButton.IsEnabled = !busy;
            AssignSelectedButton.IsEnabled = !busy;
            RemoveSelectedButton.IsEnabled = !busy;
            CheckSelectedButton.IsEnabled = !busy;
            SyncSelectedButton.IsEnabled = !busy;
            SyncAllButton.IsEnabled = !busy;
            SelectAllOnlineCheckBox.IsEnabled = !busy;
            CabinetGrid.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            base.OnClosed(e);
        }
    }

    public sealed class ClassCabinetSyncRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private int _inSyncCount;
        private int _needSyncCount;
        private string _status;
        private string _detail;

        public ClassCabinetSyncRow(
            Device device, int studentTotal, string[] assignedUserIds, int syncableCount)
        {
            DeviceId = device.DeviceId;
            DeviceNumber = device.DeviceNumber;
            DeviceName = device.DeviceName;
            MeshMac = device.MeshMac;
            IsOnline = device.IsOnline;
            StudentTotal = studentTotal;
            AssignedUserIds = assignedUserIds;
            SyncableCount = syncableCount;
            _status = !device.IsOnline
                ? "离线"
                : assignedUserIds.Length == 0 ? "未分配" : "待检测";
            _detail = !device.IsOnline && assignedUserIds.Length > 0
                ? "已保存分配，等待柜机上线"
                : assignedUserIds.Length == 0 ? "无需同步" : "";
        }

        public string DeviceId { get; }
        public string DeviceNumber { get; }
        public string DeviceName { get; }
        public string MeshMac { get; }
        public bool IsOnline { get; }
        public int StudentTotal { get; }
        public string[] AssignedUserIds { get; }
        public int AssignedCount => AssignedUserIds.Length;
        public int SyncableCount { get; }
        public string OnlineText => IsOnline ? "在线" : "离线";
        public string AssignmentText => AssignedCount == 0
            ? "未分配"
            : AssignedCount == StudentTotal ? $"整班 {AssignedCount}" : $"部分 {AssignedCount}/{StudentTotal}";
        public string DisplayName => string.IsNullOrWhiteSpace(DeviceNumber)
            ? DeviceName
            : $"{DeviceNumber} · {DeviceName}";
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
        public int InSyncCount { get => _inSyncCount; set => Set(ref _inSyncCount, value); }
        public int NeedSyncCount { get => _needSyncCount; set => Set(ref _needSyncCount, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public string Detail { get => _detail; set => Set(ref _detail, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
