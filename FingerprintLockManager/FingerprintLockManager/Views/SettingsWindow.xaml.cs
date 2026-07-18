using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var cfg = ConfigHelper.Current;
            TransportTypeBox.ItemsSource = new[]
            {
                nameof(TransportType.UsbSerial),
                nameof(TransportType.TcpClient),
                nameof(TransportType.TcpServer)
            };
            TransportTypeBox.SelectedItem = cfg.TransportType;
            SerialPortBox.ItemsSource = SerialPort.GetPortNames().OrderBy(n => n).ToList();
            if (!string.IsNullOrWhiteSpace(cfg.SerialPortName) &&
                !SerialPortBox.Items.Contains(cfg.SerialPortName))
            {
                var ports = SerialPortBox.ItemsSource as List<string> ?? new List<string>();
                ports.Insert(0, cfg.SerialPortName);
                SerialPortBox.ItemsSource = ports;
            }
            SerialPortBox.Text = cfg.SerialPortName;
            BaudRateBox.Text = cfg.SerialBaudRate.ToString();
            TcpHostBox.Text = cfg.TcpClientHost;
            TcpClientPortBox.Text = cfg.TcpClientPort.ToString();
            TcpServerPortBox.Text = cfg.TcpServerPort.ToString();
            OfflineTimeoutBox.Text = cfg.OfflineTimeoutSeconds.ToString();
            HmacEnabledBox.IsChecked = cfg.HmacEnabled;
            HmacKeyBox.Password = cfg.HmacKey ?? "";
            UpdateFieldVisibility();
        }

        private void TransportTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFieldVisibility();
        }

        private void UpdateFieldVisibility()
        {
            string type = TransportTypeBox.SelectedItem as string ?? "UsbSerial";
            SerialPanel.Visibility = type == nameof(TransportType.UsbSerial)
                ? Visibility.Visible : Visibility.Collapsed;
            TcpClientPanel.Visibility = type == nameof(TransportType.TcpClient)
                ? Visibility.Visible : Visibility.Collapsed;
            TcpServerPanel.Visibility = type == nameof(TransportType.TcpServer)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(BaudRateBox.Text, out int baud) || baud <= 0)
            {
                MessageBox.Show("波特率无效", "提示");
                return;
            }
            if (!int.TryParse(TcpClientPortBox.Text, out int tcpClientPort) || tcpClientPort <= 0)
            {
                MessageBox.Show("TCP 客户端端口无效", "提示");
                return;
            }
            if (!int.TryParse(TcpServerPortBox.Text, out int tcpServerPort) || tcpServerPort <= 0)
            {
                MessageBox.Show("TCP 服务端端口无效", "提示");
                return;
            }
            if (!int.TryParse(OfflineTimeoutBox.Text, out int offline) || offline <= 0)
            {
                MessageBox.Show("离线超时无效", "提示");
                return;
            }

            var cfg = new AppConfig
            {
                TransportType = TransportTypeBox.SelectedItem as string ?? "UsbSerial",
                SerialPortName = SerialPortBox.Text?.Trim() ?? "",
                SerialBaudRate = baud,
                TcpClientHost = TcpHostBox.Text?.Trim() ?? "192.168.4.1",
                TcpClientPort = tcpClientPort,
                TcpServerPort = tcpServerPort,
                OfflineTimeoutSeconds = offline,
                HmacEnabled = HmacEnabledBox.IsChecked == true,
                HmacKey = HmacKeyBox.Password ?? "",
                ApDeviceIp = ConfigHelper.Current.ApDeviceIp,
                ApDevicePort = ConfigHelper.Current.ApDevicePort,
                TcpPort = ConfigHelper.Current.TcpPort
            };

            ConfigHelper.Save(cfg);
            try
            {
                App.MeshBridge.Stop();
                App.MeshBridge.Start(cfg.ToTransportConfig());
                StatusText.Text = "配置已保存并重新连接链路";
                MessageBox.Show("配置已保存，链路已按新参数重启。", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"配置已保存，但重启链路失败：{ex.Message}\n请重启应用。",
                    "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
