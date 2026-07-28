using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    public partial class TeacherFingerprintSyncWindow : BorderlessWindow
    {
        private const int AutoProbeBudget = 60;
        private readonly ObservableCollection<TeacherCabinetSyncRow> _rows = new();
        private readonly CancellationTokenSource _cts = new();
        private List<User> _teachers = new();
        private bool _busy;

        public TeacherFingerprintSyncWindow()
        {
            InitializeComponent();
            CabinetGrid.ItemsSource = _rows;
            Loaded += async (_, _) => await LoadAndCheckAsync();
        }

        private async Task LoadAndCheckAsync()
        {
            SetBusy(true, "正在检测老师指纹与权限");
            try
            {
                _teachers = (await Task.Run(() => App.UserService.GetUsersByRole("teacher")))
                    .Where(user => user.Enabled && user.FingerprintId.HasValue)
                    .OrderBy(user => user.Name).ToList();
                var devices = (await Task.Run(App.DeviceService.GetAllDevices))
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .OrderBy(device => device.DeviceNumber).ThenBy(device => device.DeviceName).ToList();
                _rows.Clear();
                foreach (Device device in devices) _rows.Add(new TeacherCabinetSyncRow(device));
                TeacherCountText.Text = $"老师 {_teachers.Count}";
                CabinetCountText.Text = $"柜机 {_rows.Count}";
                if (_teachers.Count == 0)
                {
                    PageStatusText.Text = "没有启用且已录入指纹的老师";
                    return;
                }
                var autoRows = new List<TeacherCabinetSyncRow>();
                int probes = 0;
                foreach (TeacherCabinetSyncRow row in _rows.Where(row => row.IsOnline))
                {
                    if (probes + _teachers.Count > AutoProbeBudget)
                    {
                        row.Status = "待检测";
                        row.Detail = "批量检测已限流";
                        continue;
                    }
                    probes += _teachers.Count;
                    autoRows.Add(row);
                }
                await CheckRowsAsync(autoRows);
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

        private async Task CheckRowsAsync(IReadOnlyList<TeacherCabinetSyncRow> rows)
        {
            using var gate = new SemaphoreSlim(3);
            int finished = 0;
            int total = rows.Count;
            await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(_cts.Token);
                try
                {
                    await CheckRowAsync(row);
                    int done = Interlocked.Increment(ref finished);
                    Dispatcher.Invoke(() => UpdateProgress(done, total, $"已检测 {row.DisplayName}"));
                }
                finally
                {
                    gate.Release();
                }
            }));
            UpdateSummary();
        }

        private async Task CheckRowAsync(TeacherCabinetSyncRow row)
        {
            if (!row.IsOnline)
            {
                row.Status = "离线";
                row.Detail = "等待柜机上线";
                return;
            }
            row.Status = "检测中";
            row.TeacherTotal = _teachers.Count;
            int inSync = 0;
            int need = 0;
            var failures = new List<string>();
            foreach (User teacher in _teachers)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    UserCabinetSyncResult result = await App.CabinetSyncService
                        .CheckUserOnCabinetAsync(teacher, row.DeviceId, _cts.Token);
                    if (!result.Success) failures.Add($"{teacher.Name}: {result.ErrorMessage}");
                    else if (result.NeedsUpdate) need++;
                    else inSync++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{teacher.Name}: {ex.Message}");
                }
                row.InSyncCount = inSync;
                row.NeedSyncCount = need;
            }
            row.Status = failures.Count > 0 ? "检测失败" : need > 0 ? "需同步" : "已同步";
            row.Detail = failures.Count > 0
                ? string.Join("；", failures.Take(2))
                : need > 0 ? $"{need} 位老师需要更新" : "老师指纹与权限一致";
        }

        private async Task SyncRowsAsync(IReadOnlyList<TeacherCabinetSyncRow> rows)
        {
            if (_teachers.Count == 0 || rows.Count == 0) return;
            SetBusy(true, "正在同步老师指纹");
            try
            {
                int total = rows.Count * _teachers.Count;
                int done = 0;
                using var gate = new SemaphoreSlim(2);
                await Task.WhenAll(rows.Select(async row =>
                {
                    await gate.WaitAsync(_cts.Token);
                    try
                    {
                        row.Status = "同步中";
                        var failures = new List<string>();
                        int synced = 0;
                        foreach (User teacher in _teachers)
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                            try
                            {
                                IReadOnlyList<UserCabinetSyncResult> result = await App.CabinetSyncService
                                    .VerifyAndSyncUserAsync(teacher, new[] { row.DeviceId },
                                        cancellationToken: _cts.Token);
                                UserCabinetSyncResult? item = result.FirstOrDefault();
                                if (item == null || !item.Success)
                                    failures.Add($"{teacher.Name}: {item?.ErrorMessage ?? "未返回结果"}");
                                else
                                    synced++;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                failures.Add($"{teacher.Name}: {ex.Message}");
                            }
                            finally
                            {
                                int current = Interlocked.Increment(ref done);
                                Dispatcher.Invoke(() => UpdateProgress(
                                    current, total, $"{row.DisplayName} · {teacher.Name}"));
                            }
                        }
                        row.TeacherTotal = _teachers.Count;
                        row.InSyncCount = synced;
                        row.NeedSyncCount = 0;
                        row.Status = failures.Count == 0 ? "已同步" : "部分失败";
                        row.Detail = failures.Count == 0
                            ? "老师指纹与权限已校验更新"
                            : string.Join("；", failures.Take(2));
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

        private async void SyncOneButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is TeacherCabinetSyncRow { IsOnline: true } row)
                await SyncRowsAsync(new[] { row });
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

        private async void CheckSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(row => row.IsSelected && row.IsOnline).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选在线柜机", "提示");
                return;
            }
            SetBusy(true, "正在检测选中柜机");
            try
            {
                await CheckRowsAsync(selected);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void SyncAllButton_Click(object sender, RoutedEventArgs e) =>
            await SyncRowsAsync(_rows.Where(row => row.IsOnline).ToList());

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadAndCheckAsync();

        private void UpdateProgress(int completed, int total, string text)
        {
            SyncProgressBar.Value = total <= 0 ? 0 : completed * 100.0 / total;
            ProgressText.Text = total <= 0 ? text : $"{text} · {completed}/{total}";
        }

        private void UpdateSummary()
        {
            int ready = _rows.Count(row => row.Status == "已同步");
            ReadyCountText.Text = $"已同步 {ready}";
            PageStatusText.Text = $"在线 {_rows.Count(row => row.IsOnline)} 台，已同步 {ready} 台";
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            CheckSelectedButton.IsEnabled = !busy;
            SyncSelectedButton.IsEnabled = !busy;
            SyncAllButton.IsEnabled = !busy;
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

    public sealed class TeacherCabinetSyncRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private int _teacherTotal;
        private int _inSyncCount;
        private int _needSyncCount;
        private string _status;
        private string _detail = "";

        public TeacherCabinetSyncRow(Device device)
        {
            DeviceId = device.DeviceId;
            DeviceNumber = device.DeviceNumber;
            DeviceName = device.DeviceName;
            MeshMac = device.MeshMac;
            IsOnline = device.IsOnline;
            _status = device.IsOnline ? "待检测" : "离线";
        }

        public string DeviceId { get; }
        public string DeviceNumber { get; }
        public string DeviceName { get; }
        public string MeshMac { get; }
        public bool IsOnline { get; }
        public string OnlineText => IsOnline ? "在线" : "离线";
        public string DisplayName => string.IsNullOrWhiteSpace(DeviceNumber) ? DeviceName : $"{DeviceNumber} · {DeviceName}";
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
        public int TeacherTotal { get => _teacherTotal; set => Set(ref _teacherTotal, value); }
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
