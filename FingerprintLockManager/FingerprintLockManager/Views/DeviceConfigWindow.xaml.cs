using System.Windows;

namespace FingerprintLockManager
{
    /// <summary>
    /// 设备配置窗口（AP模式）
    /// 上位机作为 TCP 客户端连接 ESP32 AP 热点（默认 192.168.4.1:8888）
    /// 读取/修改/下发设备配置，重启设备
    /// </summary>
    public partial class DeviceConfigWindow : Window
    {
        /// <summary>AP 模式配置客户端</summary>
        private DeviceConfigClient? _client;

        public DeviceConfigWindow()
        {
            InitializeComponent();
        }

        /// <summary>窗口加载：初始化默认 AP 地址并显示未连接状态</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApIpBox.Text = ConfigHelper.Current.ApDeviceIp;
            ApPortBox.Text = ConfigHelper.Current.ApDevicePort.ToString();

            // 用本机服务器信息预填
            ServerIpBox.Text = GetLocalIp();
            ServerPortBox.Text = ConfigHelper.Current.TcpPort.ToString();

            UpdateConnectionStatus();
        }

        /// <summary>窗口关闭：断开连接并释放资源</summary>
        private void Window_Closed(object sender, EventArgs e)
        {
            try
            {
                if (_client != null)
                {
                    _client.MessageReceived -= OnMessageReceived;
                    _client.Disconnected -= OnDisconnected;
                    _client.Disconnect();
                    _client.Dispose();
                    _client = null;
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>连接按钮：连接 ESP32 AP</summary>
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null && _client.IsConnected)
            {
                // 已连接则断开
                _client.Disconnect();
                UpdateConnectionStatus();
                return;
            }

            string ip = ApIpBox.Text?.Trim() ?? "";
            if (!int.TryParse(ApPortBox.Text?.Trim(), out int port) || port <= 0)
            {
                MessageBox.Show("请输入有效的端口号", "提示");
                return;
            }

            ConnectButton.IsEnabled = false;
            ConnectButton.Content = "连接中...";

            try
            {
                _client = new DeviceConfigClient();
                _client.MessageReceived += OnMessageReceived;
                _client.Disconnected += OnDisconnected;
                await _client.ConnectAsync(ip, port);

                UpdateConnectionStatus();
                // 连接成功后自动读取配置
                ReadConfig();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接失败：{ex.Message}\n请确认已连接 ESP32 AP 热点。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ConnectButton.IsEnabled = true;
                ConnectButton.Content = "连接设备";
            }
        }

        /// <summary>读取配置按钮</summary>
        private void ReadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            ReadConfig();
        }

        /// <summary>下发配置按钮</summary>
        private void WriteConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            var config = BuildConfigFromForm();
            var msg = Message.Create(Protocol.CmdWriteConfig, config.DeviceId ?? "", config);
            _client!.Send(msg);

            MessageBox.Show("配置已下发，等待设备确认...", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>重启设备按钮</summary>
        private void RebootButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            var result = MessageBox.Show("确认重启设备？重启期间连接将断开。", "确认重启",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            var msg = Message.Create(Protocol.CmdReboot, DeviceIdBox.Text?.Trim() ?? "");
            _client!.Send(msg);
        }

        /// <summary>关闭按钮</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // ===== 通信处理 =====

        /// <summary>读取设备配置</summary>
        private void ReadConfig()
        {
            if (_client == null || !_client.IsConnected) return;
            var msg = Message.Create(Protocol.CmdReadConfig, "");
            _client.Send(msg);
        }

        /// <summary>收到消息回调（来自后台线程，需切到 UI 线程）</summary>
        private void OnMessageReceived(Message msg)
        {
            Dispatcher.BeginInvoke(new Action(() => HandleMessage(msg)));
        }

        /// <summary>处理收到的消息</summary>
        private void HandleMessage(Message msg)
        {
            if (msg == null) return;

            var cmdType = Protocol.ToCommandType(msg.Cmd);
            if (cmdType == null) return;

            switch (cmdType.Value)
            {
                case CommandType.ConfigResponse:
                    // 配置读取响应：填充表单
                    FillConfigFromMessage(msg);
                    break;

                case CommandType.ConfigSaved:
                    MessageBox.Show("设备配置保存成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }

        /// <summary>连接断开回调</summary>
        private void OnDisconnected()
        {
            Dispatcher.BeginInvoke(new Action(UpdateConnectionStatus));
        }

        /// <summary>从消息中填充配置到表单</summary>
        private void FillConfigFromMessage(Message msg)
        {
            try
            {
                // 序列化 data 后反序列化为 DeviceConfig
                string json = msg.Data != null ? JsonHelper.Serialize(msg.Data) : "{}";
                var config = JsonHelper.Deserialize<DeviceConfig>(json);
                if (config == null) return;

                if (!string.IsNullOrEmpty(config.DeviceId)) DeviceIdBox.Text = config.DeviceId;
                if (config.DeviceName != null) DeviceNameBox.Text = config.DeviceName;
                if (config.WifiSsid != null) WifiSsidBox.Text = config.WifiSsid;
                if (config.WifiPassword != null) WifiPasswordBox.Text = config.WifiPassword;
                if (config.ServerIp != null) ServerIpBox.Text = config.ServerIp;
                if (config.ServerPort > 0) ServerPortBox.Text = config.ServerPort.ToString();
                StaticIpCheckBox.IsChecked = config.StaticIpEnable;
                if (config.StaticIp != null) StaticIpBox.Text = config.StaticIp;
                if (config.Gateway != null) GatewayBox.Text = config.Gateway;
                if (config.Subnet != null) SubnetBox.Text = config.Subnet;
            }
            catch
            {
                // 忽略解析异常
            }
        }

        /// <summary>从表单构建 DeviceConfig</summary>
        private DeviceConfig BuildConfigFromForm()
        {
            int.TryParse(ServerPortBox.Text?.Trim(), out int serverPort);
            return new DeviceConfig
            {
                DeviceId = DeviceIdBox.Text?.Trim() ?? "",
                DeviceName = DeviceNameBox.Text?.Trim() ?? "",
                WifiSsid = WifiSsidBox.Text?.Trim() ?? "",
                WifiPassword = WifiPasswordBox.Text?.Trim() ?? "",
                ServerIp = ServerIpBox.Text?.Trim() ?? "",
                ServerPort = serverPort,
                StaticIpEnable = StaticIpCheckBox.IsChecked == true,
                StaticIp = StaticIpBox.Text?.Trim() ?? "",
                Gateway = GatewayBox.Text?.Trim() ?? "",
                Subnet = SubnetBox.Text?.Trim() ?? ""
            };
        }

        /// <summary>更新连接状态显示</summary>
        private void UpdateConnectionStatus()
        {
            bool connected = _client != null && _client.IsConnected;

            if (connected)
            {
                StatusDot.Fill = FindResource("SuccessBrush") as System.Windows.Media.Brush;
                StatusText.Text = "已连接";
                ConnectButton.Content = "断开";
                ConnectButton.IsEnabled = true;
                ReadConfigButton.IsEnabled = true;
            }
            else
            {
                StatusDot.Fill = FindResource("DangerBrush") as System.Windows.Media.Brush;
                StatusText.Text = "未连接";
                ConnectButton.Content = "连接设备";
                ConnectButton.IsEnabled = true;
                ReadConfigButton.IsEnabled = false;
            }
        }

        /// <summary>确认已连接，未连接时提示</summary>
        private bool EnsureConnected()
        {
            if (_client == null || !_client.IsConnected)
            {
                MessageBox.Show("请先连接设备", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        /// <summary>获取本机局域网 IP（用于预填服务器IP）</summary>
        private static string GetLocalIp()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var addr in host.AddressList)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return addr.ToString();
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return "192.168.1.100";
        }
    }
}
