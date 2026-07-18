using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    public partial class DashboardPage : Page
    {
        private readonly DispatcherTimer _refreshTimer;
        private bool _loading;

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
            _refreshTimer.Start();
            await LoadSnapshotAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            App.MeshBridge.ConnectionChanged -= OnConnectionChanged;
        }

        private void OnConnectionChanged(bool connected) =>
            Dispatcher.BeginInvoke(new Action(async () => await LoadSnapshotAsync()));

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
        }

        private void ClearSnapshot()
        {
            DeviceValueText.Text = "-";
            DeviceDetailText.Text = "根节点不可用";
            SyncValueText.Text = "-";
            SyncDetailText.Text = "等待版本信息";
            StorageValueText.Text = "-";
            StorageDetailText.Text = "等待 SD 信息";
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
            LinkValueText.Text = rootAvailable ? "数据可用" : connected ? "等待根节点" : "未连接";
            LinkDetailText.Text = App.MeshBridge.CurrentType switch
            {
                TransportType.UsbSerial => "USB 串口",
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
