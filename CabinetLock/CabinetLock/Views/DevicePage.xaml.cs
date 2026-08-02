using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    public partial class DevicePage : Page
    {
        private readonly string _detailDeviceId = "";
        private Device? _selectedDevice;
        private System.Windows.Threading.DispatcherTimer? _refreshTimer;
        private List<DeviceFingerprintInfo> _fingerprints = new();
        private List<BackupFingerprintRow> _backupFingerprints = new();
        private bool _loading;
        private bool _busy;
        private int _fpListLoadVersion;
        private int? _reportedFingerprintCount;

        public DevicePage()
        {
            InitializeComponent();
            Loaded += DevicePage_Loaded;
            Unloaded += DevicePage_Unloaded;
        }

        public DevicePage(Device device)
            : this()
        {
            ArgumentNullException.ThrowIfNull(device);
            _detailDeviceId = device.DeviceId;
            _selectedDevice = device;
            UpdateCabinetSummary(device);
        }

        private async void DevicePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_detailDeviceId))
            {
                NavigationService?.Navigate(new CabinetManagePage());
                return;
            }

            App.MeshBridge.DeviceConnected += OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected += OnDevicePresenceChanged;
            LockSelectBox.SelectedIndex = 1;

            await LoadCabinetAsync();
            if (_selectedDevice != null)
            {
                await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
                await LoadBackupFingerprintListAsync(quiet: true);
            }

            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
        }

        private void DevicePage_Unloaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected -= OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected -= OnDevicePresenceChanged;
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Tick -= RefreshTimer_Tick;
                _refreshTimer = null;
            }
        }

        private async void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (IsLoaded && !_loading && !_busy) await LoadCabinetAsync(quiet: true);
        }

        private void OnDevicePresenceChanged(DeviceClient device)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (IsLoaded && !_loading && !_busy) await LoadCabinetAsync(quiet: true);
            }));
        }

        private async Task LoadCabinetAsync(bool quiet = false)
        {
            if (_loading) return;
            _loading = true;
            if (!quiet) SetBusy(true, "正在读取柜子状态");
            try
            {
                var devices = await Task.Run(App.DeviceService.GetAllDevices);
                var current = devices.FirstOrDefault(device =>
                    string.Equals(device.DeviceId, _detailDeviceId, StringComparison.OrdinalIgnoreCase));

                if (current != null)
                    _selectedDevice = current;
                if (_selectedDevice == null)
                {
                    PageStatusText.Text = "柜子已不在设备列表中";
                    return;
                }

                try
                {
                    _selectedDevice.RootPermissionVersion = await Task.Run(
                        CabinetSyncService.GetExpectedPermissionVersion);
                }
                catch
                {
                }

                UpdateCabinetSummary(_selectedDevice);
            }
            catch (Exception ex)
            {
                PageStatusText.Text = $"柜子状态读取失败：{ex.Message}";
            }
            finally
            {
                if (!quiet) SetBusy(false);
                _loading = false;
            }
        }

        private void UpdateCabinetSummary(Device device)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceName)) device.DeviceName = device.DeviceId;
            DataContext = null;
            DataContext = device;
            CabinetNameText.Text = device.DisplayIdentity;
            FingerprintCountText.Text = (_reportedFingerprintCount ??
                device.Status.FingerprintCount).ToString();
            PermissionCountText.Text = device.Status.PermissionCount.ToString();
            PermissionSyncStateText.Text = device.PermissionSyncText;

            string lastSeen = device.LastSeenTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "无记录";
            string identity = $"CAB MAC {device.MeshMac} · 通讯 ID {device.DeviceId}";
            PageStatusText.Text = device.IsOnline
                ? $"当前在线 · {identity} · 权限{device.PermissionSyncText} · 最后心跳 {lastSeen}"
                : $"当前离线 · {identity} · 最后在线 {lastSeen}";
        }

        private async Task LoadDeviceFpListAsync(string deviceId)
        {
            int version = Interlocked.Increment(ref _fpListLoadVersion);
            FpListStatusText.Text = "正在读取绑定用户与下位机状态";
            try
            {
                var result = await App.FingerprintTemplateService
                    .GetDeviceFingerprintListAsync(deviceId)
                    .ConfigureAwait(true);
                if (version != _fpListLoadVersion) return;

                _fingerprints = result.Items;
                _reportedFingerprintCount = result.ReportedFingerprintCount;
                if (result.ReportedStatus != null && _selectedDevice != null)
                {
                    _selectedDevice.Status = result.ReportedStatus;
                    _selectedDevice.RootPermissionVersion = await Task.Run(
                        CabinetSyncService.GetExpectedPermissionVersion);
                    UpdateCabinetSummary(_selectedDevice);
                }
                if (_reportedFingerprintCount.HasValue)
                    FingerprintCountText.Text = _reportedFingerprintCount.Value.ToString();
                ApplyFingerprintFilter();
                UpdateFingerprintStatus();
            }
            catch (Exception ex)
            {
                if (version != _fpListLoadVersion) return;
                _fingerprints.Clear();
                ApplyFingerprintFilter();
                FpListStatusText.Text = $"指纹用户读取失败：{ex.Message}";
            }
        }

        private void FingerprintFilter_Changed(object sender, RoutedEventArgs e) =>
            ApplyFingerprintFilter();

        private void ApplyFingerprintFilter()
        {
            if (DeviceFpListGrid == null) return;
            string keyword = FingerprintSearchBox?.Text?.Trim() ?? "";
            var visible = _fingerprints.Where(item =>
                string.IsNullOrWhiteSpace(keyword) ||
                item.FingerprintId.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (item.UserCode?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.UserName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                item.RoleText.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            DeviceFpListGrid.ItemsSource = visible;
            VisibleFingerprintCountText.Text = _reportedFingerprintCount.HasValue
                ? $"{visible.Count} 条 / {_reportedFingerprintCount.Value} 枚模板"
                : $"{visible.Count} 条";
            FingerprintEmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FingerprintEmptyText.Text = _fingerprints.Count == 0
                ? "当前柜子暂无绑定用户"
                : "没有符合条件的用户";
            UpdateSelectionActions();
        }

        private void UpdateFingerprintStatus()
        {
            int enabled = _fingerprints.Count(item => item.IsEnabled);
            FpListStatusText.Text = _reportedFingerprintCount.HasValue
                ? $"授权 {_fingerprints.Count} 人，启用 {enabled} 人 · 传感器内 {_reportedFingerprintCount.Value} 枚指纹模板"
                : $"授权 {_fingerprints.Count} 人，启用 {enabled} 人 · 柜机当前未响应";
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadCabinetAsync();
            if (_selectedDevice != null) await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
        }

        private async void ResyncButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            if (!EnsureDeviceOnline("同步权限")) return;

            SetBusy(true, "正在校验当前柜子的指纹与权限");
            try
            {
                var progress = new Progress<string>(message => PageStatusText.Text = message);
                CabinetDataSyncResult result = await App.CabinetSyncService.SyncCabinetDataAsync(
                    _selectedDevice.DeviceId, progress);
                await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
                await LoadCabinetAsync(quiet: true);
                if (result.Success) AppToast.Success("柜机数据已同步");
                else
                {
                    AppToast.Warning("柜机数据未完全同步");
                    MessageBox.Show(result.FormatForDisplay(), "柜机数据未完全同步",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppToast.Error($"柜机数据同步失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void ReadStatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            if (!EnsureDeviceOnline("读取状态")) return;

            SetBusy(true, "正在读取柜子状态");
            try
            {
                await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
                await LoadCabinetAsync(quiet: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RemoteUnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            if (!EnsureDeviceOnline("远程开锁")) return;

            int lockId = 1;
            if (LockSelectBox.SelectedItem is ComboBoxItem item && item.Tag != null)
                int.TryParse(item.Tag.ToString(), out lockId);
            if (lockId == 0 && App.CurrentUser?.Role != "admin")
            {
                MessageBox.Show("系统锁仅管理员可远程开启", "权限不足",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string lockName = LockNaming.ToDisplayName(lockId);
            SetBusy(true, $"正在开启 {lockName}");
            try
            {
                var message = Message.Create(Protocol.CmdControlLock, _selectedDevice.DeviceId,
                    new Dictionary<string, object>
                    {
                        ["lock_id"] = lockId,
                        ["action"] = "open",
                        ["operator"] = App.CurrentUser?.UserId ?? "system"
                    });
                var result = await App.CommandService.SendAsync(_selectedDevice.DeviceId, message);
                if (result.Success) AppToast.Success($"{lockName} 已开锁");
                else AppToast.Error(string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "开锁失败" : result.ErrorMessage);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void EnrollFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            if (!EnsureDeviceOnline("录入指纹")) return;

            var window = new EnrollFingerprintWindow(_selectedDevice.DeviceId)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            if (window.EnrolledFingerprintId > 0)
                _ = LoadDeviceFpListAsync(_selectedDevice.DeviceId);
        }

        private async void RefreshFpListButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice != null)
                await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true)
                NavigationService.GoBack();
            else
                NavigationService?.Navigate(new CabinetManagePage());
        }

        private async void DeleteSelectedFpButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedFingerprint(out var selected) || _selectedDevice == null) return;
            if (!EnsureDeviceOnline("删除指纹")) return;
            if (MessageBox.Show(
                    $"确认删除「{selected.UserName}（{selected.UserCode}）· 指纹 #{selected.FingerprintId}」在当前柜子的指纹和权限数据？\n\n" +
                    "用户主档及其在其他柜子的绑定不会受影响。",
                    "删除柜子用户指纹", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            SetBusy(true, "正在删除下位机指纹");
            try
            {
                var deleteResult = await App.CommandService.SendAsync(
                    _selectedDevice.DeviceId,
                    Message.Create(Protocol.CmdDeleteFingerprint, _selectedDevice.DeviceId,
                        new { fingerprint_id = selected.FingerprintId }));
                if (!deleteResult.Success)
                {
                    MessageBox.Show(string.IsNullOrWhiteSpace(deleteResult.ErrorMessage)
                            ? "柜子未确认删除指纹"
                            : deleteResult.ErrorMessage,
                        "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                bool bindingSaved = await Task.Run(() =>
                    App.CabinetBindingService.RemoveFingerprintFromCabinet(
                        selected.UserId!, _selectedDevice.DeviceId, selected.FingerprintId));
                if (!bindingSaved)
                {
                    MessageBox.Show(
                        "下位机指纹已删除，但绑定关系保存失败。该用户仍显示在列表中，可再次执行删除。",
                        "绑定保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                PageStatusText.Text = "指纹已删除，正在清理当前柜子的权限数据";
                var syncResult = await Task.Run(() =>
                    App.CabinetSyncService.SyncCabinetPermissions(_selectedDevice.DeviceId));
                await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
                await LoadCabinetAsync(quiet: true);

                MessageBox.Show(syncResult.Success
                        ? "已删除下位机指纹、用户绑定和对应权限数据"
                        : "指纹与绑定已删除，但柜子未确认权限同步。请点击“同步当前柜权限”重试。",
                    syncResult.Success ? "删除完成" : "权限待同步", MessageBoxButton.OK,
                    syncResult.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void EnrollBackupFpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            if (!EnsureDeviceOnline("录入副指纹")) return;
            string? userId = (DeviceFpListGrid.SelectedItem as DeviceFingerprintInfo)?.UserId
                ?? (BackupFpListGrid.SelectedItem as BackupFingerprintRow)?.UserId;
            var window = new BackupFingerprintWindow(_selectedDevice.DeviceId, userId)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _ = LoadDeviceFpListAsync(_selectedDevice.DeviceId);
            _ = LoadBackupFingerprintListAsync();
        }

        private async void RefreshBackupListButton_Click(object sender, RoutedEventArgs e) =>
            await LoadBackupFingerprintListAsync();

        private async Task LoadBackupFingerprintListAsync(bool quiet = false)
        {
            if (_selectedDevice == null) return;
            if (!quiet) BackupListStatusText.Text = "正在请求本机副指纹清单…";
            if (!IsDeviceMeshOnline(_selectedDevice))
            {
                _backupFingerprints.Clear();
                ApplyBackupList();
                BackupListStatusText.Text = "柜子离线，无法读取副指纹清单";
                return;
            }

            try
            {
                string? json = await App.CommandService.GetBackupFingerprintListAsync(
                    _selectedDevice.DeviceId);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _backupFingerprints.Clear();
                    ApplyBackupList();
                    BackupListStatusText.Text = "未收到副指纹清单（超时或固件不支持）";
                    return;
                }

                _backupFingerprints = ParseBackupList(json);
                ApplyBackupList();
                BackupListStatusText.Text = _backupFingerprints.Count == 0
                    ? "本柜暂无副指纹 · 仅本机生效，不占其他柜槽位"
                    : $"本机副指纹 {_backupFingerprints.Count} 条 · 不覆盖全局主指纹";
            }
            catch (Exception ex)
            {
                BackupListStatusText.Text = "读取副指纹失败：" + ex.Message;
            }
        }

        private void ApplyBackupList()
        {
            if (BackupFpListGrid == null) return;
            BackupFpListGrid.ItemsSource = _backupFingerprints.ToList();
            BackupEmptyState.Visibility = _backupFingerprints.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            BackupEmptyText.Text = "本柜暂无副指纹";
            UpdateSelectionActions();
        }

        private List<BackupFingerprintRow> ParseBackupList(string json)
        {
            var rows = new List<BackupFingerprintRow>();
            try
            {
                JToken root = JToken.Parse(json);
                JArray? backups = root["backups"] as JArray
                    ?? root["items"] as JArray
                    ?? root as JArray;
                if (backups == null) return rows;

                Dictionary<string, User> users;
                try
                {
                    users = App.UserService.GetVisibleUsers()
                        .ToDictionary(u => u.UserId, StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    users = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
                }

                foreach (JToken item in backups)
                {
                    string userId = item["user_id"]?.ToString()
                        ?? item["userId"]?.ToString()
                        ?? "";
                    if (string.IsNullOrWhiteSpace(userId)) continue;
                    int localId = item["local_fp_id"]?.Value<int?>()
                        ?? item["fingerprint_id"]?.Value<int?>()
                        ?? item["fp_id"]?.Value<int?>()
                        ?? 0;
                    users.TryGetValue(userId, out User? user);
                    rows.Add(new BackupFingerprintRow
                    {
                        UserId = userId,
                        UserCode = user?.DisplayId ?? userId,
                        UserName = user?.Name ?? userId,
                        LocalFpId = localId > 0 ? localId.ToString() : "—",
                        Note = "本机备用 · 不参与全局同步覆盖"
                    });
                }
            }
            catch
            {
                // keep empty
            }
            return rows.OrderBy(r => r.UserId, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async void DeleteBackupFpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            if (BackupFpListGrid.SelectedItem is not BackupFingerprintRow selected)
            {
                MessageBox.Show("请在「本机副指纹」列表中选择一条记录", "提示");
                return;
            }
            if (!EnsureDeviceOnline("删除副指纹")) return;
            if (MessageBox.Show(
                    $"确认删除「{selected.UserName}」在当前柜子的副指纹（槽 {selected.LocalFpId}）？",
                    "删除副指纹", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            SetBusy(true, "正在删除副指纹");
            try
            {
                var result = await App.CommandService.DeleteBackupFingerprintAsync(
                    _selectedDevice.DeviceId, selected.UserId);
                if (result.Success) AppToast.Success("副指纹已删除");
                else AppToast.Error(string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "删除副指纹失败" : result.ErrorMessage);
                await LoadBackupFingerprintListAsync();
                await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void DeviceFpListGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateSelectionActions();

        private void BackupFpListGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateSelectionActions();

        private bool TryGetSelectedFingerprint(out DeviceFingerprintInfo selected)
        {
            if (DeviceFpListGrid.SelectedItem is DeviceFingerprintInfo item &&
                item.FingerprintId > 0 && !string.IsNullOrWhiteSpace(item.UserId))
            {
                selected = item;
                return true;
            }
            selected = null!;
            MessageBox.Show("请先选择一名已绑定的指纹用户", "提示");
            return false;
        }

        private bool EnsureDeviceOnline(string action)
        {
            if (_selectedDevice != null && IsDeviceMeshOnline(_selectedDevice)) return true;
            MessageBox.Show($"当前柜子不在线，无法{action}", "柜子离线",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private static bool IsDeviceMeshOnline(Device device)
        {
            return App.MeshBridge.GetOnlineDevices().Any(client =>
                client.IsOnline &&
                (string.Equals(client.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(device.MeshMac) &&
                  string.Equals(client.MeshMac, device.MeshMac, StringComparison.OrdinalIgnoreCase))));
        }

        private void UpdateSelectionActions()
        {
            bool hasMain = !_busy &&
                DeviceFpListGrid?.SelectedItem is DeviceFingerprintInfo item &&
                item.FingerprintId > 0 && !string.IsNullOrWhiteSpace(item.UserId);
            bool hasBackup = !_busy &&
                BackupFpListGrid?.SelectedItem is BackupFingerprintRow;
            if (DeleteSelectedFpButton != null) DeleteSelectedFpButton.IsEnabled = hasMain;
            if (DeleteBackupFpButton != null) DeleteBackupFpButton.IsEnabled = hasBackup;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            ResyncButton.IsEnabled = !busy;
            RemoteUnlockButton.IsEnabled = !busy;
            ReadStatusButton.IsEnabled = !busy;
            EnrollFingerprintButton.IsEnabled = !busy;
            EnrollBackupFpButton.IsEnabled = !busy;
            RefreshFpListButton.IsEnabled = !busy;
            RefreshBackupListButton.IsEnabled = !busy;
            LockSelectBox.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
            UpdateSelectionActions();
        }

        private sealed class BackupFingerprintRow
        {
            public string UserId { get; init; } = "";
            public string UserCode { get; init; } = "";
            public string UserName { get; init; } = "";
            public string LocalFpId { get; init; } = "";
            public string Note { get; init; } = "";
        }
    }
}
