using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    /// <summary>
    /// 主窗口
    /// 左侧导航栏切换右侧页面，底部状态栏显示 Mesh 链路状态、在线设备数、传输类型与当前时间。
    /// 菜单按角色控制可见性：admin 全可见，teacher 隐藏角色权限和用户管理，student 仅见日志。
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

        /// <summary>窗口加载：初始化用户信息、应用角色可见性、默认页面、订阅 Mesh 事件、启动状态刷新</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 显示当前登录用户
            if (App.CurrentUser != null)
            {
                CurrentUserName.Text = App.CurrentUser.Name;
                CurrentUserRole.Text = App.CurrentUser.Role;
            }

            // 应用角色可见性
            ApplyRoleVisibility();

            // 订阅 Mesh 桥接器事件（设备连接/断开与链路状态变化时刷新状态）
            App.MeshBridge.DeviceConnected += OnDeviceConnectionChanged;
            App.MeshBridge.DeviceDisconnected += OnDeviceConnectionChanged;
            App.MeshBridge.ConnectionChanged += OnMeshConnectionChanged;

            // 默认打开首个可见页面
            _currentNavButton = GetDefaultNavButton();
            if (_currentNavButton != null)
            {
                _currentNavButton.Tag = "Active";
                NavigateByButton(_currentNavButton);
            }

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

        /// <summary>角色权限页（仅 admin 可见）</summary>
        private void NavRolePermission_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new RolePermissionPage());
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
        /// 根据当前用户角色控制导航菜单可见性
        /// admin：全部可见
        /// teacher：隐藏角色权限和用户管理
        /// student：仅见日志
        /// </summary>
        private void ApplyRoleVisibility()
        {
            string role = App.CurrentUser?.Role ?? "student";

            // 默认全部可见
            NavUserManage.Visibility = Visibility.Visible;
            NavPermission.Visibility = Visibility.Visible;
            NavRolePermission.Visibility = Visibility.Visible;
            NavDevice.Visibility = Visibility.Visible;
            NavLog.Visibility = Visibility.Visible;
            NavDeviceConfig.Visibility = Visibility.Visible;

            switch (role)
            {
                case "admin":
                    // 全部可见，角色权限仅 admin 可见
                    break;
                case "teacher":
                    // 隐藏角色权限和用户管理
                    NavRolePermission.Visibility = Visibility.Collapsed;
                    NavUserManage.Visibility = Visibility.Collapsed;
                    break;
                case "student":
                default:
                    // 学生仅见日志
                    NavUserManage.Visibility = Visibility.Collapsed;
                    NavPermission.Visibility = Visibility.Collapsed;
                    NavRolePermission.Visibility = Visibility.Collapsed;
                    NavDevice.Visibility = Visibility.Collapsed;
                    NavDeviceConfig.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>获取当前角色默认应打开的导航按钮（首个可见项）</summary>
        private Button? GetDefaultNavButton()
        {
            // 按顺序返回首个可见的导航按钮
            if (NavUserManage.Visibility == Visibility.Visible) return NavUserManage;
            if (NavPermission.Visibility == Visibility.Visible) return NavPermission;
            if (NavRolePermission.Visibility == Visibility.Visible) return NavRolePermission;
            if (NavDevice.Visibility == Visibility.Visible) return NavDevice;
            if (NavLog.Visibility == Visibility.Visible) return NavLog;
            if (NavDeviceConfig.Visibility == Visibility.Visible) return NavDeviceConfig;
            return null;
        }

        /// <summary>根据按钮导航到对应页面</summary>
        private void NavigateByButton(Button btn)
        {
            if (btn == NavUserManage) NavigateToPage(new UserManagePage());
            else if (btn == NavPermission) NavigateToPage(new PermissionPage());
            else if (btn == NavRolePermission) NavigateToPage(new RolePermissionPage());
            else if (btn == NavDevice) NavigateToPage(new DevicePage());
            else if (btn == NavLog) NavigateToPage(new LogPage());
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

        /// <summary>Mesh 链路连接状态变化回调（来自后台线程）</summary>
        private void OnMeshConnectionChanged(bool connected)
        {
            Dispatcher.BeginInvoke(new Action(UpdateStatusBar));
        }

        /// <summary>刷新底部状态栏：Mesh 链路状态、在线设备数、传输类型、当前时间</summary>
        private void UpdateStatusBar()
        {
            // Mesh 链路状态
            try
            {
                bool connected = App.MeshBridge.IsConnected;
                int onlineCount = App.MeshBridge.GetOnlineDevices().Count(d => !d.IsRoot);
                OnlineDeviceCount.Text = onlineCount.ToString();

                if (connected)
                {
                    MeshStatusText.Text = "链路已连接";
                    MeshStatusDot.Fill = FindResource("SuccessBrush") as System.Windows.Media.Brush;
                }
                else
                {
                    MeshStatusText.Text = "链路未连接";
                    MeshStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
                }

                // 传输类型显示
                TransportTypeText.Text = App.MeshBridge.CurrentType switch
                {
                    TransportType.UsbSerial => "USB 串口",
                    TransportType.TcpClient => "TCP 客户端",
                    TransportType.TcpServer => "TCP 服务端",
                    _ => "未启动"
                };

                bool rootAvailable = App.SdStorageService.IsAvailable;
                RootDataStatusText.Text = rootAvailable ? "根节点数据可用" : "根节点数据不可用";
                RootDataStatusDot.Fill = FindResource(
                    rootAvailable ? "SuccessBrush" : "DangerBrush") as System.Windows.Media.Brush;
            }
            catch
            {
                MeshStatusText.Text = "链路未启动";
                MeshStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
            }

            // 当前时间
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        protected override void OnClosed(EventArgs e)
        {
            // 解除事件订阅，避免内存泄漏
            App.MeshBridge.DeviceConnected -= OnDeviceConnectionChanged;
            App.MeshBridge.DeviceDisconnected -= OnDeviceConnectionChanged;
            App.MeshBridge.ConnectionChanged -= OnMeshConnectionChanged;
            _statusTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
