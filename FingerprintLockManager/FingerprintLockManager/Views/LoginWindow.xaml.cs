using System.Windows;
using System.Windows.Input;

namespace FingerprintLockManager
{
    /// <summary>
    /// 登录窗口
    /// 用户ID + 密码登录，验证成功后打开主窗口
    /// </summary>
    public partial class LoginWindow : BorderlessWindow
    {
        private bool _loggingIn;
        private bool _navigatingToMain;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            ContentRendered += OnContentRendered;
            Closed += OnClosed;

            // 立即填充；再在下一 UI 节拍补一次，防止样式/焦点清空
            ApplyDefaultCredentials();
            Dispatcher.BeginInvoke(new Action(ApplyDefaultCredentials),
                System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(ApplyDefaultCredentials),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void ApplyDefaultCredentials()
        {
            if (UserIdBox == null || PasswordBox == null) return;
            UserIdBox.Text = "admin";
            PasswordBox.Password = "admin123";
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.ConnectionChanged += OnConnectionChanged;
            App.MessageHandler.OnRootDeviceRegistered += OnRootRegistered;
            App.SdStorageService.StatusChanged += OnStorageStatusChanged;
            UpdateLinkStatus();
            ApplyDefaultCredentials();
            // 密码框显示为圆点，不是明文 admin123，这是正常的
            LoginButton.Focus();
        }

        private void OnContentRendered(object? sender, EventArgs e)
        {
            ApplyDefaultCredentials();
            ContentRendered -= OnContentRendered;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            App.MeshBridge.ConnectionChanged -= OnConnectionChanged;
            App.MessageHandler.OnRootDeviceRegistered -= OnRootRegistered;
            App.SdStorageService.StatusChanged -= OnStorageStatusChanged;
        }

        /// <summary>登录按钮点击：验证账号密码</summary>
        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await DoLoginAsync();
        }

        /// <summary>密码框回车登录</summary>
        protected override async void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await DoLoginAsync();
            }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// 执行登录验证
        /// </summary>
        private async Task DoLoginAsync()
        {
            if (_loggingIn) return;
            string userId = UserIdBox.Text?.Trim() ?? "";
            string password = PasswordBox.Password ?? "";

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password))
            {
                SetFeedback("请输入用户 ID 和密码后再登录", "DangerBrush");
                if (string.IsNullOrEmpty(userId)) UserIdBox.Focus(); else PasswordBox.Focus();
                return;
            }

            // 账号以本机 business.db 为准（启动页已从 SD 同步或选用本地数据）。
            // 无本机用户且无链路时：仅允许内置管理员；有本机用户时可不依赖 SD 在线。
            bool builtInCredentials =
                App.AuthService.IsBuiltInAdministratorCredentials(userId, password);
            bool hasLocalBusiness = false;
            try { hasLocalBusiness = BusinessDatabase.HasAnyBusinessData(); } catch { }
            bool allowOfflineLocal = hasLocalBusiness ||
                (builtInCredentials && !App.SdStorageService.IsAvailable);

            if (!allowOfflineLocal && !App.MeshBridge.IsConnected)
            {
                SetFeedback($"{App.MeshBridge.TransportDescription} 尚未连接，请返回启动页同步或检查连接设置", "DangerBrush");
                return;
            }

            if (!allowOfflineLocal && !App.SdStorageService.IsRootConnected && !hasLocalBusiness)
            {
                SetFeedback("物理链路已连接，但尚未收到根节点协议响应；请打开“通讯日志”查看收发数据", "WarningBrush");
                return;
            }

            _loggingIn = true;
            LoginButton.IsEnabled = false;
            LoginButton.Content = "正在验证，请稍候…";
            SetFeedback(allowOfflineLocal && !App.SdStorageService.IsAvailable
                ? "正在验证本机业务库账号…"
                : builtInCredentials
                    ? "正在检查用户表；若无用户将自动创建默认管理员…"
                    : "正在从本机业务库验证密码…", "PrimaryBrush");
            User? user = null;
            string? errorFeedback = null;
            try
            {
                user = await Task.Run(() => App.AuthService.Login(userId, password));
            }
            catch (RootDataUnavailableException ex)
            {
                errorFeedback = ex.Message;
            }
            catch (Exception ex)
            {
                errorFeedback = $"登录处理失败：{ex.Message}";
            }
            finally
            {
                _loggingIn = false;
                LoginButton.IsEnabled = true;
                LoginButton.Content = "登录";
            }

            if (user == null)
            {
                SetFeedback(errorFeedback ??
                    (builtInCredentials
                        ? "用户表已有账户，请使用已存在账号登录；默认 admin/admin123 仅在无用户时可用"
                        : "用户 ID 不存在、账号已停用或密码错误"),
                    "DangerBrush");
                PasswordBox.Clear();
                PasswordBox.Focus();
                return;
            }

            // 登录成功：记录当前用户，打开主窗口，关闭登录窗口
            App.CurrentUser = user;
            try
            {
                App.OperationLogService.Write("登录", "登录成功",
                    target: user.UserId, result: "success",
                    detail: $"角色={user.Role}", operatorId: user.UserId, operatorName: user.Name);
            }
            catch { /* ignore */ }

            var main = new MainWindow();
            _navigatingToMain = true;
            main.Show();

            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_navigatingToMain && !App.ExitApproved)
            {
                e.Cancel = true;
                base.OnClosing(e);
                Dispatcher.BeginInvoke(new Action(() => App.RequestShutdown(this)));
                return;
            }
            base.OnClosing(e);
        }

        private void OnConnectionChanged(bool connected) =>
            Dispatcher.BeginInvoke(new Action(UpdateLinkStatus));
        private void OnRootRegistered(string deviceId, bool? storageReady) =>
            Dispatcher.BeginInvoke(new Action(UpdateLinkStatus));
        private void OnStorageStatusChanged() =>
            Dispatcher.BeginInvoke(new Action(UpdateLinkStatus));

        private void UpdateLinkStatus()
        {
            bool physical = App.MeshBridge.IsConnected;
            bool root = App.SdStorageService.IsRootConnected;
            bool storageFailed = App.SdStorageService.IsStorageReady == false;

            if (!physical)
            {
                LinkStatusText.Text = "物理链路未连接";
                LinkStatusDetailText.Text = $"等待 {App.MeshBridge.TransportDescription}";
                LinkStatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
            }
            else if (!root)
            {
                LinkStatusText.Text = "物理链路已连接";
                LinkStatusDetailText.Text = "尚未收到根节点协议帧";
                LinkStatusDot.Fill = FindResource("WarningBrush") as System.Windows.Media.Brush;
            }
            else if (storageFailed)
            {
                LinkStatusText.Text = "根节点通讯正常";
                LinkStatusDetailText.Text = "SD 卡未就绪，默认管理员可登录";
                LinkStatusDot.Fill = FindResource("WarningBrush") as System.Windows.Media.Brush;
            }
            else
            {
                LinkStatusText.Text = "根节点数据已连接";
                LinkStatusDetailText.Text = $"{App.MeshBridge.TransportDescription} 通讯正常";
                LinkStatusDot.Fill = FindResource("SuccessBrush") as System.Windows.Media.Brush;
            }

            if (!_loggingIn)
            {
                if (!physical)
                    SetFeedback("链路未连接；默认管理员仍可登录", "WarningBrush");
                else if (!root)
                    SetFeedback("根节点尚未响应；默认管理员仍可登录", "WarningBrush");
                else if (storageFailed)
                    SetFeedback("SD 卡未就绪；默认管理员仍可登录", "WarningBrush");
                else
                    SetFeedback("通讯和数据服务正常，请输入账号信息", "SubTextBrush");
            }
        }

        private void SetFeedback(string message, string brushKey)
        {
            HintText.Text = message;
            HintText.Foreground = FindResource(brushKey) as System.Windows.Media.Brush;
        }
    }
}
