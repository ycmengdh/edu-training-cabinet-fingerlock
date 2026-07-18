using System.Windows;
using System.Windows.Input;

namespace FingerprintLockManager
{
    /// <summary>
    /// 登录窗口
    /// 用户ID + 密码登录，验证成功后打开主窗口
    /// </summary>
    public partial class LoginWindow : Window
    {
        private bool _loggingIn;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.ConnectionChanged += OnConnectionChanged;
            App.MessageHandler.OnRootDeviceRegistered += OnRootRegistered;
            App.SdStorageService.StatusChanged += OnStorageStatusChanged;
            UpdateLinkStatus();
            UserIdBox.Focus();
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

        /// <summary>登录前配置根节点连接方式，并按新参数立即重连。</summary>
        private void ConnectionSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow
            {
                Owner = this
            };
            window.ShowDialog();
            UpdateLinkStatus();
        }

        private void CommunicationTestButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new CommunicationTestWindow { Owner = this };
            window.ShowDialog();
            UpdateLinkStatus();
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

            if (!App.MeshBridge.IsConnected)
            {
                SetFeedback($"{App.MeshBridge.TransportDescription} 尚未连接，请检查连接设置或打开通讯测试", "DangerBrush");
                return;
            }

            if (!App.SdStorageService.IsRootConnected)
            {
                SetFeedback("物理链路已连接，但尚未收到根节点协议响应；请打开“通讯测试”查看收发数据", "WarningBrush");
                return;
            }

            if (App.SdStorageService.IsStorageReady == false)
            {
                SetFeedback("根节点通讯正常，但 SD 卡未就绪；登录账号保存在 SD 卡中，暂时无法验证", "WarningBrush");
                return;
            }

            if (!App.SdStorageService.IsAvailable)
            {
                SetFeedback("根节点数据服务尚未连接，请先完成通讯测试", "WarningBrush");
                return;
            }

            _loggingIn = true;
            LoginButton.IsEnabled = false;
            ConnectionSettingsButton.IsEnabled = false;
            CommunicationTestButton.IsEnabled = false;
            LoginButton.Content = "正在验证，请稍候…";
            SetFeedback("正在从根节点读取账号数据并验证密码…", "PrimaryBrush");
            User? user = null;
            try
            {
                user = await Task.Run(() => App.AuthService.Login(userId, password));
            }
            catch (RootDataUnavailableException ex)
            {
                SetFeedback(ex.Message, "DangerBrush");
            }
            catch (Exception ex)
            {
                SetFeedback($"登录处理失败：{ex.Message}", "DangerBrush");
            }
            finally
            {
                _loggingIn = false;
                LoginButton.IsEnabled = true;
                LoginButton.Content = "登录";
                ConnectionSettingsButton.IsEnabled = true;
                CommunicationTestButton.IsEnabled = true;
            }

            if (user == null)
            {
                if (HintText.Text.StartsWith("正在从根节点", StringComparison.Ordinal))
                    SetFeedback("用户 ID 不存在、账号已停用或密码错误", "DangerBrush");
                PasswordBox.Clear();
                PasswordBox.Focus();
                return;
            }

            // 登录成功：记录当前用户，打开主窗口，关闭登录窗口
            App.CurrentUser = user;

            var main = new MainWindow();
            main.Show();

            Close();
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
                LinkStatusDetailText.Text = "SD 卡未就绪，无法登录";
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
                    SetFeedback("链路未连接；可进入“连接设置”选择串口，再用“通讯测试”检查", "WarningBrush");
                else if (!root)
                    SetFeedback("串口已打开，但根节点尚未返回协议数据", "WarningBrush");
                else if (storageFailed)
                    SetFeedback("根节点通讯正常，但 SD 卡未就绪，当前不能读取登录账号", "WarningBrush");
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
