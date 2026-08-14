using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.ComponentModel;

namespace CabinetLock
{
    /// <summary>
    /// 主窗口
    /// 左侧导航栏切换右侧页面，底部状态栏显示 Mesh 链路状态、在线设备数、传输类型与当前时间。
    /// 菜单按角色控制可见性：admin 负责全局管理，teacher 进入班级工作台，student 不允许登录。
    /// </summary>
    public partial class MainWindow : BorderlessWindow
    {
        /// <summary>当前选中的导航按钮</summary>
        private Button? _currentNavButton;

        /// <summary>状态栏刷新定时器</summary>
        private DispatcherTimer? _statusTimer;
        private bool _pendingSyncStatusLoading;
        private DateTime _lastPendingSyncStatusRefreshUtc = DateTime.MinValue;
        private int _statusTimerTicks;
        private int _statusRefreshQueued;

        private bool _loggingOut;

        private bool IsDirectUart => string.Equals(ConfigHelper.Current.LinkMode, "Uart",
            StringComparison.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>窗口加载：初始化用户信息、应用角色可见性、默认页面、订阅 Mesh 事件、启动状态刷新</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshCurrentUserDisplay();

            // 应用角色可见性
            ApplyRoleVisibility();
            // 仅管理员可手动同步业务库到 SD
            string role = App.CurrentUser?.Role ?? "";
            SyncToSdButton.Visibility = role == "admin" && !IsDirectUart
                ? Visibility.Visible
                : Visibility.Collapsed;
            DirectModeBanner.Visibility = IsDirectUart ? Visibility.Visible : Visibility.Collapsed;
            if (IsDirectUart)
                SyncStatusText.Text = "本机变更待恢复组网后同步";

            // 订阅 Mesh 桥接器事件（设备连接/断开与链路状态变化时刷新状态）
            App.MeshBridge.DeviceConnected += OnDeviceConnectionChanged;
            App.MeshBridge.DeviceDisconnected += OnDeviceConnectionChanged;
            App.MeshBridge.ConnectionChanged += OnMeshConnectionChanged;
            App.CommunicationCoordinator.StateChanged += OnCommunicationStateChanged;

            // 默认打开首个可见页面
            _currentNavButton = GetDefaultNavButton();
            if (_currentNavButton != null)
            {
                _currentNavButton.Tag = "Active";
                NavigateByButton(_currentNavButton);
            }

            // 刷新底部状态栏
            UpdateStatusBar();
            ApplyCommunicationState(App.CommunicationCoordinator.Current);
            _ = RefreshPendingSyncStatusAsync(force: true);
            UpdateThemeToggle();
            ThemeManager.ThemeChanged += OnThemeChanged;

            // 启动定时器（每秒刷新时间与在线设备数）
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();
        }

        internal void RefreshCurrentUserDisplay()
        {
            CurrentUserName.Text = App.CurrentUser?.Name ?? "";
            CurrentUserRole.Text = App.CurrentUser?.Role ?? "";
        }

        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (++_statusTimerTicks % 5 != 0) return;
            UpdateStatusBar();
            _ = RefreshPendingSyncStatusAsync();
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

        private void OnThemeChanged(AppTheme theme)
        {
            if (Dispatcher.CheckAccess())
                UpdateThemeToggle();
            else
                Dispatcher.BeginInvoke(new Action(UpdateThemeToggle));
        }

        private void UpdateThemeToggle()
        {
            bool dark = ThemeManager.Current == AppTheme.Dark;
            ThemeToggleButton.Content = dark ? "\uE706" : "\uE708";
            ThemeToggleButton.ToolTip = dark ? "切换为浅色模式" : "切换为深色模式";
        }

        // ===== 导航按钮点击事件 =====

        private void NavDashboard_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new DashboardPage());
        }

        private void NavUserManage_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new UserManagePage());
        }

        private void NavTeacherManage_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new TeacherManagePage());
        }

        private void NavPermission_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new PermissionPage());
        }

        /// <summary>角色权限页（教师只读，管理员可编辑）</summary>
        private void NavRolePermission_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new RolePermissionPage());
        }

        private void NavClassManage_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new ClassManagePage());
        }

        private void NavDevice_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new CabinetManagePage());
        }

        /// <summary>指纹库页（采集-存储-分配解耦的指纹模板管理）</summary>
        private void NavFingerprintTemplate_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new FingerprintTemplatePage());
        }

        private void NavSystemLog_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton(sender);
            NavigateToPage(new SystemLogPage());
        }

        private void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new ChangePasswordWindow { Owner = this };
            window.ShowDialog();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e) => ShowAbout();

        private void BrandLogo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ShowAbout();

        private void ShowAbout()
        {
            var window = new AboutWindow { Owner = this };
            window.ShowDialog();
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确认退出当前账号？", "退出登录",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                App.OperationLogService.Write("登录", "退出登录",
                    target: App.CurrentUser?.UserId, result: "success");
            }
            catch { /* ignore */ }

            _loggingOut = true;
            App.CurrentUser = null;
            new LoginWindow().Show();
            Close();
        }

        private bool _syncingToSd;

        /// <summary>状态栏：立即将本机业务库 + 指纹模板同步到 SD。</summary>
        private async void SyncToSdButton_Click(object sender, RoutedEventArgs e)
        {
            if (_syncingToSd) return;
            if (!App.SdStorageService.IsAvailable)
            {
                SyncStatusText.Text = "SD 不可用，无法同步";
                AppToast.Warning("根节点 SD 不可用，请检查通讯链路");
                return;
            }

            _syncingToSd = true;
            SyncToSdButton.IsEnabled = false;
            SyncStatusText.Text = "正在同步…";
            try
            {
                var progress = new Progress<string>(msg =>
                {
                    SyncStatusText.Text = msg;
                });
                var result = await Task.Run(async () =>
                    await App.SdBusinessSyncService.PushBusinessToSdAsync(progress, timeoutMs: 8000));

                SyncStatusText.Text = result.Success
                    ? (string.IsNullOrWhiteSpace(result.Message) ? "同步完成" : result.Message)
                    : (string.IsNullOrWhiteSpace(result.Message) ? "同步失败" : result.Message);

                try
                {
                    App.OperationLogService.Write("系统", "立即同步到 SD",
                        result: result.Success ? "success" : "fail",
                        detail: result.Message);
                }
                catch { }

                if (result.Success)
                    AppToast.Success(string.IsNullOrWhiteSpace(result.Message) ? "已同步到 SD" : result.Message);
                else
                    AppToast.Error(string.IsNullOrWhiteSpace(result.Message) ? "同步到 SD 失败" : result.Message);
            }
            catch (Exception ex)
            {
                SyncStatusText.Text = "同步异常";
                AppToast.Error("同步到 SD 失败：" + ex.Message);
            }
            finally
            {
                _syncingToSd = false;
                SyncToSdButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 根据当前用户角色控制导航菜单可见性
        /// admin：全部管理菜单
        /// teacher：班级管理和系统日志
        /// student：不允许登录；若已有会话仅保留系统日志
        /// </summary>
        private void ApplyRoleVisibility()
        {
            string role = App.CurrentUser?.Role ?? "student";

            // 默认全部可见
            NavDashboard.Visibility = Visibility.Visible;
            NavUserManage.Visibility = Visibility.Visible;
            NavTeacherManage.Visibility = Visibility.Visible;
            NavPermission.Visibility = Visibility.Visible;
            NavRolePermission.Visibility = Visibility.Visible;
            NavClassManage.Visibility = Visibility.Visible;
            NavDevice.Visibility = Visibility.Visible;
            NavFingerprintTemplate.Visibility = Visibility.Visible;
            NavSystemLog.Visibility = Visibility.Visible;

            switch (role)
            {
                case "admin":
                    // 全部可见
                    break;
                case "teacher":
                    // 教师只从班级工作台维护本班学生，避免进入全局用户和设备管理。
                    NavDashboard.Visibility = Visibility.Collapsed;
                    NavUserManage.Visibility = Visibility.Collapsed;
                    NavTeacherManage.Visibility = Visibility.Collapsed;
                    NavPermission.Visibility = Visibility.Collapsed;
                    NavRolePermission.Visibility = Visibility.Visible;
                    NavDevice.Visibility = Visibility.Collapsed;
                    NavFingerprintTemplate.Visibility = Visibility.Collapsed;
                    break;
                case "student":
                default:
                    // 学生仅见系统日志（包含开锁日志）
                    NavDashboard.Visibility = Visibility.Collapsed;
                    NavUserManage.Visibility = Visibility.Collapsed;
                    NavTeacherManage.Visibility = Visibility.Collapsed;
                    NavPermission.Visibility = Visibility.Collapsed;
                    NavRolePermission.Visibility = Visibility.Collapsed;
                    NavClassManage.Visibility = Visibility.Collapsed;
                    NavDevice.Visibility = Visibility.Collapsed;
                    NavFingerprintTemplate.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>获取当前角色默认应打开的导航按钮（首个可见项）</summary>
        private Button? GetDefaultNavButton()
        {
            if (string.Equals(App.CurrentUser?.Role, "teacher", StringComparison.OrdinalIgnoreCase) &&
                NavClassManage.Visibility == Visibility.Visible)
                return NavClassManage;
            // 按顺序返回首个可见项
            if (NavDashboard.Visibility == Visibility.Visible) return NavDashboard;
            if (NavUserManage.Visibility == Visibility.Visible) return NavUserManage;
            if (NavTeacherManage.Visibility == Visibility.Visible) return NavTeacherManage;
            if (NavPermission.Visibility == Visibility.Visible) return NavPermission;
            if (NavRolePermission.Visibility == Visibility.Visible) return NavRolePermission;
            if (NavClassManage.Visibility == Visibility.Visible) return NavClassManage;
            if (NavDevice.Visibility == Visibility.Visible) return NavDevice;
            if (NavFingerprintTemplate.Visibility == Visibility.Visible) return NavFingerprintTemplate;
            if (NavSystemLog.Visibility == Visibility.Visible) return NavSystemLog;
            return NavSystemLog;
        }

        /// <summary>根据按钮导航到对应页面</summary>
        private void NavigateByButton(Button btn)
        {
            if (btn == NavDashboard) NavigateToPage(new DashboardPage());
            else if (btn == NavUserManage) NavigateToPage(new UserManagePage());
            else if (btn == NavTeacherManage) NavigateToPage(new TeacherManagePage());
            else if (btn == NavPermission) NavigateToPage(new PermissionPage());
            else if (btn == NavRolePermission) NavigateToPage(new RolePermissionPage());
            else if (btn == NavClassManage) NavigateToPage(new ClassManagePage());
            else if (btn == NavDevice) NavigateToPage(new CabinetManagePage());
            else if (btn == NavFingerprintTemplate) NavigateToPage(new FingerprintTemplatePage());
            else if (btn == NavSystemLog) NavigateToPage(new SystemLogPage());
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

        /// <summary>供总览等子页跳转到柜子详情。</summary>
        public void NavigateToCabinetDetail(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            try
            {
                Device? device = App.DeviceService.GetAllDevices()
                    .FirstOrDefault(d => string.Equals(d.DeviceId, deviceId,
                        StringComparison.OrdinalIgnoreCase));
                if (device == null)
                {
                    AppToast.Warning("未找到该柜子，请先刷新设备列表");
                    SelectNavButton(NavDevice);
                    NavigateToPage(new CabinetManagePage());
                    return;
                }
                SelectNavButton(NavDevice);
                NavigateToPage(new DevicePage(device));
                AppToast.Info($"已打开 {device.DisplayIdentity}");
            }
            catch (Exception ex)
            {
                AppToast.Error("打开柜子失败：" + ex.Message);
            }
        }

        /// <summary>打开柜子管理列表。</summary>
        public void NavigateToCabinetList()
        {
            SelectNavButton(NavDevice);
            NavigateToPage(new CabinetManagePage());
        }

        /// <summary>设备连接/断开回调（来自后台线程，需切到 UI 线程刷新）</summary>
        private void OnDeviceConnectionChanged(DeviceClient device)
        {
            QueueStatusBarRefresh();
        }

        private void OnCommunicationStateChanged(CommunicationOperationSnapshot state)
        {
            if (Dispatcher.CheckAccess()) ApplyCommunicationState(state);
            else Dispatcher.BeginInvoke(new Action(() => ApplyCommunicationState(state)));
        }

        private void ApplyCommunicationState(CommunicationOperationSnapshot state)
        {
            string detail = state.IsActive && !string.IsNullOrWhiteSpace(state.Description)
                ? $"{state.DisplayText} · {state.Description}"
                : state.DisplayText;
            CommunicationStatusText.Text = detail;
            CommunicationStatusText.ToolTip = state.IsActive
                ? $"{state.Description}\n目标：{state.TargetDeviceId}\n开始：{state.StartedAt:HH:mm:ss}"
                : "当前没有主动通讯事务";
            CommunicationStatusDot.Fill = FindResource(state.Mode switch
            {
                CommunicationMode.Ota => "WarningBrush",
                CommunicationMode.Enrollment => "PrimaryBrush",
                CommunicationMode.Synchronizing => "PrimaryBrush",
                _ => "SuccessBrush"
            }) as System.Windows.Media.Brush;
        }

        /// <summary>Mesh 链路连接状态变化回调（来自后台线程）</summary>
        private void OnMeshConnectionChanged(bool connected)
        {
            QueueStatusBarRefresh();
        }

        private void QueueStatusBarRefresh()
        {
            if (Interlocked.Exchange(ref _statusRefreshQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _statusRefreshQueued, 0);
                if (IsLoaded) UpdateStatusBar();
            }), DispatcherPriority.Background);
        }

        /// <summary>刷新底部状态栏：Mesh 链路状态、SD 数据状态、在线设备数、传输类型、当前时间</summary>
        private void UpdateStatusBar()
        {
            // Mesh 链路状态
            try
            {
                bool connected = App.MeshBridge.IsConnected;
                List<Device> cabinets = App.DeviceService.GetAllDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .ToList();
                int onlineCabinets = cabinets.Count(device => device.IsOnline);
                int totalCabinets = cabinets.Count;
                int transportIdentities = App.MeshBridge.GetOnlineDevices().Count;
                OnlineDeviceCount.Text = onlineCabinets.ToString();
                OnlineDeviceCount.ToolTip =
                    $"业务柜机：在线 {onlineCabinets} / 总数 {totalCabinets}；" +
                    $"通讯身份 {transportIdentities}（仅用于诊断）；收包 {App.MeshBridge.ReceivedCount}";

                if (connected)
                {
                    MeshStatusText.Text = IsDirectUart ? "柜机直连已连接" : "组网U盘已连接";
                    MeshStatusDot.Fill = FindResource("SuccessBrush") as System.Windows.Media.Brush;
                }
                else
                {
                    MeshStatusText.Text = IsDirectUart ? "柜机直连已断开" : "组网U盘未连接";
                    MeshStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
                }

                // 传输类型显示
                TransportTypeText.Text = IsDirectUart
                    ? $"{ConfigHelper.Current.UartSerialPortName} · 单柜"
                    : App.MeshBridge.CurrentType switch
                    {
                        TransportType.UsbSerial => $"{ConfigHelper.Current.MeshSerialPortName} · 组网",
                        TransportType.TcpClient => "TCP 客户端",
                        TransportType.TcpServer => "TCP 服务端",
                        _ => "未启动"
                    };
                OnlineDeviceLabel.Text = IsDirectUart ? "当前柜机" : "在线柜子";

                // SD 数据状态：就绪 / 降级模式 / 未连接
                UpdateRootDataStatus();
            }
            catch
            {
                MeshStatusText.Text = "链路未启动";
                MeshStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
            }

            // 当前时间
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private async Task RefreshPendingSyncStatusAsync(bool force = false)
        {
            if (_pendingSyncStatusLoading) return;
            DateTime now = DateTime.UtcNow;
            if (!force && now - _lastPendingSyncStatusRefreshUtc < TimeSpan.FromSeconds(5))
                return;

            _pendingSyncStatusLoading = true;
            _lastPendingSyncStatusRefreshUtc = now;
            try
            {
                (int open, int failed) = await Task.Run(
                    App.CabinetSyncQueueService.CountOpenAndFailed);
                if (!IsLoaded) return;
                ApplyPendingSyncStatus(open, failed);
            }
            catch
            {
                if (IsLoaded) PendingSyncText.Text = "待同步 —";
            }
            finally
            {
                _pendingSyncStatusLoading = false;
            }
        }

        private void ApplyPendingSyncStatus(int open, int failed)
        {
            PendingSyncText.Text = open == 0 ? "待同步 0" : $"待同步 {open}";
            PendingSyncDot.Fill = FindResource(
                open == 0 ? "SuccessBrush" : failed > 0 ? "DangerBrush" : "WarningBrush")
                as System.Windows.Media.Brush;
            PendingSyncButton.ToolTip = open == 0
                ? "无待同步任务。用户可多指纹入库，并按柜选择下发一枚或多枚。"
                : failed > 0
                    ? $"有 {open} 项待处理（含 {failed} 项失败），点击查看并重试"
                    : $"有 {open} 项待下发到柜机，点击查看队列";
        }

        private void PendingSyncButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new SyncQueueWindow { Owner = this };
            window.ShowDialog();
            _ = RefreshPendingSyncStatusAsync(force: true);
        }

        /// <summary>
        /// 根据 SdStorageService 状态更新 SD 数据指示灯：
        ///   SD 就绪 → 绿色 "根节点数据可用"
        ///   SD 不可用（降级模式） → 黄色 "本地缓存模式"
        ///   未连接根节点 → 红色 "根节点未连接"
        /// </summary>
        private void UpdateRootDataStatus()
        {
            var sd = App.SdStorageService;
            if (IsDirectUart)
            {
                RootDataStatusText.Text = "本机应急数据";
                RootDataStatusDot.Fill = FindResource("WarningBrush") as System.Windows.Media.Brush;
                return;
            }
            if (sd.IsAvailable)
            {
                RootDataStatusText.Text = "根节点数据可用";
                RootDataStatusDot.Fill = FindResource("SuccessBrush") as System.Windows.Media.Brush;
            }
            else if (sd.IsRootConnected && sd.IsStorageReady == false)
            {
                // 根节点在线但 SD 卡未就绪：降级模式
                RootDataStatusText.Text = "本地缓存模式";
                RootDataStatusDot.Fill = FindResource("WarningBrush") as System.Windows.Media.Brush;
            }
            else if (sd.IsRootConnected)
            {
                // 根节点在线但 SD 状态未知：等待 SD 状态上报
                RootDataStatusText.Text = "SD 状态未知";
                RootDataStatusDot.Fill = FindResource("WarningBrush") as System.Windows.Media.Brush;
            }
            else
            {
                RootDataStatusText.Text = "根节点未连接";
                RootDataStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_loggingOut && !App.ExitApproved)
            {
                e.Cancel = true;
                base.OnClosing(e);
                Dispatcher.BeginInvoke(new Action(() => App.RequestShutdown(this)));
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            App.MeshBridge.DeviceConnected -= OnDeviceConnectionChanged;
            App.MeshBridge.DeviceDisconnected -= OnDeviceConnectionChanged;
            App.MeshBridge.ConnectionChanged -= OnMeshConnectionChanged;
            App.CommunicationCoordinator.StateChanged -= OnCommunicationStateChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;

            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Tick -= StatusTimer_Tick;
                _statusTimer = null;
            }

            base.OnClosed(e);

            if (!_loggingOut && Application.Current?.Dispatcher.HasShutdownStarted == false)
                Application.Current.Shutdown();
        }
    }
}
