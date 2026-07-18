using System.Windows;

namespace FingerprintLockManager
{
    public partial class ChangePasswordWindow : Window
    {
        private bool _saving;

        public ChangePasswordWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => OldPasswordBox.Focus();
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_saving || App.CurrentUser == null) return;

            string oldPassword = OldPasswordBox.Password;
            string newPassword = NewPasswordBox.Password;
            if (!PasswordHelper.IsPasswordAcceptable(newPassword))
            {
                ShowError(PasswordHelper.PasswordRequirement);
                return;
            }
            if (newPassword != ConfirmPasswordBox.Password)
            {
                ShowError("两次输入的新密码不一致");
                return;
            }
            if (oldPassword == newPassword)
            {
                ShowError("新密码不能与当前密码相同");
                return;
            }

            _saving = true;
            SaveButton.IsEnabled = false;
            SaveButton.Content = "正在更新";
            HintText.Text = "正在写入根节点";
            try
            {
                bool changed = await Task.Run(() => App.AuthService.ChangePassword(
                    App.CurrentUser.UserId, oldPassword, newPassword));
                if (!changed)
                {
                    ShowError("当前密码不正确，或根节点未能保存新密码");
                    return;
                }

                MessageBox.Show("密码已更新，请使用新密码登录。", "修改成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (RootDataUnavailableException ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                _saving = false;
                SaveButton.IsEnabled = true;
                SaveButton.Content = "更新密码";
            }
        }

        private void ShowError(string message)
        {
            HintText.Text = message;
            HintText.Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
