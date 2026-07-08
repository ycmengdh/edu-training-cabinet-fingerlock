using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    /// <summary>
    /// 主窗口
    /// 左侧导航栏切换右侧页面，底部状态栏显示 TCP 服务状态与在线设备数量
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>当前选中的导航按钮</summary>
        private Button? _currentNavButton;

        /// <summary>状态栏刷新定时器</summary>
        private DispatcherTimer? _statusTimer;

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>窗口加载：初始化用户信息、默认页面、订阅 TCP 事件、启动状态刷新</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 显示当前登录用户
            if (App.CurrentUser != null)
            {
                CurrentUserName.Text = App.CurrentUser.Name;
                CurrentUserRole.Text = App.CurrentUser.Role;
            }

            // 订阅 TCP 服务端事件（设备连接/断开时刷新状态）
            App.TcpServer.DeviceConnected += OnDeviceConnectionChanged;
            App.TcpServer.DeviceDisconnected += OnDeviceConnectionChanged;

            // 默认打开用户管理页面
            _currentNavButton = NavUserManage;
            NavigateToPage(new UserManagePage());

            // 刷新底部状态栏
            UpdateStatusBar();

            // 启动定时器（每秒刷新时间与在线设备数）
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (s, args) => UpdateStatusBar();
            _statusTimer.Start();
        }

        // ===== 导航按钮点击事件 =====

        private void NavUserManage_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new UserManagePage());
        }

        private void NavPermission_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new PermissionPage());
        }

        private void NavDevice_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new DevicePage());
        }

        private void NavLog_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new LogPage());
        }

        /// <summary>设备配置（AP模式）：打开独立窗口</summary>
        private void NavDeviceConfig_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            var window = new DeviceConfigWindow { Owner = this };
            window.ShowDialog();
        }

        /// <summary>
        /// 切换导航按钮选中状态（通过 Tag=Active 触发样式）
        /// </summary>
        private void SelectNavButton(object sender)
        {
            if (_currentNavButton != null)
            {
                _currentNavButton.Tag = null;
            }
            if (sender is Button btn)
            {
                btn.Tag = "Active";
                _currentNavButton = btn;
            }
        }

        /// <summary>
        /// 在 Frame 中导航到指定页面
        /// </summary>
        private void NavigateToPage(Page page)
        {
            ContentFrame.Navigate(page);
        }

        /// <summary>设备连接/断开回调（来自后台线程，需切到 UI 线程刷新）</summary>
        private void OnDeviceConnectionChanged(DeviceClient device)
        {
            Dispatcher.BeginInvoke(new Action(UpdateStatusBar));
        }

        /// <summary>刷新底部状态栏：TCP 状态、在线设备数、当前时间</summary>
        private void UpdateStatusBar()
        {
            // TCP 服务端状态
            try
            {
                int onlineCount = App.TcpServer.GetOnlineDevices().Count;
                OnlineDeviceCount.Text = onlineCount.ToString();
                TcpStatusText.Text = $"TCP服务：运行中（端口 {ConfigHelper.Current.TcpPort}）";
                TcpStatusDot.Fill = FindResource("SuccessBrush") as System.Windows.Media.Brush;
            }
            catch
            {
                TcpStatusText.Text = "TCP服务：未运行";
                TcpStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
            }

            // 当前时间
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        protected override void OnClosed(EventArgs e)
        {
            // 解除事件订阅，避免内存泄漏
            App.TcpServer.DeviceConnected -= OnDeviceConnectionChanged;
            App.TcpServer.DeviceDisconnected -= OnDeviceConnectionChanged;
            _statusTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
