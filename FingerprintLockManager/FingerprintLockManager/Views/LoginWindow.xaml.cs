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
        public LoginWindow()
        {
            InitializeComponent();
            // 默认焦点在用户ID输入框
            Loaded += (s, e) => UserIdBox.Focus();

            // 数据未从根节点 SD 卡加载完成时，禁用登录并提示等待
            if (!DataStore.Current.IsLoaded)
            {
                LoginButton.IsEnabled = false;
                HintText.Text = "正在连接根节点并加载数据，请稍候...";
                DataStore.Current.Loaded += OnDataLoaded;
            }
        }

        /// <summary>数据加载完成后启用登录（由后台线程触发，需切回 UI 线程）</summary>
        private void OnDataLoaded()
        {
            Dispatcher.Invoke(() =>
            {
                LoginButton.IsEnabled = true;
                HintText.Text = "请输入用户ID和密码登录";
                UserIdBox.Focus();
            });
        }

        /// <summary>登录按钮点击：验证账号密码</summary>
        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            DoLogin();
        }

        /// <summary>密码框回车登录</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                DoLogin();
            }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// 执行登录验证
        /// </summary>
        private void DoLogin()
        {
            string userId = UserIdBox.Text?.Trim() ?? "";
            string password = PasswordBox.Password ?? "";

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password))
            {
                HintText.Text = "请输入用户ID和密码";
                HintText.Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush;
                return;
            }

            // 调用认证服务验证
            var user = App.AuthService.Login(userId, password);
            if (user == null)
            {
                HintText.Text = "用户ID或密码错误";
                HintText.Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush;
                PasswordBox.Clear();
                return;
            }

            // 登录成功：记录当前用户，打开主窗口，关闭登录窗口
            App.CurrentUser = user;

            var main = new MainWindow();
            main.Show();

            this.Close();
        }
    }
}
