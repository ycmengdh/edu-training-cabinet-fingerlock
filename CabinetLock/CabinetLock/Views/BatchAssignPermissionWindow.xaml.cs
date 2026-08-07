using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    /// <summary>
    /// 批量分配柜子权限：可视化多选在线柜 + 锁位 → 写权限 → 绑定 → 同步。
    /// 默认选择第一枚有效指纹，也支持按柜选择多枚（见 CabinetBindingService）。
    /// </summary>
    public partial class BatchAssignPermissionWindow : BorderlessWindow
    {
        private readonly IReadOnlyList<User> _users;
        private readonly string? _preferredDeviceId;
        private readonly List<CabinetPickItem> _cabinets = new();
        private bool _busy;

        public int SuccessCount { get; private set; }
        public int FailCount { get; private set; }
        public BroadcastCommandResult? SyncResult { get; private set; }

        public BatchAssignPermissionWindow(IEnumerable<User> users, string? preferredDeviceId = null)
        {
            InitializeComponent();
            _users = users?.Where(u => u != null).ToList() ?? new List<User>();
            _preferredDeviceId = preferredDeviceId;
            SummaryText.Text = _users.Count == 0
                ? "未选择用户"
                : $"已选 {_users.Count} 个用户：{string.Join("、", _users.Take(5).Select(u => u.Name))}" +
                  (_users.Count > 5 ? "…" : "") +
                  " · 每枚下发指纹占 1 个传感器槽位";
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Lock0Check.IsEnabled = App.CurrentUser?.Role == "admin";
            if (!Lock0Check.IsEnabled) Lock0Check.IsChecked = false;

            try
            {
                List<Device> saved = await Task.Run(() => App.DeviceService.GetAllDevices());
                var online = App.MeshBridge.GetOnlineDevices()
                    .Where(d => d.IsOnline && !d.IsRoot && !string.IsNullOrWhiteSpace(d.DeviceId))
                    .ToList();
                HashSet<string> onlineIds = online.Select(item => item.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                string[] knownIds = saved.Where(device => !DeviceService.IsTrueRoot(device))
                    .Select(device => device.DeviceId).ToArray();
                Dictionary<string, HashSet<string>> occupants = knownIds.ToDictionary(
                    id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
                foreach (User user in App.UserService.GetVisibleUsers().Where(user =>
                             string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (string deviceId in App.CabinetBindingService.GetAssignedDeviceIds(user, knownIds))
                    {
                        if (!occupants.TryGetValue(deviceId, out HashSet<string>? users))
                        {
                            users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            occupants[deviceId] = users;
                        }
                        users.Add(user.UserId);
                    }
                }

                _cabinets.Clear();
                foreach (Device device in saved.Where(item => !DeviceService.IsTrueRoot(item))
                             .OrderByDescending(item => onlineIds.Contains(item.DeviceId))
                             .ThenBy(item => occupants.GetValueOrDefault(item.DeviceId)?.Count ?? 0)
                             .ThenBy(item => item.DeviceNumber).ThenBy(item => item.DeviceName))
                {
                    string number = device.DeviceNumber ?? "";
                    string name = string.IsNullOrWhiteSpace(device.DeviceName) ? device.DeviceId : device.DeviceName;
                    HashSet<string> assignedUsers = occupants.GetValueOrDefault(device.DeviceId) ??
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool isOnline = onlineIds.Contains(device.DeviceId);
                    string availability = assignedUsers.Count == 0
                        ? "空闲"
                        : $"已分配 {assignedUsers.Count} 人 · 可重复分配";
                    string label = string.IsNullOrWhiteSpace(number)
                        ? $"{name} · {availability} · {(isOnline ? "在线" : "离线")}"
                        : $"{number} · {name} · {availability} · {(isOnline ? "在线" : "离线")}";
                    var item = new CabinetPickItem
                    {
                        DeviceId = device.DeviceId,
                        DisplayText = label,
                        IsOnline = isOnline,
                        AssignedUserIds = assignedUsers,
                        IsSelected = isOnline && string.Equals(device.DeviceId, _preferredDeviceId,
                            StringComparison.OrdinalIgnoreCase)
                    };
                    item.PropertyChanged += (_, _) => UpdateSelectionCount();
                    _cabinets.Add(item);
                }

                ApplyCabinetFilter();
                StatusText.Text = _cabinets.Count == 0
                    ? "当前没有柜子，请先维护柜机数据"
                    : $"共 {_cabinets.Count} 台柜子，空闲柜优先显示；已分配柜可重复选择";
                UpdateSelectionCount();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"读取柜子列表失败：{ex.Message}";
            }
        }

        private void ApplyCabinetFilter()
        {
            string keyword = CabinetSearchBox?.Text?.Trim() ?? "";
            var visible = string.IsNullOrWhiteSpace(keyword)
                ? _cabinets
                : _cabinets.Where(item =>
                    item.DisplayText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    item.DeviceId.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            CabinetList.ItemsSource = visible;
        }

        private void CabinetSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
            ApplyCabinetFilter();

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _cabinets)
                item.IsSelected = item.AssignedUserIds.Count == 0;
            UpdateSelectionCount();
        }

        private void ClearSelectButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _cabinets) item.IsSelected = false;
            UpdateSelectionCount();
        }

        private void UpdateSelectionCount()
        {
            int count = _cabinets.Count(item => item.IsSelected);
            SelectionCountText.Text = count == 0
                ? "已选 0 台柜子"
                : $"已选 {count} 台 · 约 {_users.Count * count} 个用户·柜槽位";
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _users.Count == 0) return;

            var targetDevices = _cabinets.Where(item => item.IsSelected)
                .Select(item => item.DeviceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targetDevices.Count == 0)
            {
                AppToast.Warning("请至少勾选一个柜子");
                return;
            }

            HashSet<string> selectedUserIds = _users.Select(user => user.UserId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<CabinetPickItem> duplicateTargets = _cabinets.Where(item => item.IsSelected)
                .Where(item => item.AssignedUserIds.Any(id => !selectedUserIds.Contains(id)) ||
                    _users.Count + item.AssignedUserIds.Count(id => !selectedUserIds.Contains(id)) > 1)
                .ToList();
            if (duplicateTargets.Count > 0 && MessageBox.Show(
                    $"本次选择会让 {duplicateTargets.Count} 台柜机由多个学生共用。\n" +
                    "其中可能包含已有学生的柜机，或本次同时分配多名学生的空闲柜。" +
                    "继续后将形成重复分配，请确认这是预期操作。",
                    "确认重复分配", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            bool[] perms =
            {
                Lock0Check.IsChecked == true,
                Lock1Check.IsChecked == true,
                Lock2Check.IsChecked == true,
                Lock3Check.IsChecked == true
            };

            SetBusy(true, $"正在为 {_users.Count} 个用户 × {targetDevices.Count} 台柜写入…");
            try
            {
                int success = 0, fail = 0;
                foreach (var user in _users)
                {
                    try
                    {
                        var dict = new Dictionary<int, bool>
                        {
                            { 0, perms[0] }, { 1, perms[1] }, { 2, perms[2] }, { 3, perms[3] }
                        };
                        bool ok = await Task.Run(() =>
                            App.PermissionService.SetUserPermissions(user.UserId, dict));
                        if (ok)
                        {
                            await Task.Run(() =>
                                App.CabinetBindingService.SetUsersAssignments(
                                    targetDevices, new[] { user.UserId }, true));
                            success++;
                        }
                        else fail++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        fail++;
                    }
                }

                StatusText.Text = "权限与绑定已写入，正在逐柜校验指纹与权限…";
                // 优先按选中柜同步，避免无关柜风暴
                var failedDevices = new List<string>();
                var confirmed = new List<string>();
                var queuedDevices = new List<string>();
                foreach (string deviceId in targetDevices)
                {
                    CabinetPickItem? target = _cabinets.FirstOrDefault(item =>
                        string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                    if (target?.IsOnline != true)
                    {
                        queuedDevices.Add(deviceId);
                        StatusText.Text = $"{deviceId} 离线，已加入待同步队列";
                        continue;
                    }
                    var progress = new Progress<string>(message =>
                        StatusText.Text = $"{deviceId}：{message}");
                    CabinetDataSyncResult one = await App.CabinetSyncService
                        .SyncCabinetDataAsync(deviceId, progress);
                    if (one.Success) confirmed.Add(deviceId);
                    else failedDevices.Add(deviceId);
                }

                var syncResult = failedDevices.Count == 0
                    ? BroadcastCommandResult.Succeeded(confirmed.ToArray())
                    : new BroadcastCommandResult
                    {
                        Success = false,
                        ErrorMessage = "部分柜子未确认权限同步",
                        ConfirmedDeviceIds = confirmed.ToArray(),
                        FailedDeviceIds = failedDevices.ToArray(),
                        MissingDeviceIds = queuedDevices.ToArray()
                    };

                SuccessCount = success;
                FailCount = fail;
                SyncResult = syncResult;

                string msg =
                    $"批量分配完成：用户成功 {success}，失败 {fail}\n" +
                    $"柜子确认 {confirmed.Count}，离线排队 {queuedDevices.Count}，失败 {failedDevices.Count}\n" +
                    CabinetSyncService.FormatSyncResult(syncResult,
                        "在线柜子均已确认，离线柜子将在上线后自动同步。",
                        "部分柜子未确认，可在待同步队列中重试。");
                if (fail == 0 && syncResult.Success)
                {
                    AppToast.Success($"批量分配完成：{success} 用户 · {confirmed.Count} 柜确认 · {queuedDevices.Count} 柜排队");
                }
                else
                {
                    AppToast.Warning($"完成但有失败：用户 {fail} · 柜未确认 {failedDevices.Count}");
                    MessageBox.Show(msg, "完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                DialogResult = true;
            }
            catch (Exception ex)
            {
                AppToast.Error("批量分配异常：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            ApplyButton.IsEnabled = !busy;
            CabinetList.IsEnabled = !busy;
            SelectAllButton.IsEnabled = !busy;
            ClearSelectButton.IsEnabled = !busy;
            CabinetSearchBox.IsEnabled = !busy;
            Lock0Check.IsEnabled = !busy && App.CurrentUser?.Role == "admin";
            Lock1Check.IsEnabled = !busy;
            Lock2Check.IsEnabled = !busy;
            Lock3Check.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) StatusText.Text = status;
        }

        private sealed class CabinetPickItem : INotifyPropertyChanged
        {
            private bool _isSelected;
            public string DeviceId { get; init; } = "";
            public string DisplayText { get; init; } = "";
            public bool IsOnline { get; init; }
            public HashSet<string> AssignedUserIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
