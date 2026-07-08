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
