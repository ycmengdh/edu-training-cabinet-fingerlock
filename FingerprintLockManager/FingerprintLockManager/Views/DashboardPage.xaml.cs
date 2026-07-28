using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    public partial class DashboardPage : Page
    {
        private readonly DispatcherTimer _refreshTimer;
        private bool _loading;

        private bool IsDirectUart => string.Equals(ConfigHelper.Current.LinkMode, "Uart",
            StringComparison.OrdinalIgnoreCase);

        public DashboardPage()
        {
            InitializeComponent();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += async (_, _) => await LoadSnapshotAsync();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.ConnectionChanged += OnConnectionChanged;
            App.MessageHandler.OnStatusResponse += OnStatusResponse;
            _refreshTimer.Start();
            await LoadSnapshotAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            App.MeshBridge.ConnectionChanged -= OnConnectionChanged;
            App.MessageHandler.OnStatusResponse -= OnStatusResponse;
        }

        private void OnConnectionChanged(bool connected) =>
            Dispatcher.BeginInvoke(new Action(async () => await LoadSnapshotAsync()));

        private void OnStatusResponse(string deviceId, string statusJson)
        {
            if (!IsDirectUart) return;
            Dispatcher.BeginInvoke(new Action(ApplyDirectSnapshot));
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadSnapshotAsync();

        private async Task LoadSnapshotAsync()
        {
            if (_loading) return;
            _loading = true;
            RefreshButton.IsEnabled = false;
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

                SystemHealthSnapshot snapshot = await Task.Run(
                    App.SystemHealthService.LoadSnapshot);
                ApplySnapshot(snapshot);
                PageStatusText.Text = snapshot.CriticalCount > 0
                    ? $"发现 {snapshot.CriticalCount} 项异常，需要处理"
                    : snapshot.WarningCount > 0
                        ? $"系统可用，有 {snapshot.WarningCount} 项需要关注"
                        : "系统运行状态正常";
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

        private void ApplySnapshot(SystemHealthSnapshot snapshot)
        {
            DeviceValueText.Text = $"{snapshot.OnlineCount} / {snapshot.Devices.Count}";
            DeviceDetailText.Text = snapshot.Devices.Count == 0
                ? "尚未发现柜子"
                : $"离线 {snapshot.Devices.Count - snapshot.OnlineCount}";
            SyncValueText.Text = $"{snapshot.SynchronizedCount} / {snapshot.Devices.Count}";
            SyncDetailText.Text = $"根节点权限版本 {snapshot.Version.GlobalVersion}";
            StorageValueText.Text = snapshot.Version.SdTotalBytes == 0
                ? "-"
                : $"{snapshot.SdUsagePercent:F0}%";
            StorageDetailText.Text = snapshot.Version.SdTotalBytes == 0
                ? "未读取到容量"
                : $"{FormatBytes(snapshot.Version.SdUsedBytes)} / {FormatBytes(snapshot.Version.SdTotalBytes)}";
            AlertDataGrid.ItemsSource = snapshot.Alerts;
            AlertCountText.Text = snapshot.Alerts.Count == 0
                ? "无告警"
                : $"异常 {snapshot.CriticalCount} · 注意 {snapshot.WarningCount}";
            RecentLogDataGrid.ItemsSource = snapshot.RecentLogs;
            PendingLogText.Text = $"待传 {snapshot.PendingLogCount}";
            RefreshTimeText.Text = $"最近刷新 {snapshot.RefreshedAt:yyyy-MM-dd HH:mm:ss}";

            // V2.7：学生绑定设备统计（已分配权限的学生数 / 总学生数）
            PopulateStudentBindMetric();
        }

        private void ApplyDirectSnapshot()
        {
            DeviceClient? cabinet = GetDirectCabinet();
            bool connected = cabinet?.IsOnline == true && App.MeshBridge.IsConnected;
            DeviceRuntimeStatus status = cabinet?.Status ?? new DeviceRuntimeStatus();

            LinkMetricLabel.Text = "柜机直连";
            DeviceMetricLabel.Text = "当前柜机";
            SyncMetricLabel.Text = "权限记录";
            StorageMetricLabel.Text = "指纹数量";
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
            SyncDetailText.Text = $"权限版本 {status.PermissionVersion}";
            StorageValueText.Text = status.FingerprintCount.ToString();
            StorageDetailText.Text = "柜机内已录入指纹";

            int openLocks = status.LockStatus?.Count(value => value != 0) ?? 0;
            StudentBindValueText.Text = openLocks == 0 ? "全部关闭" : $"{openLocks} 路开启";
            StudentBindDetailText.Text = $"运行时间 {status.UptimeText}";
            PageStatusText.Text = connected
                ? $"已直连 {cabinet?.DeviceId} · 应急维护模式"
                : "柜机串口直连已断开";

            AlertDataGrid.ItemsSource = null;
            AlertCountText.Text = "应急模式";
            try
            {
                RecentLogDataGrid.ItemsSource = LogDatabase.ReadAllUnlock()
                    .OrderByDescending(log => log.CreateTime).Take(12).ToList();
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
            StorageMetricLabel.Text = "根节点存储";
            StudentBindMetricLabel.Text = "学生绑定";
        }

        /// <summary>
        /// V2.7：统计已分配权限覆盖的学生数与总学生数。
        /// 教师仅统计本班学生（数据范围隔离）。
        /// </summary>
        private void PopulateStudentBindMetric()
        {
            try
            {
                var visibleStudents = App.UserService.GetVisibleUsersByRole("student");
                int totalStudents = visibleStudents.Count;
                var boundUserIds = App.PermissionService.GetAllBoundUserIds();
                int boundStudents = visibleStudents.Count(u => boundUserIds.Contains(u.UserId));
                StudentBindValueText.Text = $"{boundStudents} / {totalStudents}";
                StudentBindDetailText.Text = totalStudents == 0
                    ? "暂无学生"
                    : $"绑定率 {(totalStudents > 0 ? boundStudents * 100.0 / totalStudents : 0):F0}%";
            }
            catch
            {
                StudentBindValueText.Text = "-";
                StudentBindDetailText.Text = "无法读取学生数据";
            }
        }

        private void ClearSnapshot()
        {
            DeviceValueText.Text = "-";
            DeviceDetailText.Text = "根节点不可用";
            SyncValueText.Text = "-";
            SyncDetailText.Text = "等待版本信息";
            StorageValueText.Text = "-";
            StorageDetailText.Text = "等待 SD 信息";
            StudentBindValueText.Text = "-";
            StudentBindDetailText.Text = "根节点不可用";
            AlertDataGrid.ItemsSource = null;
            RecentLogDataGrid.ItemsSource = null;
            AlertCountText.Text = "无法读取";
            PendingLogText.Text = "待传 -";
            RefreshTimeText.Text = "";
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

        private static string FormatBytes(ulong bytes) => bytes >= 1024UL * 1024 * 1024
            ? $"{bytes / 1024d / 1024 / 1024:F1} GB"
            : $"{bytes / 1024d / 1024:F0} MB";
    }
}
