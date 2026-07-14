using System.IO.Ports;
using System.Text;

namespace FingerprintLockManager
{
    /// <summary>
    /// USB 串口传输实现
    /// 通过 USB 串口直连 Mesh 根节点，默认波特率 2000000（2Mbps）。
    /// DataReceived 事件按行读取（\n 分隔），触发 LineReceived 事件。
    /// </summary>
    public class SerialTransport : ITransport
    {
        /// <summary>默认波特率（2Mbps，满足 Mesh 控制消息 + 指纹模板传输带宽需求）</summary>
        public const int DefaultBaudRate = 2000000;

        private SerialPort? _port;
        private readonly object _sendLock = new object();
        private bool _running;
        private bool _disposed;

        /// <summary>串口名（如 COM3 / /dev/ttyUSB0）</summary>
        public string PortName { get; }

        /// <summary>波特率</summary>
        public int BaudRate { get; }

        /// <summary>是否已连接</summary>
        public bool IsConnected => _port != null && _port.IsOpen;

        /// <summary>收到一行 JSON 消息事件</summary>
        public event Action<string>? LineReceived;

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

        /// <summary>启动串口（打开端口并订阅数据接收事件）</summary>
        public void Start()
        {
            if (_running) return;

            string name = string.IsNullOrEmpty(PortName) ? DetectFirstPort() : PortName;
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException("未检测到可用串口，请在配置中指定串口名");
            }

            _port = new SerialPort(name, BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000,
                Encoding = Encoding.UTF8,
                NewLine = "\n"
            };
            _port.DataReceived += OnDataReceived;
            _port.Open();
            _running = true;
        }

        /// <summary>停止串口并释放资源</summary>
        public void Stop()
        {
            _running = false;
            try
            {
                if (_port != null)
                {
                    _port.DataReceived -= OnDataReceived;
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                }
            }
            catch
            {
                // 忽略关闭异常
            }
            _port = null;
        }

        /// <summary>发送一条 JSON 消息（自动补 \n）</summary>
        public bool Send(string jsonLine)
        {
            if (!IsConnected || _port == null) return false;
            try
            {
                string line = jsonLine?.TrimEnd('\n', '\r') + "\n";
                lock (_sendLock)
                {
                    _port.Write(line);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>串口数据接收回调：按行读取并触发 LineReceived</summary>
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_port == null || !_port.IsOpen) return;

                // 循环读取已缓冲的完整行
                while (_port.BytesToRead > 0)
                {
                    string? line = _port.ReadLine();
                    if (string.IsNullOrEmpty(line)) continue;
                    line = line.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    LineReceived?.Invoke(line);
                }
            }
            catch
            {
                // 读取异常（如未读完一行）忽略，等待后续数据
            }
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
