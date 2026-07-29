using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 批量分配柜子权限：可视化多选在线柜 + 锁位 → 写权限 → 绑定 → 同步。
    /// 每柜每用户仅一枚当前指纹（见 CabinetBindingService）。
    /// </summary>
    public partial class BatchAssignPermissionWindow : BorderlessWindow
    {
        private readonly IReadOnlyList<User> _users;
        private readonly List<CabinetPickItem> _cabinets = new();
        private bool _busy;

        public int SuccessCount { get; private set; }
        public int FailCount { get; private set; }
        public BroadcastCommandResult? SyncResult { get; private set; }

        public BatchAssignPermissionWindow(IEnumerable<User> users)
        {
            InitializeComponent();
            _users = users?.Where(u => u != null).ToList() ?? new List<User>();
            SummaryText.Text = _users.Count == 0
                ? "未选择用户"
                : $"已选 {_users.Count} 个用户：{string.Join("、", _users.Take(5).Select(u => u.Name))}" +
                  (_users.Count > 5 ? "…" : "") +
                  " · 每柜每用户只占 1 指纹槽";
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

                _cabinets.Clear();
                foreach (var client in online.OrderBy(d => d.DeviceId))
                {
                    Device? device = saved.FirstOrDefault(item =>
                        string.Equals(item.DeviceId, client.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(client.MeshMac) &&
                         string.Equals(item.MeshMac, client.MeshMac, StringComparison.OrdinalIgnoreCase)));
                    string number = device?.DeviceNumber ?? "";
                    string name = !string.IsNullOrWhiteSpace(device?.DeviceName)
                        ? device!.DeviceName
                        : (string.IsNullOrWhiteSpace(client.DeviceName) ? client.DeviceId : client.DeviceName);
                    string label = string.IsNullOrWhiteSpace(number)
                        ? $"{name}  ·  {client.DeviceId}"
                        : $"{number} · {name}  ·  {client.DeviceId}";
                    var item = new CabinetPickItem
                    {
                        DeviceId = client.DeviceId,
                        DisplayText = label,
                        IsSelected = false
                    };
                    item.PropertyChanged += (_, _) => UpdateSelectionCount();
                    _cabinets.Add(item);
                }

                ApplyCabinetFilter();
                StatusText.Text = _cabinets.Count == 0
                    ? "当前没有在线柜子，请确认 Mesh 链路后再试"
                    : $"共 {_cabinets.Count} 台在线柜子，勾选后保存；离线柜请先组网";
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
            foreach (var item in _cabinets) item.IsSelected = true;
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
                AppToast.Warning("请至少勾选一个在线柜子");
                return;
            }

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

                StatusText.Text = "权限与绑定已写入，正在按柜同步（每用户一指纹槽）…";
                // 优先按选中柜同步，避免无关柜风暴
                var failedDevices = new List<string>();
                var confirmed = new List<string>();
                foreach (string deviceId in targetDevices)
                {
                    BroadcastCommandResult one = await Task.Run(() =>
                        App.CabinetSyncService.SyncCabinetPermissions(deviceId));
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
                        FailedDeviceIds = failedDevices.ToArray()
                    };

                SuccessCount = success;
                FailCount = fail;
                SyncResult = syncResult;

                string msg =
                    $"批量分配完成：用户成功 {success}，失败 {fail}\n" +
                    $"柜子确认 {confirmed.Count}/{targetDevices.Count}\n" +
                    CabinetSyncService.FormatSyncResult(syncResult,
                        "所选在线柜子均已确认权限更新（每用户一槽）。",
                        "部分柜子未确认，可在待同步队列中重试。");
                if (fail == 0 && syncResult.Success)
                {
                    AppToast.Success($"批量分配完成：{success} 用户 · {confirmed.Count} 柜");
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
