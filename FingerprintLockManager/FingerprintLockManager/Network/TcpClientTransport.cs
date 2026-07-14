using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// WiFi TCP 客户端传输实现
    /// 上位机主动连接 Mesh 根节点的 AP 热点（默认 192.168.4.1:8888）。
    /// 断线后自动重连（3 秒间隔），按行读取（\n 分隔）。
    /// </summary>
    public class TcpClientTransport : ITransport
    {
        /// <summary>重连间隔（毫秒）</summary>
        private const int ReconnectIntervalMs = 3000;

        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _cts;
        private readonly object _sendLock = new object();
        private volatile bool _connected;
        private bool _running;

        /// <summary>目标 IP（根节点 AP 地址）</summary>
        public string Host { get; }

        /// <summary>目标端口</summary>
        public int Port { get; }

        /// <summary>是否已连接</summary>
        public bool IsConnected => _connected;

        /// <summary>收到一行 JSON 消息事件</summary>
        public event Action<string>? LineReceived;

        /// <summary>连接状态变化事件（参数为是否已连接）</summary>
        public event Action<bool>? ConnectionChanged;

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
            _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        }

        /// <summary>停止并断开连接</summary>
        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            DisconnectInternal();
        }

        /// <summary>发送一条 JSON 消息（自动补 \n）</summary>
        public bool Send(string jsonLine)
        {
            if (!_connected || _writer == null) return false;
            try
            {
                string line = jsonLine?.TrimEnd('\n', '\r') + "\n";
                lock (_sendLock)
                {
                    _writer.Write(line);
                    _writer.Flush();
                }
                return true;
            }
            catch
            {
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

                    var stream = _client.GetStream();
                    _reader = new StreamReader(stream);
                    _writer = new StreamWriter(stream) { NewLine = "\n", AutoFlush = true };

                    ConnectionChanged?.Invoke(true);

                    // 接收循环（阻塞直到断开）
                    await ReceiveLoopAsync(token);
                }
                catch
                {
                    // 连接失败或接收中断
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

        /// <summary>接收循环：按行读取直到连接断开</summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                while (_running && _connected && !token.IsCancellationRequested)
                {
                    var line = await _reader!.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    line = line.TrimEnd('\r');
                    LineReceived?.Invoke(line);
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
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _writer = null;
            _reader = null;
            _client = null;

            if (wasConnected)
            {
                ConnectionChanged?.Invoke(false);
            }
        }
    }
}
