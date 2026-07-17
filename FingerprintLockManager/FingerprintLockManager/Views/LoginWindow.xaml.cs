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
            UpdateLinkStatus();
            UserIdBox.Focus();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            App.MeshBridge.ConnectionChanged -= OnConnectionChanged;
            App.MessageHandler.OnRootDeviceRegistered -= OnRootRegistered;
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
                HintText.Text = "请输入用户ID和密码";
                HintText.Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush;
                return;
            }

            if (!App.SdStorageService.IsAvailable)
            {
                HintText.Text = "根节点数据服务尚未连接";
                HintText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush;
                return;
            }

            _loggingIn = true;
            LoginButton.IsEnabled = false;
            LoginButton.Content = "验证中";
            HintText.Text = "验证中";
            HintText.Foreground = FindResource("SubTextBrush") as System.Windows.Media.Brush;
            User? user = null;
            try
            {
                user = await Task.Run(() => App.AuthService.Login(userId, password));
            }
            catch (RootDataUnavailableException ex)
            {
                HintText.Text = ex.Message;
                HintText.Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush;
            }
            finally
            {
                _loggingIn = false;
                LoginButton.IsEnabled = true;
                LoginButton.Content = "登录";
            }

            if (user == null)
            {
                if (HintText.Text == "验证中") HintText.Text = "用户 ID 或密码错误";
                if (HintText.Text == "用户 ID 或密码错误")
                    HintText.Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush;
                PasswordBox.Clear();
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
        private void OnRootRegistered(string deviceId) =>
            Dispatcher.BeginInvoke(new Action(UpdateLinkStatus));

        private void UpdateLinkStatus()
        {
            bool available = App.SdStorageService.IsAvailable;
            LinkStatusText.Text = available ? "根节点数据已连接" : "等待根节点";
            LinkStatusDot.Fill = FindResource(
                available ? "SuccessBrush" : "DangerBrush") as System.Windows.Media.Brush;
            if (!_loggingIn)
            {
                HintText.Text = available ? "请输入账号信息" : "根节点数据服务尚未连接";
                HintText.Foreground = FindResource(
                    available ? "SubTextBrush" : "WarningBrush") as System.Windows.Media.Brush;
            }
        }
    }
}
