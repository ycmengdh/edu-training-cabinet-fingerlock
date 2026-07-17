using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// USB 串口传输实现
    /// 通过 USB 串口直连 Mesh 根节点，默认波特率 921600。
    /// DataReceived 事件读取二进制协议帧，触发 LineReceived 事件（事件参数仍为 JSON）。
    /// </summary>
    public class SerialTransport : ITransport
    {
        /// <summary>默认波特率（921600，满足 Mesh 控制消息带宽需求）</summary>
        public const int DefaultBaudRate = 921600;

        private const int ReconnectIntervalMs = 2000;

        private SerialPort? _port;
        private readonly object _sendLock = new object();
        private readonly object _receiveLock = new object();
        private CancellationTokenSource? _cts;
        private volatile bool _running;
        private readonly FrameStreamDecoder _decoder = new FrameStreamDecoder();
        private readonly byte[] _readBuffer = new byte[4096];

        /// <summary>串口名（如 COM3 / /dev/ttyUSB0）</summary>
        public string PortName { get; }

        /// <summary>波特率</summary>
        public int BaudRate { get; }

        /// <summary>是否已连接</summary>
        public bool IsConnected
        {
            get
            {
                lock (_sendLock)
                {
                    return _port?.IsOpen == true;
                }
            }
        }

        /// <summary>收到一条 JSON 消息事件</summary>
        public event Action<string>? LineReceived;

        /// <summary>串口打开或断开事件</summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>
        /// 构造串口传输
        /// </summary>
        /// <param name="portName">串口名，为空时使用首个可用串口</param>
        /// <param name="baudRate">波特率，默认 921600</param>
        public SerialTransport(string portName = "", int baudRate = DefaultBaudRate)
        {
            PortName = portName;
            BaudRate = baudRate;
        }

        /// <summary>启动串口探测循环；设备暂未接入时会持续等待并自动重连。</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        }

        /// <summary>停止串口并释放资源</summary>
        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            DisconnectPort();
        }

        /// <summary>发送一条 JSON 消息（编码为 ESP 二进制协议帧）</summary>
        public bool Send(string jsonLine)
        {
            try
            {
                byte[]? frame = FrameCodec.Encode(jsonLine.TrimEnd('\n', '\r'));
                if (frame == null) return false;
                lock (_sendLock)
                {
                    if (_port?.IsOpen != true) return false;
                    _port.Write(frame, 0, frame.Length);
                }
                return true;
            }
            catch
            {
                DisconnectPort();
                return false;
            }
        }

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested)
            {
                if (!IsConnected) TryOpenPort();
                try
                {
                    await Task.Delay(ReconnectIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void TryOpenPort()
        {
            string name = string.IsNullOrWhiteSpace(PortName) ? DetectFirstPort() : PortName;
            if (string.IsNullOrWhiteSpace(name)) return;

            var port = new SerialPort(name, BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000
            };

            try
            {
                port.DataReceived += OnDataReceived;
                port.Open();
                lock (_sendLock)
                {
                    if (!_running || _port != null)
                    {
                        port.DataReceived -= OnDataReceived;
                        port.Dispose();
                        return;
                    }
                    _port = port;
                    _decoder.Reset();
                }
                ConnectionChanged?.Invoke(true);
            }
            catch
            {
                try { port.DataReceived -= OnDataReceived; } catch { }
                try { port.Dispose(); } catch { }
            }
        }

        /// <summary>串口数据接收回调：读取协议帧并触发 LineReceived</summary>
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (sender is not SerialPort port || !ReferenceEquals(port, _port) || !port.IsOpen) return;

                // Read raw bytes and let the shared decoder recover frame
                // boundaries across arbitrary SerialPort chunks.
                lock (_receiveLock)
                {
                    while (port.BytesToRead > 0)
                    {
                        int count = port.Read(_readBuffer, 0,
                            Math.Min(_readBuffer.Length, port.BytesToRead));
                        _decoder.Append(_readBuffer, 0, count,
                            json => LineReceived?.Invoke(json));
                    }
                }
            }
            catch
            {
                DisconnectPort();
            }
        }

        private void DisconnectPort()
        {
            SerialPort? port;
            bool wasConnected;
            lock (_sendLock)
            {
                port = _port;
                _port = null;
                wasConnected = port?.IsOpen == true;
            }

            if (port != null)
            {
                try { port.DataReceived -= OnDataReceived; } catch { }
                try { if (port.IsOpen) port.Close(); } catch { }
                try { port.Dispose(); } catch { }
            }
            if (wasConnected) ConnectionChanged?.Invoke(false);
        }

        /// <summary>检测首个可用串口名</summary>
        private static string DetectFirstPort()
        {
            try
            {
                var names = SerialPort.GetPortNames();
                return names != null && names.Length > 0 ? names[0] : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
