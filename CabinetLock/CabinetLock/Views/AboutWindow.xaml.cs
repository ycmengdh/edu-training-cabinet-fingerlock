using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace CabinetLock
{
    /// <summary>关于本软件：logo、名称、版本、作者联系邮箱。</summary>
    public partial class AboutWindow : BorderlessWindow
    {
        private const string AuthorEmail = "yc_mdh@qq.com";

        public AboutWindow()
        {
            InitializeComponent();
            string product = GetProductName();
            string version = GetVersionText();
            AppNameText.Text = product;
            VersionText.Text = $"版本 {version}";
            AuthorEmailText.Text = AuthorEmail;
            Title = $"关于 - {product}";
        }

        private static string GetProductName()
        {
            var attr = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyProductAttribute>();
            return string.IsNullOrWhiteSpace(attr?.Product)
                ? "实训柜权限管理系统"
                : attr!.Product;
        }

        private static string GetVersionText()
        {
            Version? ver = Assembly.GetExecutingAssembly().GetName().Version;
            if (ver == null) return "1.0.0";
            return ver.Build >= 0
                ? $"{ver.Major}.{ver.Minor}.{ver.Build}"
                : $"{ver.Major}.{ver.Minor}";
        }

        /// <summary>左键：打开系统默认邮件客户端。</summary>
        private void AuthorEmailText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            OpenMailClient();
        }

        private void SendEmailMenuItem_Click(object sender, RoutedEventArgs e) => OpenMailClient();

        private async void CopyEmailMenuItem_Click(object sender, RoutedEventArgs e) =>
            await CopyEmailToClipboardAsync();

        private void OpenMailClient()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"mailto:{AuthorEmail}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"无法打开邮件客户端：{ex.Message}\n\n可右键邮箱选择「复制邮箱」。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task CopyEmailToClipboardAsync()
        {
            Exception? lastError = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, AuthorEmail);
                    data.SetData(DataFormats.Text, AuthorEmail);
                    Clipboard.SetDataObject(data, true);
                    MessageBox.Show("邮箱已复制到剪贴板", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                catch (ExternalException ex)
                {
                    lastError = ex;
                    await Task.Delay(40 + attempt * 20);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break;
                }
            }

            MessageBox.Show(
                $"剪贴板持续被其他程序占用，复制未完成：{lastError?.Message}\n\n请稍后重试。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
