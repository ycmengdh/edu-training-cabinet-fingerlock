using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    public partial class CommunicationTestWindow : BorderlessWindow
    {
        private const int MaxVisibleEntries = 400;
        private readonly DispatcherTimer _statusTimer;

        public ObservableCollection<CommunicationTraceEntry> TraceEntries { get; } = new();

        public CommunicationTestWindow()
        {
            InitializeComponent();
            DataContext = this;
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            foreach (var entry in App.MeshBridge.RecentTrace.TakeLast(MaxVisibleEntries))
                TraceEntries.Add(entry);
            App.MeshBridge.TraceAdded += OnTraceAdded;
            App.SdStorageService.StatusChanged += OnStorageStatusChanged;
            _statusTimer.Start();
            UpdateStatus();
            ScrollToEnd();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _statusTimer.Stop();
            App.MeshBridge.TraceAdded -= OnTraceAdded;
            App.SdStorageService.StatusChanged -= OnStorageStatusChanged;
        }

        private void OnTraceAdded(CommunicationTraceEntry entry)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        TraceEntries.Add(entry);
                        while (TraceEntries.Count > MaxVisibleEntries) TraceEntries.RemoveAt(0);
                        ScrollToEnd();
                        UpdateStatus();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CommTest] Append: {ex.Message}");
                    }
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommTest] OnTraceAdded: {ex.Message}");
            }
        }

        private void OnStorageStatusChanged() =>
            Dispatcher.BeginInvoke(new Action(UpdateStatus));

        private void SendTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (!App.MeshBridge.IsConnected)
            {
                TestResultText.Text = "发送失败：物理链路未连接";
                TestResultText.Foreground = FindResource("DangerBrush") as Brush;
                return;
            }

            bool sent = App.MeshBridge.Send("", Protocol.CmdRegister);
            TestResultText.Text = sent
                ? "REGISTER 已发送，正在等待根节点返回…"
                : "发送失败，请查看传输层错误";
            TestResultText.Foreground = FindResource(sent ? "PrimaryBrush" : "DangerBrush") as Brush;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.ClearTrace();
            TraceEntries.Clear();
            TestResultText.Text = "记录已清空，可重新发送测试";
            TestResultText.Foreground = FindResource("SubTextBrush") as Brush;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (TraceEntries.Count == 0) return;
            var text = new StringBuilder();
            foreach (var entry in TraceEntries) text.AppendLine(entry.CopyText);
            Clipboard.SetText(text.ToString());
            TestResultText.Text = $"已复制 {TraceEntries.Count} 条记录";
            TestResultText.Foreground = FindResource("PrimaryBrush") as Brush;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdateStatus()
        {
            bool physical = App.MeshBridge.IsConnected;
            bool protocol = App.MeshBridge.ReceivedCount > 0;
            bool root = App.SdStorageService.IsRootConnected;
            bool? storage = App.SdStorageService.IsStorageReady;

            TransportDescriptionText.Text = App.MeshBridge.TransportDescription;
            SentCountText.Text = App.MeshBridge.SentCount.ToString();
            ReceivedCountText.Text = App.MeshBridge.ReceivedCount.ToString();

            SetStatus(PhysicalStatusDot, PhysicalStatusText, physical,
                physical ? "已连接" : "未连接");
            SetStatus(ProtocolStatusDot, ProtocolStatusText, protocol,
                protocol ? $"已收到 {App.MeshBridge.ReceivedCount} 条" : "未收到");
            SetStatus(RootStatusDot, RootStatusText, root,
                root ? App.SdStorageService.RootDeviceId : "未注册");

            if (!root)
                SetStatus(StorageStatusDot, StorageStatusText, false, "等待根节点");
            else if (storage == false)
                SetStatus(StorageStatusDot, StorageStatusText, false, "未就绪", true);
            else if (storage == true)
                SetStatus(StorageStatusDot, StorageStatusText, true, "已就绪");
            else
                SetStatus(StorageStatusDot, StorageStatusText, false, "旧固件未报告", true);

            string error = App.MeshBridge.LastTransportError;
            LastErrorText.Text = string.IsNullOrWhiteSpace(error)
                ? "提示：普通串口助手发送纯文本不会得到协议应答，测试按钮发送的是完整二进制帧"
                : $"传输层错误：{error}";
            LastErrorText.Foreground = FindResource(
                string.IsNullOrWhiteSpace(error) ? "SubTextBrush" : "DangerBrush") as Brush;
        }

        private void SetStatus(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock text,
            bool success, string value, bool warning = false)
        {
            string brush = success ? "SuccessBrush" : (warning ? "WarningBrush" : "DangerBrush");
            dot.Fill = FindResource(brush) as Brush;
            text.Text = value;
        }

        private void ScrollToEnd()
        {
            if (TraceEntries.Count > 0) TraceList.ScrollIntoView(TraceEntries[^1]);
        }
    }
}
