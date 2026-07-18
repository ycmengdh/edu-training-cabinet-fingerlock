using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// WiFi TCP 客户端传输实现
    /// 上位机主动连接 Mesh 根节点的 AP 热点（默认 192.168.4.1:8888）。
    /// 断线后自动重连（3 秒间隔），使用 ESP 二进制协议帧收发。
    /// </summary>
    public class TcpClientTransport : ITransport
    {
        /// <summary>重连间隔（毫秒）</summary>
        private const int ReconnectIntervalMs = 3000;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private readonly object _sendLock = new object();
        private readonly FrameStreamDecoder _decoder = new FrameStreamDecoder();
        private volatile bool _connected;
        private bool _running;

        /// <summary>目标 IP（根节点 AP 地址）</summary>
        public string Host { get; }

        /// <summary>目标端口</summary>
        public int Port { get; }

        public string Description => $"TCP 客户端 {Host}:{Port}";

        public string LastError { get; private set; } = "";

        private string _lastDiagnostic = "";

        /// <summary>是否已连接</summary>
        public bool IsConnected => _connected;

        /// <summary>收到一条完整 JSON 消息事件</summary>
        public event Action<string>? LineReceived;

        /// <summary>连接状态变化事件（参数为是否已连接）</summary>
        public event Action<bool>? ConnectionChanged;

        public event Action<string>? DiagnosticMessage;

        public event Action<byte[]>? UnframedDataReceived;

        /// <summary>
        /// 构造 TCP 客户端传输
        /// </summary>
        /// <param name="host">根节点 AP IP，默认 192.168.4.1</param>
        /// <param name="port">端口，默认 8888</param>
        public TcpClientTransport(string host = "192.168.4.1", int port = 8888)
        {
            Host = host;
            Port = port;
        }

        /// <summary>启动连接循环（后台自动重连）</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            ReportDiagnostic($"正在连接 {Description}");
            _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        }

        /// <summary>停止并断开连接</summary>
        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            DisconnectInternal();
        }

        /// <summary>发送一条 JSON 消息（编码为 ESP 二进制协议帧）</summary>
        public bool Send(string jsonLine)
        {
            if (!_connected || _stream == null) return false;
            try
            {
                byte[]? frame = FrameCodec.Encode(jsonLine.TrimEnd('\n', '\r'));
                if (frame == null) return false;
                lock (_sendLock)
                {
                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                ReportError($"TCP 发送失败：{ex.Message}");
                DisconnectInternal();
                return false;
            }
        }

        /// <summary>连接循环：连接 -> 接收 -> 断开后等待重连</summary>
        private async Task ConnectLoopAsync(CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested)
            {
                try
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync(Host, Port, token);
                    _client.NoDelay = true;
                    _connected = true;

                    _stream = _client.GetStream();
                    _decoder.Reset();
                    LastError = "";
                    ReportDiagnostic($"已连接 {Description}", true);

                    ConnectionChanged?.Invoke(true);

                    // 接收循环（阻塞直到断开）
                    await ReceiveLoopAsync(token);
                }
                catch (Exception ex)
                {
                    if (_running && !token.IsCancellationRequested)
                        ReportError($"连接 {Host}:{Port} 失败：{ex.Message}");
                }
                finally
                {
                    DisconnectInternal();
                }

                // 等待重连间隔
                if (!_running || token.IsCancellationRequested) break;
                try
                {
                    await Task.Delay(ReconnectIntervalMs, token);
                }
                catch
                {
                    break;
                }
            }
        }

        /// <summary>接收循环：读取二进制帧直到连接断开</summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                byte[] buffer = new byte[8192];
                while (_running && _connected && !token.IsCancellationRequested)
                {
                    int count = await _stream!.ReadAsync(buffer, 0, buffer.Length, token);
                    if (count == 0) break;
                    _decoder.Append(buffer, 0, count,
                        json => LineReceived?.Invoke(json),
                        bytes => UnframedDataReceived?.Invoke(bytes));
                }
            }
            catch
            {
                // 读取异常：连接已断开
            }
        }

        /// <summary>断开当前连接并通知状态变化</summary>
        private void DisconnectInternal()
        {
            bool wasConnected = _connected;
            _connected = false;
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;

            if (wasConnected)
            {
                ConnectionChanged?.Invoke(false);
                ReportDiagnostic("TCP 连接已断开，正在自动重连", true);
            }
        }

        private void ReportError(string message)
        {
            LastError = message;
            ReportDiagnostic(message);
        }

        private void ReportDiagnostic(string message, bool force = false)
        {
            if (!force && string.Equals(_lastDiagnostic, message, StringComparison.Ordinal)) return;
            _lastDiagnostic = message;
            DiagnosticMessage?.Invoke(message);
        }
    }
}
