using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
// MouseButtonEventArgs via System.Windows.Input

namespace CabinetLock
{
    public partial class DashboardPage : Page
    {
        private const int MaxLiveEvents = 40;
        private readonly DispatcherTimer _refreshTimer;
        private readonly ObservableCollection<LiveCabinetEventRow> _liveEvents = new();
        private bool _loading;

        private bool IsDirectUart => string.Equals(ConfigHelper.Current.LinkMode, "Uart",
            StringComparison.OrdinalIgnoreCase);

        public DashboardPage()
        {
            InitializeComponent();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _refreshTimer.Tick += async (_, _) => await LoadSnapshotAsync();
            LiveEventDataGrid.ItemsSource = _liveEvents;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.ConnectionChanged += OnConnectionChanged;
            App.MessageHandler.OnStatusResponse += OnStatusResponse;
            App.MessageHandler.OnVerifyWindowEvent += OnVerifyWindowEvent;
            App.MessageHandler.OnDeviceRegistered += OnDeviceRegistered;
            _refreshTimer.Start();
            RefreshLiveEventCount();
            await ApplyCachedSnapshotAsync();
            await LoadSnapshotAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            App.MeshBridge.ConnectionChanged -= OnConnectionChanged;
            App.MessageHandler.OnStatusResponse -= OnStatusResponse;
            App.MessageHandler.OnVerifyWindowEvent -= OnVerifyWindowEvent;
            App.MessageHandler.OnDeviceRegistered -= OnDeviceRegistered;
        }

        private void OnConnectionChanged(bool connected) =>
            Dispatcher.BeginInvoke(new Action(async () => await LoadSnapshotAsync()));

        private void OnStatusResponse(string deviceId, string statusJson)
        {
            if (!IsDirectUart) return;
            Dispatcher.BeginInvoke(new Action(ApplyDirectSnapshot));
        }

        private void OnVerifyWindowEvent(string deviceId, string evt, string userId, int lockId)
        {
            string summary = evt?.ToLowerInvariant() switch
            {
                "enter" => string.IsNullOrWhiteSpace(userId)
                    ? "验证窗口开始"
                    : $"验证通过 · {userId}",
                "unlocked" => lockId >= 0 ? $"开锁 {LockNaming.ToDisplayName(lockId)}" : "开锁",
                "timeout" => "验证超时",
                "cancel" => "取消验证",
                _ => string.IsNullOrWhiteSpace(evt) ? "柜机事件" : evt
            };
            PushLiveEvent(deviceId, summary);
        }

        private void OnDeviceRegistered(string deviceId, string deviceName) =>
            PushLiveEvent(deviceId, string.IsNullOrWhiteSpace(deviceName) ? "上线注册" : $"上线 · {deviceName}");

        private void PushLiveEvent(string deviceId, string summary)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => PushLiveEvent(deviceId, summary)));
                return;
            }

            _liveEvents.Insert(0, new LiveCabinetEventRow
            {
                TimeText = DateTime.Now.ToString("HH:mm:ss"),
                DeviceId = deviceId ?? "",
                DeviceText = CompactDevice(deviceId),
                Summary = summary
            });
            while (_liveEvents.Count > MaxLiveEvents)
                _liveEvents.RemoveAt(_liveEvents.Count - 1);
            RefreshLiveEventCount();
        }

        private void RefreshLiveEventCount() =>
            LiveEventCountText.Text = _liveEvents.Count == 0 ? "暂无" : $"{_liveEvents.Count} 条";

        private static string CompactDevice(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return "—";
            string value = deviceId.Replace("CABINET_", "C", StringComparison.OrdinalIgnoreCase)
                .Replace("CAB_", "C", StringComparison.OrdinalIgnoreCase);
            return value.Length <= 12 ? value : value[^12..];
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadSnapshotAsync(showProgress: true);

        private void PendingSyncMetric_Click(object sender, MouseButtonEventArgs e)
        {
            Window? owner = Window.GetWindow(this);
            var window = new SyncQueueWindow();
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
            PopulatePendingSyncMetric();
        }

        private void AlertDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AlertDataGrid.SelectedItem is not SystemAlert alert) return;
            if (string.IsNullOrWhiteSpace(alert.DeviceId))
            {
                AppToast.Info(string.IsNullOrWhiteSpace(alert.ActionHint)
                    ? "系统级告警，请按建议处理"
                    : alert.ActionHint);
                if (alert.Source.Contains("Mesh", StringComparison.OrdinalIgnoreCase) ||
                    alert.Message.Contains("尚未发现", StringComparison.OrdinalIgnoreCase))
                {
                    (Window.GetWindow(this) as MainWindow)?.NavigateToCabinetList();
                }
                return;
            }
            (Window.GetWindow(this) as MainWindow)?.NavigateToCabinetDetail(alert.DeviceId);
        }

        private void LiveEventDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LiveEventDataGrid.SelectedItem is not LiveCabinetEventRow row) return;
            if (string.IsNullOrWhiteSpace(row.DeviceId))
            {
                AppToast.Info("该事件未关联具体柜子");
                return;
            }
            (Window.GetWindow(this) as MainWindow)?.NavigateToCabinetDetail(row.DeviceId);
        }

        private async Task ApplyCachedSnapshotAsync()
        {
            if (IsDirectUart) return;
            SystemHealthSnapshot? snapshot = App.SystemHealthService.LastSnapshot;
            if (!SnapshotMatchesCurrentUser(snapshot)) snapshot = null;
            SdVersionInfo? version = App.SdStorageService.LastVersion;
            if (snapshot == null && version != null)
            {
                try
                {
                    snapshot = await Task.Run(() =>
                        App.SystemHealthService.LoadSnapshot(version));
                }
                catch
                {
                    return;
                }
            }
            if (!IsLoaded || snapshot == null) return;
            ApplyNormalMetricLabels();
            UpdateLinkMetric();
            ApplySnapshot(snapshot);
            PageStatusText.Text = $"正在后台刷新 · 上次 {snapshot.RefreshedAt:HH:mm:ss}";
        }

        private async Task LoadSnapshotAsync(bool showProgress = false)
        {
            if (_loading) return;
            _loading = true;
            RefreshButton.IsEnabled = false;
            if (showProgress ||
                !SnapshotMatchesCurrentUser(App.SystemHealthService.LastSnapshot))
                PageStatusText.Text = "正在读取根节点运行状态";
            UpdateLinkMetric();
            try
            {
                if (IsDirectUart)
                {
                    PageStatusText.Text = "正在读取当前柜机状态";
                    DeviceClient? direct = GetDirectCabinet();
                    if (direct != null)
                        App.MeshBridge.Send(direct.DeviceId, Protocol.CmdReadStatus);
                    await Task.Delay(180);
                    ApplyDirectSnapshot();
                    return;
                }

                ApplyNormalMetricLabels();
                if (!App.SdStorageService.IsAvailable)
                    throw new RootDataUnavailableException("根节点数据服务尚未连接");

                SystemHealthSnapshot snapshot =
                    await App.SystemHealthService.LoadSnapshotAsync();
                if (!IsLoaded) return;
                ApplySnapshot(snapshot);
                PageStatusText.Text = snapshot.CriticalCount > 0
                    ? $"发现 {snapshot.CriticalCount} 项异常，需要处理"
                    : snapshot.WarningCount > 0
                        ? $"系统可用，有 {snapshot.WarningCount} 项需要关注"
                        : "系统运行状态正常 · 支持逐柜多指纹授权";
            }
            catch (RootDataUnavailableException ex)
            {
                PageStatusText.Text = ex.Message;
                ClearSnapshot();
            }
            finally
            {
                _loading = false;
                RefreshButton.IsEnabled = true;
            }
        }

        private static bool SnapshotMatchesCurrentUser(SystemHealthSnapshot? snapshot) =>
            snapshot != null &&
            string.Equals(snapshot.ScopeUserId, App.CurrentUser?.UserId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ScopeRole, App.CurrentUser?.Role,
                StringComparison.OrdinalIgnoreCase);

        private void ApplySnapshot(SystemHealthSnapshot snapshot)
        {
            DeviceValueText.Text = $"{snapshot.OnlineCount} / {snapshot.Devices.Count}";
            DeviceDetailText.Text = snapshot.Devices.Count == 0
                ? "尚未发现柜子"
                : $"离线 {snapshot.Devices.Count - snapshot.OnlineCount}";
            SyncValueText.Text = $"{snapshot.SynchronizedCount} / {snapshot.Devices.Count}";
            SyncDetailText.Text = snapshot.Version.SdTotalBytes == 0
                ? $"权限版本 {snapshot.Version.GlobalVersion}"
                : $"SD {snapshot.SdUsagePercent:F0}% · v{snapshot.Version.GlobalVersion}";
            AlertDataGrid.ItemsSource = snapshot.Alerts;
            AlertCountText.Text = snapshot.Alerts.Count == 0
                ? "无告警"
                : $"异常 {snapshot.CriticalCount} · 注意 {snapshot.WarningCount}";
            RecentLogDataGrid.ItemsSource = snapshot.RecentLogs;
            PendingLogText.Text = $"待传 {snapshot.PendingLogCount}";
            RefreshTimeText.Text = $"最近刷新 {snapshot.RefreshedAt:yyyy-MM-dd HH:mm:ss}";
            PopulateStudentBindMetric(snapshot.BoundStudentCount,
                snapshot.TotalStudentCount);
            ApplyPendingSyncMetric(snapshot.OpenSyncCount, snapshot.FailedSyncCount);
        }

        private void ApplyDirectSnapshot()
        {
            DeviceClient? cabinet = GetDirectCabinet();
            bool connected = cabinet?.IsOnline == true && App.MeshBridge.IsConnected;
            DeviceRuntimeStatus status = cabinet?.Status ?? new DeviceRuntimeStatus();

            LinkMetricLabel.Text = "柜机直连";
            DeviceMetricLabel.Text = "当前柜机";
            SyncMetricLabel.Text = "权限记录";
            PendingSyncMetricLabel.Text = "指纹槽位";
            StudentBindMetricLabel.Text = "柜门状态";

            LinkStatusDot.Fill = FindResource(connected ? "SuccessBrush" : "DangerBrush")
                as System.Windows.Media.Brush;
            LinkValueText.Text = connected ? "已连接" : "已断开";
            LinkDetailText.Text = string.IsNullOrWhiteSpace(ConfigHelper.Current.UartSerialPortName)
                ? "柜机串口"
                : ConfigHelper.Current.UartSerialPortName;

            DeviceValueText.Text = connected ? "1 / 1" : "0 / 1";
            DeviceDetailText.Text = cabinet == null
                ? "等待柜机响应"
                : string.IsNullOrWhiteSpace(cabinet.DeviceName)
                    ? cabinet.DeviceId
                    : $"{cabinet.DeviceName} · {cabinet.DeviceId}";
            SyncValueText.Text = status.PermissionCount.ToString();
            SyncDetailText.Text = $"权限版本 {status.PermissionVersion} · 一人一槽";
            PendingSyncValueText.Text = status.FingerprintCount.ToString();
            PendingSyncDetailText.Text = status.FingerprintCount >= 180
                ? "接近 200 上限，请精简"
                : "本机已用槽位";

            int openLocks = status.LockStatus?.Count(value => value != 0) ?? 0;
            StudentBindValueText.Text = openLocks == 0 ? "全部关闭" : $"{openLocks} 路开启";
            StudentBindDetailText.Text = $"运行时间 {status.UptimeText}";
            PageStatusText.Text = connected
                ? $"已直连 {cabinet?.DeviceId} · 应急维护 · 多指纹模式"
                : "柜机串口直连已断开";

            AlertDataGrid.ItemsSource = null;
            AlertCountText.Text = "应急模式";
            try
            {
                RecentLogDataGrid.ItemsSource = App.LogService.QueryLogs(limit: 12);
            }
            catch
            {
                RecentLogDataGrid.ItemsSource = null;
            }
            PendingLogText.Text = "本机日志";
            RefreshTimeText.Text = $"最近刷新 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }

        private static DeviceClient? GetDirectCabinet() => App.MeshBridge.GetOnlineDevices()
            .FirstOrDefault(device => !DeviceService.IsTrueRoot(device));

        private void ApplyNormalMetricLabels()
        {
            LinkMetricLabel.Text = "根节点链路";
            DeviceMetricLabel.Text = "柜子在线";
            SyncMetricLabel.Text = "权限一致";
            PendingSyncMetricLabel.Text = "待同步";
            StudentBindMetricLabel.Text = "学生绑定";
        }

        private void PopulatePendingSyncMetric()
        {
            try
            {
                (int open, int failed) =
                    App.CabinetSyncQueueService.CountOpenAndFailed();
                ApplyPendingSyncMetric(open, failed);
            }
            catch
            {
                PendingSyncValueText.Text = "-";
                PendingSyncDetailText.Text = "队列不可用";
            }
        }

        private void ApplyPendingSyncMetric(int open, int failed)
        {
            PendingSyncValueText.Text = open.ToString();
            PendingSyncDetailText.Text = open == 0
                ? "队列空闲 · 点此查看"
                : failed > 0
                    ? $"失败 {failed} · 点此处理"
                    : "等待下发 · 点此查看";
        }

        private void PopulateStudentBindMetric(int boundStudents, int totalStudents)
        {
            StudentBindValueText.Text = $"{boundStudents} / {totalStudents}";
            StudentBindDetailText.Text = totalStudents == 0
                ? "暂无学生"
                : $"绑定率 {boundStudents * 100.0 / totalStudents:F0}%";
        }

        private void ClearSnapshot()
        {
            DeviceValueText.Text = "-";
            DeviceDetailText.Text = "根节点不可用";
            SyncValueText.Text = "-";
            SyncDetailText.Text = "等待版本信息";
            PendingSyncValueText.Text = "-";
            PendingSyncDetailText.Text = "根节点不可用";
            StudentBindValueText.Text = "-";
            StudentBindDetailText.Text = "根节点不可用";
            AlertDataGrid.ItemsSource = null;
            RecentLogDataGrid.ItemsSource = null;
            AlertCountText.Text = "无法读取";
            PendingLogText.Text = "待传 -";
            RefreshTimeText.Text = "";
            PopulatePendingSyncMetric();
        }

        private void UpdateLinkMetric()
        {
            bool connected = App.MeshBridge.IsConnected;
            bool rootAvailable = App.SdStorageService.IsAvailable;
            LinkStatusDot.Fill = FindResource(connected ? "SuccessBrush" : "DangerBrush")
                as System.Windows.Media.Brush;
            LinkValueText.Text = IsDirectUart
                ? connected ? "已连接" : "未连接"
                : rootAvailable ? "数据可用" : connected ? "等待根节点" : "未连接";
            LinkDetailText.Text = App.MeshBridge.CurrentType switch
            {
                TransportType.UsbSerial => IsDirectUart ? "柜机串口直连" : "组网U盘连接",
                TransportType.TcpClient => "TCP 客户端",
                TransportType.TcpServer => "TCP 服务端",
                _ => "链路未启动"
            };
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        private sealed class LiveCabinetEventRow
        {
            public string TimeText { get; init; } = "";
            public string DeviceId { get; init; } = "";
            public string DeviceText { get; init; } = "";
            public string Summary { get; init; } = "";
        }
    }
}
