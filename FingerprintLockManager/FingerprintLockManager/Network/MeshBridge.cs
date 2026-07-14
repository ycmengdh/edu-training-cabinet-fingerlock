namespace FingerprintLockManager
{
    /// <summary>
    /// 传输链路配置（MeshBridge 启动参数）
    /// </summary>
    public class TransportConfig
    {
        /// <summary>传输类型</summary>
        public TransportType Type { get; set; } = TransportType.UsbSerial;

        /// <summary>串口名（UsbSerial 用，如 COM3 / /dev/ttyUSB0）</summary>
        public string PortName { get; set; } = "";

        /// <summary>波特率（UsbSerial 用，默认 2Mbps）</summary>
        public int BaudRate { get; set; } = SerialTransport.DefaultBaudRate;

        /// <summary>目标主机（TcpClient 用，根节点 AP IP，默认 192.168.4.1）</summary>
        public string Host { get; set; } = "192.168.4.1";

        /// <summary>目标端口（TcpClient/TcpServer 用，默认 8888）</summary>
        public int Port { get; set; } = 8888;
    }

    /// <summary>
    /// Mesh 桥接管理器
    /// 持有 ITransport 实例，根据配置在 USB 串口 / TCP 客户端 / TCP 服务端之间切换；
    /// 维护 Dictionary[string, DeviceClient] 按 device_id 管理逻辑设备；
    /// 统一发送 API（Send/Broadcast/SendToDevice），统一接收（LineReceived → 解析 Message → 更新 _devices → 触发 MessageReceived）。
    /// 上层 App 通过 MessageReceived 事件将消息交给 MessageHandler 处理。
    /// </summary>
    public class MeshBridge
    {
        private readonly object _devicesLock = new object();
        private readonly Dictionary<string, DeviceClient> _devices = new Dictionary<string, DeviceClient>();
        private ITransport? _transport;

        /// <summary>当前传输实例</summary>
        public ITransport? Transport => _transport;

        /// <summary>链路是否已连接</summary>
        public bool IsConnected => _transport?.IsConnected ?? false;

        /// <summary>当前传输类型</summary>
        public TransportType? CurrentType { get; private set; }

        /// <summary>
        /// 所有已发现的逻辑设备（返回列表副本，保证线程安全）
        /// </summary>
        public List<DeviceClient> Devices
        {
            get
            {
                lock (_devicesLock)
                {
                    return new List<DeviceClient>(_devices.Values);
                }
            }
        }

        /// <summary>设备首次发现事件（新 device_id 出现时触发）</summary>
        public event Action<DeviceClient>? DeviceConnected;

        /// <summary>设备离线/移除事件</summary>
        public event Action<DeviceClient>? DeviceDisconnected;

        /// <summary>收到设备消息事件（App 订阅后调用 MessageHandler.HandleMessage）</summary>
        public event Action<DeviceClient?, Message>? MessageReceived;

        /// <summary>链路连接状态变化事件（参数为是否已连接）</summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>
        /// 根据传输配置启动对应 ITransport 实现
        /// </summary>
        /// <param name="config">传输配置</param>
        public void Start(TransportConfig config)
        {
            if (_transport != null) return;

            _transport = CreateTransport(config);
            CurrentType = config.Type;
            _transport.LineReceived += OnLineReceived;

            // 订阅连接状态变化（TCP 传输支持，串口无此事件）
            if (_transport is TcpClientTransport tcpClient)
            {
                tcpClient.ConnectionChanged += OnConnectionChanged;
            }
            else if (_transport is TcpServerTransport tcpServer)
            {
                tcpServer.ConnectionChanged += OnConnectionChanged;
            }

            _transport.Start();

            // 串口启动即视为链路就绪
            if (_transport is SerialTransport)
            {
                OnConnectionChanged(true);
            }
        }

        /// <summary>停止桥接器并释放传输资源</summary>
        public void Stop()
        {
            if (_transport == null) return;

            _transport.LineReceived -= OnLineReceived;
            if (_transport is TcpClientTransport tcpClient)
            {
                tcpClient.ConnectionChanged -= OnConnectionChanged;
            }
            else if (_transport is TcpServerTransport tcpServer)
            {
                tcpServer.ConnectionChanged -= OnConnectionChanged;
            }

            try { _transport.Stop(); } catch { }
            _transport = null;
            CurrentType = null;

            // 标记所有设备离线
            List<DeviceClient> snapshot;
            lock (_devicesLock)
            {
                snapshot = new List<DeviceClient>(_devices.Values);
                _devices.Clear();
            }
            foreach (var d in snapshot)
            {
                d.IsOnline = false;
                DeviceDisconnected?.Invoke(d);
            }
            OnConnectionChanged(false);
        }

        /// <summary>
        /// 向指定设备发送消息（经 Root 转发）
        /// </summary>
        /// <param name="deviceId">目标设备 ID</param>
        /// <param name="msg">待发送的消息（DeviceId 会被自动填充）</param>
        /// <returns>发送成功返回 true；未连接或异常返回 false</returns>
        public bool SendToDevice(string deviceId, Message msg)
        {
            if (_transport == null || msg == null) return false;
            msg.DeviceId = deviceId;
            return _transport.Send(JsonHelper.Serialize(msg));
        }

        /// <summary>
        /// 统一发送 API：构造消息并发送到指定设备
        /// </summary>
        /// <param name="deviceId">目标设备 ID</param>
        /// <param name="cmd">命令字符串</param>
        /// <param name="data">附加数据，可为 null</param>
        /// <returns>发送成功返回 true；否则返回 false</returns>
        public bool Send(string deviceId, string cmd, object? data = null)
        {
            var msg = Message.Create(cmd, deviceId, data);
            return SendToDevice(deviceId, msg);
        }

        /// <summary>
        /// 向所有在线设备广播消息（DeviceId 置空表示广播，由 Root 转发给所有节点）
        /// </summary>
        /// <param name="msg">待广播的消息</param>
        /// <returns>发送成功返回 true；否则返回 false</returns>
        public bool Broadcast(Message msg)
        {
            if (_transport == null || msg == null) return false;
            msg.DeviceId = ""; // 空表示广播
            return _transport.Send(JsonHelper.Serialize(msg));
        }

        /// <summary>
        /// 获取在线设备列表
        /// </summary>
        public List<DeviceClient> GetOnlineDevices()
        {
            lock (_devicesLock)
            {
                return _devices.Values.Where(d => d.IsOnline).ToList();
            }
        }

        /// <summary>根据传输配置创建对应 ITransport 实现</summary>
        private static ITransport CreateTransport(TransportConfig config)
        {
            switch (config.Type)
            {
                case TransportType.UsbSerial:
                    return new SerialTransport(config.PortName, config.BaudRate);
                case TransportType.TcpClient:
                    return new TcpClientTransport(config.Host, config.Port);
                case TransportType.TcpServer:
                    return new TcpServerTransport(config.Port);
                default:
                    return new SerialTransport(config.PortName, config.BaudRate);
            }
        }

        /// <summary>收到一行 JSON 的处理：解析消息 → 更新设备 → 触发事件</summary>
        private void OnLineReceived(string line)
        {
            try
            {
                var msg = Message.FromJson(line);
                if (msg == null || string.IsNullOrEmpty(msg.Cmd)) return;

                // 确定消息来源设备 ID：优先 SourceDeviceId（Root 转发场景），其次 DeviceId
                string sourceId = msg.SourceDeviceId;
                if (string.IsNullOrEmpty(sourceId)) sourceId = msg.DeviceId;

                DeviceClient? device = null;
                if (!string.IsNullOrEmpty(sourceId))
                {
                    device = GetOrCreateDevice(sourceId, msg);
                }

                // 触发消息事件，由 App 路由到 MessageHandler
                MessageReceived?.Invoke(device, msg);
            }
            catch
            {
                // 解析异常忽略，避免影响接收循环
            }
        }

        /// <summary>获取或创建逻辑设备，并更新状态</summary>
        private DeviceClient GetOrCreateDevice(string deviceId, Message msg)
        {
            bool isNew = false;
            DeviceClient device;
            lock (_devicesLock)
            {
                if (!_devices.TryGetValue(deviceId, out device))
                {
                    device = new DeviceClient
                    {
                        DeviceId = deviceId,
                        ConnectTime = DateTime.Now,
                        SendCallback = SendViaTransport
                    };
                    _devices[deviceId] = device;
                    isNew = true;
                }
                device.IsOnline = true;
                device.LastSeen = DateTime.Now;
            }

            if (isNew)
            {
                DeviceConnected?.Invoke(device);
            }

            return device;
        }

        /// <summary>DeviceClient 发送回调：经 ITransport 发往 Root</summary>
        private bool SendViaTransport(Message msg)
        {
            if (_transport == null) return false;
            return _transport.Send(JsonHelper.Serialize(msg));
        }

        /// <summary>链路连接状态变化</summary>
        private void OnConnectionChanged(bool connected)
        {
            ConnectionChanged?.Invoke(connected);

            // 链路断开时，标记所有设备离线
            if (!connected)
            {
                List<DeviceClient> snapshot;
                lock (_devicesLock)
                {
                    snapshot = new List<DeviceClient>(_devices.Values);
                }
                foreach (var d in snapshot)
                {
                    if (d.IsOnline)
                    {
                        d.IsOnline = false;
                        DeviceDisconnected?.Invoke(d);
                    }
                }
            }
        }
    }
}
