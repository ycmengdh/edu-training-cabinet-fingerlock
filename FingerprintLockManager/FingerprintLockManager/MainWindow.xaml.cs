using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    /// <summary>
    /// 主窗口
    /// 左侧导航栏切换右侧页面，底部状态栏显示 Mesh 链路状态、在线设备数、传输类型与当前时间。
    /// 菜单按角色控制可见性（需求 3）：
    ///   admin：全部可见
    ///   teacher：可管理自己班级的数据（用户/班级/指纹/柜子分配/权限/设备/容量/下发状态/日志/设备配置）
    ///           隐藏角色权限和备份还原（涉及全局数据）
    ///   student：不能登录上位机（由 AuthService 拦截，本窗口不会显示给学生）
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

        /// <summary>班级管理（需求 4）</summary>
        private void NavClassManage_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new ClassManagePage());
        }

        /// <summary>指纹录入（需求 5）</summary>
        private void NavFpEnroll_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new FingerprintEnrollPage());
        }

        /// <summary>柜子分配（需求 6/8）</summary>
        private void NavAssignment_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new DeviceAssignmentPage());
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

        /// <summary>设备容量监控（需求 10）</summary>
        private void NavCapacity_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new DeviceCapacityPage());
        }

        /// <summary>下发状态监控（需求 7）</summary>
        private void NavDeployStatus_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new DeployStatusPage());
        }

        /// <summary>备份还原（需求 11，仅 admin）</summary>
        private void NavBackup_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new BackupRestorePage());
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
        /// 根据当前用户角色控制导航菜单可见性（需求 3）
        /// admin：全部可见
        /// teacher：可管理自己班级的数据，隐藏角色权限和备份还原（涉及全局数据）
        /// student：不能登录上位机（由 AuthService 拦截，理论上不会进入此分支）
        /// </summary>
        private void ApplyRoleVisibility()
        {
            string role = App.CurrentUser?.Role ?? "student";

            // 默认全部可见
            NavUserManage.Visibility = Visibility.Visible;
            NavClassManage.Visibility = Visibility.Visible;
            NavFpEnroll.Visibility = Visibility.Visible;
            NavAssignment.Visibility = Visibility.Visible;
            NavPermission.Visibility = Visibility.Visible;
            NavRolePermission.Visibility = Visibility.Visible;
            NavDevice.Visibility = Visibility.Visible;
            NavCapacity.Visibility = Visibility.Visible;
            NavDeployStatus.Visibility = Visibility.Visible;
            NavBackup.Visibility = Visibility.Visible;
            NavLog.Visibility = Visibility.Visible;
            NavDeviceConfig.Visibility = Visibility.Visible;

            switch (role)
            {
                case "admin":
                    // 全部可见
                    break;
                case "teacher":
                    // 老师隐藏角色权限（全局策略）和备份还原（涉及全局数据覆盖）
                    NavRolePermission.Visibility = Visibility.Collapsed;
                    NavBackup.Visibility = Visibility.Collapsed;
                    break;
                case "student":
                default:
                    // 学生不能登录上位机，理论上不会进入此分支；
                    // 兜底处理：仅显示日志查看
                    NavUserManage.Visibility = Visibility.Collapsed;
                    NavClassManage.Visibility = Visibility.Collapsed;
                    NavFpEnroll.Visibility = Visibility.Collapsed;
                    NavAssignment.Visibility = Visibility.Collapsed;
                    NavPermission.Visibility = Visibility.Collapsed;
                    NavRolePermission.Visibility = Visibility.Collapsed;
                    NavDevice.Visibility = Visibility.Collapsed;
                    NavCapacity.Visibility = Visibility.Collapsed;
                    NavDeployStatus.Visibility = Visibility.Collapsed;
                    NavBackup.Visibility = Visibility.Collapsed;
                    NavDeviceConfig.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>获取当前角色默认应打开的导航按钮（首个可见项）</summary>
        private Button? GetDefaultNavButton()
        {
            // 按顺序返回首个可见的导航按钮
            if (NavUserManage.Visibility == Visibility.Visible) return NavUserManage;
            if (NavClassManage.Visibility == Visibility.Visible) return NavClassManage;
            if (NavFpEnroll.Visibility == Visibility.Visible) return NavFpEnroll;
            if (NavAssignment.Visibility == Visibility.Visible) return NavAssignment;
            if (NavPermission.Visibility == Visibility.Visible) return NavPermission;
            if (NavRolePermission.Visibility == Visibility.Visible) return NavRolePermission;
            if (NavDevice.Visibility == Visibility.Visible) return NavDevice;
            if (NavCapacity.Visibility == Visibility.Visible) return NavCapacity;
            if (NavDeployStatus.Visibility == Visibility.Visible) return NavDeployStatus;
            if (NavBackup.Visibility == Visibility.Visible) return NavBackup;
            if (NavLog.Visibility == Visibility.Visible) return NavLog;
            if (NavDeviceConfig.Visibility == Visibility.Visible) return NavDeviceConfig;
            return null;
        }

        /// <summary>根据按钮导航到对应页面</summary>
        private void NavigateByButton(Button btn)
        {
            if (btn == NavUserManage) NavigateToPage(new UserManagePage());
            else if (btn == NavClassManage) NavigateToPage(new ClassManagePage());
            else if (btn == NavFpEnroll) NavigateToPage(new FingerprintEnrollPage());
            else if (btn == NavAssignment) NavigateToPage(new DeviceAssignmentPage());
            else if (btn == NavPermission) NavigateToPage(new PermissionPage());
            else if (btn == NavRolePermission) NavigateToPage(new RolePermissionPage());
            else if (btn == NavDevice) NavigateToPage(new DevicePage());
            else if (btn == NavCapacity) NavigateToPage(new DeviceCapacityPage());
            else if (btn == NavDeployStatus) NavigateToPage(new DeployStatusPage());
            else if (btn == NavBackup) NavigateToPage(new BackupRestorePage());
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
                int onlineCount = App.MeshBridge.GetOnlineDevices().Count;
                OnlineDeviceCount.Text = onlineCount.ToString();

                if (connected)
                {
                    MeshStatusText.Text = "Mesh链路：已连接";
                    MeshStatusDot.Fill = FindResource("SuccessBrush") as System.Windows.Media.Brush;
                }
                else
                {
                    MeshStatusText.Text = "Mesh链路：未连接";
                    MeshStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
                }

                // 传输类型显示
                TransportTypeText.Text = App.MeshBridge.CurrentType switch
                {
                    TransportType.UsbSerial => "USB串口",
                    TransportType.TcpClient => "TCP客户端",
                    TransportType.TcpServer => "TCP服务端",
                    _ => "未启动"
                };
            }
            catch
            {
                MeshStatusText.Text = "Mesh链路：未启动";
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
