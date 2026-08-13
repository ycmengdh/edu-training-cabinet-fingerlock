using System.Windows;

namespace CabinetLock
{
    public partial class MaintenancePasswordWindow : Window
    {
        public MaintenancePasswordWindow()
        {
            InitializeComponent();
            MaintenanceSettings settings = App.MaintenanceService.GetSettings();
            VersionText.Text = $"当前配置版本 {settings.Version} · 默认密码为 112233";
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string pin = NewPinBox.Password;
            if (!MaintenanceSettings.IsValidPin(pin))
            {
                StatusText.Text = "请输入由按键 1-3 组成的 6 位密码";
                return;
            }
            if (!string.Equals(pin, ConfirmPinBox.Password, StringComparison.Ordinal))
            {
                StatusText.Text = "两次输入的密码不一致";
                return;
            }
            SaveButton.IsEnabled = false;
            StatusText.Text = "正在保存到根节点并同步在线柜机";
            try
            {
                MaintenancePasswordUpdateResult result =
                    await App.MaintenanceService.ChangePinAsync(pin);
                StatusText.Text = result.Message;
                if (result.Version > 0)
                {
                    MessageBox.Show(result.Message, "维护密码",
                        MessageBoxButton.OK, result.Success
                            ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    DialogResult = true;
                }
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
