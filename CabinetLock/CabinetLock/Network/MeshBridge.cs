using System.Text;
using System.Threading;

namespace CabinetLock
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

        /// <summary>波特率（UsbSerial 用，默认 921600）</summary>
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
        private readonly object _traceLock = new object();
        private readonly Queue<CommunicationTraceEntry> _recentTrace = new Queue<CommunicationTraceEntry>();
        private ITransport? _transport;
        private Timer? _healthTimer;
        private int _protocolConnectedFlag;
        private int _healthProbeBusy;
        private long _sentCount;
        private long _receivedCount;
        private string _lastSendFailReason = "";
        private DateTime _lastSendFailAt = DateTime.MinValue;
        private string _lastPolicyDenyReason = "";
        private DateTime _lastPolicyDenyAt = DateTime.MinValue;

        private const int MaxTraceEntries = 5000;
        private static readonly TimeSpan RootOfflineTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ProtocolSilenceTimeout = TimeSpan.FromMilliseconds(4500);
        private const int HealthProbeIntervalMs = 1000;

        /// <summary>当前传输实例</summary>
        public ITransport? Transport => _transport;

        /// <summary>链路是否已连接</summary>
        public bool IsConnected => IsPhysicalConnected &&
            Volatile.Read(ref _protocolConnectedFlag) == 1;

        public bool IsPhysicalConnected => _transport?.IsConnected ?? false;

        /// <summary>当前传输类型</summary>
        public TransportType? CurrentType { get; private set; }

        public string TransportDescription => _transport?.Description ?? "链路未启动";

        public string LastTransportError => _transport?.LastError ?? "";

        public long SentCount => Interlocked.Read(ref _sentCount);

        public long ReceivedCount => Interlocked.Read(ref _receivedCount);

        public DateTime? LastSentTime { get; private set; }

        public DateTime? LastReceivedTime { get; private set; }

        public List<CommunicationTraceEntry> RecentTrace
        {
            get
            {
                lock (_traceLock) return _recentTrace.ToList();
            }
        }

        public void ClearTrace()
        {
            lock (_traceLock) _recentTrace.Clear();
        }

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

        /// <summary>通讯测试窗口使用的实时收发与链路诊断记录。</summary>
        public event Action<CommunicationTraceEntry>? TraceAdded;

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
            _transport.PayloadReceived += OnPayloadReceived;
            _transport.ConnectionChanged += OnConnectionChanged;
            _transport.DiagnosticMessage += OnTransportDiagnostic;
            _transport.UnframedDataReceived += OnUnframedDataReceived;

            Interlocked.Exchange(ref _sentCount, 0);
            Interlocked.Exchange(ref _receivedCount, 0);
            LastSentTime = null;
            LastReceivedTime = null;
            Interlocked.Exchange(ref _protocolConnectedFlag, 0);
            RecordTrace(CommunicationDirection.System, "链路", $"启动 {_transport.Description}");

            _transport.Start();
            _healthTimer = new Timer(HealthTimerTick, null, 100, HealthProbeIntervalMs);
        }

        /// <summary>停止桥接器并释放传输资源</summary>
        public void Stop()
        {
            if (_transport == null) return;

            try { _healthTimer?.Dispose(); } catch { }
            _healthTimer = null;

            _transport.LineReceived -= OnLineReceived;
            _transport.PayloadReceived -= OnPayloadReceived;
            _transport.ConnectionChanged -= OnConnectionChanged;
            _transport.DiagnosticMessage -= OnTransportDiagnostic;
            _transport.UnframedDataReceived -= OnUnframedDataReceived;

            try { _transport.Stop(); } catch { }
            RecordTrace(CommunicationDirection.System, "链路", $"停止 {_transport.Description}");
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
            SetProtocolConnected(false);
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
            return SendMessageBinary(msg);
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
            return SendMessageBinary(msg);
        }

        /// <summary>
        /// 获取在线设备列表
        /// </summary>
        public List<DeviceClient> GetOnlineDevices()
        {
            ExpireInactiveDevices(DateTime.Now);
            lock (_devicesLock)
            {
                return _devices.Values.Where(d => d.IsOnline).ToList();
            }
        }

        /// <summary>
        /// 获取所有曾经通讯过的设备（含当前离线）。
        /// 设备页应使用此列表：见过就保留，只更新 IsOnline。
        /// </summary>
        public List<DeviceClient> GetKnownDevices()
        {
            ExpireInactiveDevices(DateTime.Now);
            lock (_devicesLock)
            {
                return new List<DeviceClient>(_devices.Values);
            }
        }

        /// <summary>
        /// 权限事务收到柜机提交确认后，立即更新运行时快照。
        /// 后续 STATUS_RESPONSE 仍会用设备真实上报覆盖该值。
        /// </summary>
        public void MarkPermissionSyncConfirmed(
            string deviceId, uint permissionVersion, int permissionCount)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || permissionVersion == 0) return;
            lock (_devicesLock)
            {
                DeviceClient? device = _devices.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.DeviceId, deviceId,
                        StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(candidate.MeshMac) &&
                     string.Equals(candidate.MeshMac, deviceId,
                         StringComparison.OrdinalIgnoreCase)));
                if (device == null) return;

                device.Status ??= new DeviceRuntimeStatus();
                device.Status.PermissionVersion = permissionVersion;
                device.Status.PermissionCount = Math.Max(0, permissionCount);
                device.LastStatusAt = DateTime.Now;
            }
        }

        public void MarkPermissionVersionConfirmed(string deviceId, uint permissionVersion)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || permissionVersion == 0) return;
            lock (_devicesLock)
            {
                DeviceClient? device = _devices.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.DeviceId, deviceId,
                        StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(candidate.MeshMac) &&
                     string.Equals(candidate.MeshMac, deviceId,
                         StringComparison.OrdinalIgnoreCase)));
                if (device == null) return;
                device.Status ??= new DeviceRuntimeStatus();
                device.Status.PermissionVersion = permissionVersion;
                device.LastStatusAt = DateTime.Now;
            }
        }

        public void MarkFingerprintSyncConfirmed(string deviceId, int fingerprintCount)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_devicesLock)
            {
                DeviceClient? device = _devices.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.DeviceId, deviceId,
                        StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(candidate.MeshMac) &&
                     string.Equals(candidate.MeshMac, deviceId,
                         StringComparison.OrdinalIgnoreCase)));
                if (device == null) return;
                device.Status ??= new DeviceRuntimeStatus();
                device.Status.FingerprintCount = Math.Max(0, fingerprintCount);
                device.LastStatusAt = DateTime.Now;
            }
        }

        public void ForgetDevice(string deviceId, string? meshMac = null)
        {
            if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(meshMac)) return;
            lock (_devicesLock)
            {
                string[] keys = _devices.Where(item =>
                        string.Equals(item.Value.DeviceId, deviceId,
                            StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(meshMac) && string.Equals(
                            item.Value.MeshMac, meshMac, StringComparison.OrdinalIgnoreCase)))
                    .Select(item => item.Key)
                    .ToArray();
                foreach (string key in keys) _devices.Remove(key);
            }
        }

        /// <summary>当前已知设备总数（含离线/根节点，调试用）。</summary>
        public int KnownDeviceCount
        {
            get { lock (_devicesLock) return _devices.Count; }
        }

        /// <summary>在线柜子数（排除真正的根节点）。</summary>
        public int OnlineCabinetCount =>
            GetOnlineDevices().Count(d => !DeviceService.IsTrueRoot(d));

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

        /// <summary>
        /// 收到应用层负载。
        /// 统一协议：外层 A5/5A 帧 + 应用层 B1/0F 二进制信封；
        /// 复杂 data 仍在信封 payload 内以 UTF-8 JSON 承载。
        /// 整包 JSON 仅作旧固件兼容，正常链路不应再出现。
        /// </summary>
        private void OnPayloadReceived(byte[] payload)
        {
            Interlocked.Increment(ref _receivedCount);
            LastReceivedTime = DateTime.Now;
            SetProtocolConnected(true);
            try
            {
                Message? msg = null;
                AppMessage? app = null;

                // 1) 标准二进制信封（主路径）
                if (BinaryMessageCodec.TryDecode(payload, out app) && app != null)
                {
                    msg = AppMessageMapper.ToMessage(app);
                }
                // 2) 帧内可能夹杂噪声：扫描 B1 0F 魔数再解
                else if (TryDecodeBinaryWithResync(payload, out app) && app != null)
                {
                    msg = AppMessageMapper.ToMessage(app);
                    RecordTrace(CommunicationDirection.System, "BIN 重同步",
                        $"offset 找到魔数后解码成功 cmd=0x{app.CmdId:X4}");
                }
                // 3) 遗留整包 JSON（兼容旧固件；新链路应全部走二进制信封）
                else if (payload.Length > 0 && (payload[0] == (byte)'{' || payload[0] == (byte)'['))
                {
                    string line = Encoding.UTF8.GetString(payload);
                    msg = Message.FromJson(line);
                    // 固件 Debug::LOG 旧路径：整包 JSON cmd=LOG，按固件日志展示，不标协议异常。
                    if (msg != null && string.Equals(msg.Cmd, Protocol.CmdDebugLog, StringComparison.OrdinalIgnoreCase))
                    {
                        string logText = FormatDebugLog(msg);
                        RecordTrace(CommunicationDirection.Receive, "固件日志(旧)",
                            logText.Length > 400 ? logText.Substring(0, 400) + "..." : logText);
                    }
                    else
                    {
                        RecordTrace(CommunicationDirection.System, "协议异常(整包JSON)",
                            line.Length > 240 ? line.Substring(0, 240) + "..." : line);
                    }
                }
                else
                {
                    string head = payload.Length == 0 ? "" :
                        BitConverter.ToString(payload, 0, Math.Min(16, payload.Length));
                    RecordTrace(CommunicationDirection.System, "解析失败",
                        $"未知负载 len={payload.Length} head={head}");
                    return;
                }

                if (msg == null || string.IsNullOrEmpty(msg.Cmd))
                {
                    RecordTrace(CommunicationDirection.System, "解析失败", "消息 cmd 为空");
                    return;
                }

                if (app != null)
                {
                    if (string.Equals(msg.Cmd, Protocol.CmdDebugLog, StringComparison.OrdinalIgnoreCase) ||
                        app.CmdId == CmdIds.DebugLog)
                    {
                        string logText = FormatDebugLog(msg);
                        RecordTrace(CommunicationDirection.Receive, "固件日志",
                            logText.Length > 400 ? logText.Substring(0, 400) + "..." : logText);
                    }
                    else
                    {
                        RecordTrace(CommunicationDirection.Receive, "协议 BIN",
                            $"cmd=0x{app.CmdId:X4}({msg.Cmd}) did='{msg.DeviceId}' src='{msg.SourceDeviceId}' mid={msg.MsgId} plen={app.Payload?.Length ?? 0} onlineCab={OnlineCabinetCount}");
                    }
                }

                // 节点唯一身份优先 MAC：
                // 1) source_id（固件/Root 已填 STA MAC）
                // 2) data.mesh_mac
                // 3) 回退 device_id（旧固件兼容）
                string meshMac = NormalizeMac(msg.SourceDeviceId);
                string logicalId = (msg.DeviceId ?? "").Trim();
                bool explicitRoot = false;
                if (msg.Data is Newtonsoft.Json.Linq.JObject dataObj)
                {
                    if (string.IsNullOrEmpty(meshMac))
                        meshMac = NormalizeMac(dataObj["mesh_mac"]?.ToString());
                    if (string.IsNullOrEmpty(logicalId))
                        logicalId = (dataObj["device_id"]?.ToString()
                            ?? dataObj["deviceId"]?.ToString()
                            ?? "").Trim();
                    var rootTok = dataObj["is_root"];
                    if (rootTok != null)
                    {
                        string rs = rootTok.ToString();
                        explicitRoot = rs.Equals("true", StringComparison.OrdinalIgnoreCase) || rs == "1";
                    }
                    string role = dataObj["role"]?.ToString() ?? "";
                    if (role.Equals("cabinet", StringComparison.OrdinalIgnoreCase))
                        explicitRoot = false;
                    if (role.Equals("root", StringComparison.OrdinalIgnoreCase))
                        explicitRoot = true;
                }

                // 字典主键：有 MAC 用 MAC，否则用逻辑 device_id
                string identityKey = !string.IsNullOrEmpty(meshMac) ? meshMac : logicalId;
                if (string.IsNullOrEmpty(msg.DeviceId) && !string.IsNullOrEmpty(logicalId))
                    msg.DeviceId = logicalId;
                if (string.IsNullOrEmpty(msg.SourceDeviceId) && !string.IsNullOrEmpty(meshMac))
                    msg.SourceDeviceId = meshMac;

                DeviceClient? device = null;
                if (!string.IsNullOrEmpty(identityKey))
                {
                    device = GetOrCreateDevice(identityKey, logicalId, meshMac, msg);
                    // 非 REGISTER 包默认不当根节点，防止误过滤柜子
                    if (device != null &&
                        !string.Equals(msg.Cmd, Protocol.CmdRegister, StringComparison.OrdinalIgnoreCase) &&
                        !explicitRoot &&
                        !string.IsNullOrEmpty(device.DeviceId) &&
                        device.DeviceId.Contains("CABINET", StringComparison.OrdinalIgnoreCase))
                    {
                        device.IsRoot = false;
                    }
                    if (device != null && explicitRoot)
                        device.IsRoot = true;
                }
                else
                {
                    RecordTrace(CommunicationDirection.System, "设备忽略",
                        $"消息无 MAC/device_id: cmd={msg.Cmd} mid={msg.MsgId} known={KnownDeviceCount}");
                }

                // Transport guarantees one ordered dispatch stream. Invoke in
                // that order so ACK/status/business frames cannot overtake.
                try
                {
                    MessageReceived?.Invoke(device, msg);
                }
                catch (Exception invEx)
                {
                    RecordTrace(CommunicationDirection.System, "业务回调异常", invEx.Message);
                }
            }
            catch (Exception ex)
            {
                RecordTrace(CommunicationDirection.System, "解析失败", ex.Message);
            }
        }

        /// <summary>在缓冲中扫描 0xB1 0x0F 并尝试解码，兼容帧边界噪声。</summary>
        private static bool TryDecodeBinaryWithResync(byte[] payload, out AppMessage? app)
        {
            app = null;
            if (payload == null || payload.Length < BinaryMessageCodec.HeaderSize) return false;
            for (int i = 0; i <= payload.Length - BinaryMessageCodec.HeaderSize; i++)
            {
                if (payload[i] != BinaryMessageCodec.AppMagicLo ||
                    payload[i + 1] != BinaryMessageCodec.AppMagicHi) continue;
                if (BinaryMessageCodec.TryDecode(payload.AsSpan(i), out app) && app != null)
                    return true;
            }
            return false;
        }

        /// <summary>遗留 LineReceived 路径（仅当传输层同时抛 JSON 字符串时）。</summary>
        private void OnLineReceived(string line)
        {
            // PayloadReceived 已覆盖主路径；此处仅在没有 PayloadReceived 的旧传输上兜底。
            // 当前三个 Transport 都会同时触发 PayloadReceived，因此默认忽略重复 JSON 行，
            // 避免双处理。若 payload 不是 JSON 开头则不会走 LineReceived。
        }

        /// <summary>
        /// 获取或创建设备。
        /// identityKey：优先 MAC（稳定唯一）；logicalDeviceId：业务 device_id（命令路由用）。
        /// </summary>
        private DeviceClient GetOrCreateDevice(
            string identityKey, string logicalDeviceId, string meshMac, Message msg)
        {
            identityKey = (identityKey ?? "").Trim();
            if (string.IsNullOrEmpty(identityKey))
                throw new ArgumentException("identityKey empty", nameof(identityKey));

            logicalDeviceId = (logicalDeviceId ?? "").Trim();
            meshMac = NormalizeMac(meshMac);

            bool becameOnline = false;
            DeviceClient device;
            lock (_devicesLock)
            {
                DeviceClient? existing = null;
                // 1) 主键精确命中（MAC 或旧逻辑 ID）
                if (!_devices.TryGetValue(identityKey, out existing))
                {
                    // 有 MAC 时绝不按逻辑 ID 合并，否则重复 device_id 会把两台
                    // 物理柜机折叠成一台，造成在线状态和命令目标串设备。
                    if (!string.IsNullOrEmpty(meshMac))
                    {
                        existing = _devices.Values.FirstOrDefault(d =>
                            string.Equals(NormalizeMac(d.MeshMac), meshMac, StringComparison.OrdinalIgnoreCase));
                    }
                    // 无 MAC 的旧固件才允许按逻辑 ID 兼容归并。
                    if (existing == null && string.IsNullOrEmpty(meshMac) &&
                        !string.IsNullOrEmpty(logicalDeviceId))
                    {
                        existing = _devices.Values.FirstOrDefault(d =>
                            string.Equals(d.DeviceId, logicalDeviceId, StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (existing == null)
                {
                    device = new DeviceClient
                    {
                        DeviceId = !string.IsNullOrEmpty(logicalDeviceId) ? logicalDeviceId : identityKey,
                        MeshMac = meshMac,
                        ConnectTime = DateTime.Now,
                        SendCallback = SendViaTransport
                    };
                    _devices[identityKey] = device;
                    becameOnline = true;
                    RecordTrace(CommunicationDirection.System, "设备上线",
                        $"key={identityKey} did={device.DeviceId} mac={meshMac} cmd={msg.Cmd}");
                }
                else
                {
                    device = existing;
                    becameOnline = !device.IsOnline;

                    // 若之前用逻辑 ID 做键，现在有了 MAC，则迁移字典键到 MAC
                    string currentKey = _devices.FirstOrDefault(kv => ReferenceEquals(kv.Value, device)).Key
                                        ?? identityKey;
                    if (!string.Equals(currentKey, identityKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _devices.Remove(currentKey);
                        _devices[identityKey] = device;
                    }
                }

                // 业务 device_id：有逻辑名就更新；发送命令仍用 DeviceId
                if (!string.IsNullOrEmpty(logicalDeviceId))
                    device.DeviceId = logicalDeviceId;
                if (!string.IsNullOrEmpty(meshMac))
                    device.MeshMac = meshMac;

                device.IsOnline = true;
                device.LastSeen = DateTime.Now;

                bool isRegister = string.Equals(
                    msg.Cmd, Protocol.CmdRegister, StringComparison.OrdinalIgnoreCase);
                bool isConfigResponse = string.Equals(
                    msg.Cmd, Protocol.CmdConfigResponse, StringComparison.OrdinalIgnoreCase);
                if ((isRegister || isConfigResponse) &&
                    msg.Data is Newtonsoft.Json.Linq.JObject metadata)
                {
                    MergeReportedMetadata(device, metadata);
                }

                // REGISTER 时刷新根节点标记
                if (isRegister && msg.Data is Newtonsoft.Json.Linq.JObject jo)
                {
                    bool isRoot = false;
                    var rootToken = jo["is_root"];
                    if (rootToken != null && rootToken.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                    {
                        string s = rootToken.Type == Newtonsoft.Json.Linq.JTokenType.Boolean
                            ? ((bool)rootToken ? "true" : "false")
                            : (rootToken.ToString() ?? "");
                        isRoot = s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
                    }
                    string role = jo["role"]?.ToString() ?? "";
                    if (role.Equals("cabinet", StringComparison.OrdinalIgnoreCase))
                        isRoot = false;
                    if (role.Equals("root", StringComparison.OrdinalIgnoreCase))
                        isRoot = true;
                    device.IsRoot = isRoot;
                }
                else if (!string.IsNullOrEmpty(device.DeviceId) &&
                         device.DeviceId.Contains("CABINET", StringComparison.OrdinalIgnoreCase))
                {
                    device.IsRoot = false;
                }

                if ((string.Equals(msg.Cmd, Protocol.CmdStatusResponse, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(msg.Cmd, Protocol.CmdStatusReport, StringComparison.OrdinalIgnoreCase)) &&
                    msg.Data is Newtonsoft.Json.Linq.JObject statusData)
                {
                    try
                    {
                        device.Status = statusData.ToObject<DeviceRuntimeStatus>() ?? new DeviceRuntimeStatus();
                        device.LastStatusAt = DateTime.Now;
                    }
                    catch
                    {
                    }
                }
            }

            if (becameOnline)
            {
                // SerialTransport 已把 PayloadReceived 放到线程池；这里在锁外按序通知，
                // 既不会重入串口 I/O，也保证上线事件先于本次收包处理完成。
                try { DeviceConnected?.Invoke(device); } catch { }
            }

            return device;
        }

        private static void MergeReportedMetadata(
            DeviceClient device, Newtonsoft.Json.Linq.JObject data)
        {
            string? name = data["device_name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                device.DeviceName = name.Trim();

            string? firmwareVersion = data["firmware_version"]?.ToString();
            if (!string.IsNullOrWhiteSpace(firmwareVersion))
                device.FirmwareVersion = firmwareVersion.Trim();

            string? hardwareVersion = data["hardware_version"]?.ToString();
            if (!string.IsNullOrWhiteSpace(hardwareVersion))
                device.HardwareVersion = hardwareVersion.Trim();
        }

        /// <summary>规范化 MAC：AA:BB:... 大写；非 MAC 返回空。</summary>
        private static string NormalizeMac(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim().ToUpperInvariant()
                .Replace("-", ":")
                .Replace(" ", "");
            // 已是 AA:BB:CC:DD:EE:FF
            if (s.Length == 17 && s.Count(c => c == ':') == 5) return s;
            // 12 位十六进制无冒号
            string hex = new string(s.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length == 12)
            {
                return string.Join(":", Enumerable.Range(0, 6)
                    .Select(i => hex.Substring(i * 2, 2)));
            }
            return "";
        }

        /// <summary>
        /// 按设备最后一条协议消息判定离线。物理串口仍连接只说明 Root 还在，
        /// 不能证明某个柜子仍在线；柜子必须持续用 HEARTBEAT 刷新 LastSeen。
        /// </summary>
        private void ExpireInactiveDevices(DateTime now)
        {
            List<DeviceClient> expired = new List<DeviceClient>();
            int configuredTimeoutSeconds = ConfigHelper.Current.OfflineTimeoutSeconds;
            TimeSpan cabinetOfflineTimeout = TimeSpan.FromSeconds(
                Math.Clamp(configuredTimeoutSeconds, 10, 3600));
            lock (_devicesLock)
            {
                foreach (var device in _devices.Values)
                {
                    if (!device.IsOnline || device.LastSeen == default) continue;
                    TimeSpan timeout = device.IsRoot
                        ? RootOfflineTimeout
                        : cabinetOfflineTimeout;
                    if (now - device.LastSeen < timeout) continue;

                    device.IsOnline = false;
                    expired.Add(device);
                }
            }

            foreach (var device in expired)
            {
                RecordTrace(CommunicationDirection.System, "设备离线",
                    $"{device.DeviceId} 已 {Math.Max(0, (int)(now - device.LastSeen).TotalSeconds)} 秒无协议消息");
                DeviceDisconnected?.Invoke(device);
            }
        }

        /// <summary>DeviceClient 发送回调：经 ITransport 发往 Root</summary>
        private bool SendViaTransport(Message msg)
        {
            if (_transport == null || msg == null) return false;
            return SendMessageBinary(msg);
        }

        private bool SendMessageBinary(Message msg)
        {
            if (_transport == null)
            {
                RecordTrace(CommunicationDirection.System, "发送失败", "链路尚未启动");
                return false;
            }
            if (!App.CommunicationCoordinator.CanSend(msg.Cmd, out string denyReason))
            {
                DateTime deniedAt = DateTime.Now;
                if (!string.Equals(_lastPolicyDenyReason, denyReason,
                        StringComparison.Ordinal) ||
                    (deniedAt - _lastPolicyDenyAt).TotalSeconds >= 5)
                {
                    _lastPolicyDenyReason = denyReason;
                    _lastPolicyDenyAt = deniedAt;
                    RecordTrace(CommunicationDirection.System, "通讯调度", denyReason);
                }
                return false;
            }
            try
            {
                AppMessage app = AppMessageMapper.ToApp(msg);
                byte[] payload = BinaryMessageCodec.Encode(app);
                bool sent = _transport.SendPayload(payload);
                if (sent)
                {
                    Interlocked.Increment(ref _sentCount);
                    LastSentTime = DateTime.Now;
                    string tableHint = "";
                    if (msg.Data is Newtonsoft.Json.Linq.JObject data &&
                        data["table"] != null)
                    {
                        tableHint = $" table={data["table"]}";
                    }
                    RecordTrace(CommunicationDirection.Transmit, "协议 BIN",
                        $"cmd=0x{app.CmdId:X4}({msg.Cmd}) did={msg.DeviceId} mid={msg.MsgId}" +
                        $" plen={app.Payload?.Length ?? 0}{tableHint}");
                }
                else
                {
                    string reason = string.IsNullOrWhiteSpace(_transport.LastError)
                        ? "物理链路未连接或写入失败"
                        : _transport.LastError;
                    // 去抖：同一原因 1.5s 内只记一条，避免日志被发送失败刷屏
                    DateTime now = DateTime.Now;
                    if (!string.Equals(_lastSendFailReason, reason, StringComparison.Ordinal) ||
                        (now - _lastSendFailAt).TotalMilliseconds >= 1500)
                    {
                        _lastSendFailReason = reason;
                        _lastSendFailAt = now;
                        RecordTrace(CommunicationDirection.System, "发送失败", reason);
                    }
                }
                return sent;
            }
            catch (Exception ex)
            {
                RecordTrace(CommunicationDirection.System, "发送失败", ex.Message);
                return false;
            }
        }

        private bool SendJson(string json)
        {
            // 统一：能解析为 Message 则走二进制信封；无法识别的字符串不再发送裸 JSON。
            var msg = Message.FromJson(json);
            if (msg != null && !string.IsNullOrEmpty(msg.Cmd))
                return SendMessageBinary(msg);

            RecordTrace(CommunicationDirection.System, "发送失败",
                "拒绝发送非协议 JSON（请使用 Message/二进制信封）");
            return false;
        }

        private void OnTransportDiagnostic(string message) =>
            RecordTrace(CommunicationDirection.System, "传输层", message);

        private void OnUnframedDataReceived(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            string content = FormatUnframedData(bytes);
            bool bootLog = content.Contains("ESP-ROM", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("rst:0x", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("[CABINET_BOOT]", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("[ROOT_BOOT]", StringComparison.OrdinalIgnoreCase);
            bool brownout = content.Contains("BROWNOUT", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("RESET_REASON=9", StringComparison.OrdinalIgnoreCase);
            RecordTrace(bootLog ? CommunicationDirection.System : CommunicationDirection.Receive,
                brownout ? "设备供电异常/重启" : bootLog ? "设备启动/重启" : "原始数据", content);
        }

        /// <summary>链路连接状态变化</summary>
        private void OnConnectionChanged(bool connected)
        {
            RecordTrace(CommunicationDirection.System, "物理链路",
                connected ? "已连接" : "已断开");
            if (!connected)
                SetProtocolConnected(false);
        }

        private void HealthTimerTick(object? state)
        {
            ITransport? transport = _transport;
            if (transport?.IsConnected != true)
            {
                SetProtocolConnected(false);
                return;
            }

            bool startupProbePaused = System.Windows.Application.Current is App app &&
                !app.CabinetBackgroundServicesStarted &&
                !string.Equals(ConfigHelper.Current.LinkMode, "Uart",
                    StringComparison.OrdinalIgnoreCase);
            bool backgroundAllowed = !startupProbePaused &&
                App.CommunicationCoordinator.IsBackgroundTrafficAllowed;

            DateTime now = DateTime.Now;
            if (backgroundAllowed && LastReceivedTime.HasValue &&
                now - LastReceivedTime.Value >= ProtocolSilenceTimeout)
                SetProtocolConnected(false);

            if (Interlocked.Exchange(ref _healthProbeBusy, 1) != 0) return;
            try
            {
                if (!backgroundAllowed) return;
                var probe = Message.Create(Protocol.CmdReadStatus, "");
                SendMessageBinary(probe);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _healthProbeBusy, 0);
            }
        }

        private void SetProtocolConnected(bool connected)
        {
            int next = connected ? 1 : 0;
            int previous = Interlocked.Exchange(ref _protocolConnectedFlag, next);
            if (previous == next) return;

            RecordTrace(CommunicationDirection.System, "协议链路",
                connected ? "已响应" : "无响应");
            try { ConnectionChanged?.Invoke(connected); } catch { }

            if (!connected)
            {
                List<DeviceClient> disconnected = new List<DeviceClient>();
                lock (_devicesLock)
                {
                    foreach (var device in _devices.Values)
                    {
                        if (!device.IsOnline) continue;
                        device.IsOnline = false;
                        disconnected.Add(device);
                    }
                }
                foreach (var device in disconnected)
                    try { DeviceDisconnected?.Invoke(device); } catch { }
            }
        }

        private void RecordTrace(CommunicationDirection direction, string category, string content)
        {
            const int maxContentLength = 16000;
            if (content.Length > maxContentLength)
                content = content.Substring(0, maxContentLength) + $"\n… 已截断 {content.Length - maxContentLength} 字符";

            var entry = new CommunicationTraceEntry
            {
                Timestamp = DateTime.Now,
                Direction = direction,
                Category = category,
                Content = content
            };
            lock (_traceLock)
            {
                _recentTrace.Enqueue(entry);
                while (_recentTrace.Count > MaxTraceEntries) _recentTrace.Dequeue();
            }
            // 订阅方可能是 UI；异常不得回灌串口 I/O 路径。
            try { TraceAdded?.Invoke(entry); }
            catch (Exception invEx)
            {
                System.Diagnostics.Debug.WriteLine($"[MeshBridge] TraceAdded: {invEx.Message}");
            }
        }


        private static string FormatDebugLog(Message msg)
        {
            try
            {
                if (msg.Data is Newtonsoft.Json.Linq.JObject jo)
                {
                    string m = jo["msg"]?.ToString()
                               ?? jo["message"]?.ToString()
                               ?? jo.ToString(Newtonsoft.Json.Formatting.None);
                    string level = jo["level"]?.ToString() ?? "INFO";
                    string did = string.IsNullOrEmpty(msg.DeviceId) ? "" : msg.DeviceId;
                    return string.IsNullOrEmpty(did) ? $"[{level}] {m}" : $"[{level}] {did}: {m}";
                }
            }
            catch { }
            return msg.Cmd ?? "LOG";
        }

        private static string FormatUnframedData(byte[] bytes)
        {
            int printable = bytes.Count(b => b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126));
            if (printable >= bytes.Length * 0.75)
            {
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0', '\r', '\n');
            }

            int shown = Math.Min(bytes.Length, 128);
            string hex = BitConverter.ToString(bytes, 0, shown).Replace('-', ' ');
            return bytes.Length > shown ? $"HEX({bytes.Length}B) {hex} …" : $"HEX({bytes.Length}B) {hex}";
        }
    }
}
