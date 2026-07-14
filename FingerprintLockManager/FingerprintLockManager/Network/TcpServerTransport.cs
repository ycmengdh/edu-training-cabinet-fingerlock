using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// TCP 服务端传输实现（由原 TcpServer 适配为 ITransport）
    /// 上位机监听 0.0.0.0:8888，等待 Mesh 根节点（Root）连接。
    /// Root 是唯一连接方，但保留多连接管理能力以保持扩展性。
    /// 收到 Root 转发的消息后触发 LineReceived 事件。
    /// </summary>
    public class TcpServerTransport : ITransport
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly object _clientsLock = new object();
        private readonly List<TcpClientState> _clients = new List<TcpClientState>();
        private bool _running;

        /// <summary>监听端口</summary>
        public int Port { get; }

        /// <summary>是否已连接（至少有一个客户端连接即视为已连接）</summary>
        public bool IsConnected
        {
            get
            {
                lock (_clientsLock)
                {
                    return _clients.Count > 0;
                }
            }
        }

        /// <summary>收到一行 JSON 消息事件</summary>
        public event Action<string>? LineReceived;

        /// <summary>客户端连接状态变化事件（参数为是否至少有一个连接）</summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>
        /// 构造 TCP 服务端传输
        /// </summary>
        /// <param name="port">监听端口，默认 8888</param>
        public TcpServerTransport(int port = 8888)
        {
            Port = port;
        }

        /// <summary>启动监听并接受连接</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        /// <summary>停止监听并断开所有客户端</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            List<TcpClientState> snapshot;
            lock (_clientsLock)
            {
                snapshot = new List<TcpClientState>(_clients);
                _clients.Clear();
            }
            foreach (var c in snapshot)
            {
                CleanupClient(c);
            }
        }

        /// <summary>发送一条 JSON 消息（自动补 \n），写入所有已连接客户端</summary>
        public bool Send(string jsonLine)
        {
            List<TcpClientState> snapshot;
            lock (_clientsLock)
            {
                snapshot = new List<TcpClientState>(_clients);
            }
            if (snapshot.Count == 0) return false;

            string line = jsonLine?.TrimEnd('\n', '\r') + "\n";
            bool anySuccess = false;
            foreach (var c in snapshot)
            {
                if (WriteToClient(c, line)) anySuccess = true;
            }
            return anySuccess;
        }

        /// <summary>接受连接循环</summary>
        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(token);
                }
                catch
                {
                    break;
                }

                client.NoDelay = true;
                var state = new TcpClientState
                {
                    Client = client,
                    Reader = new StreamReader(client.GetStream()),
                    Writer = new StreamWriter(client.GetStream()) { NewLine = "\n", AutoFlush = true }
                };

                bool wasEmpty;
                lock (_clientsLock)
                {
                    wasEmpty = _clients.Count == 0;
                    _clients.Add(state);
                }
                if (wasEmpty) ConnectionChanged?.Invoke(true);

                _ = Task.Run(() => ReceiveLoopAsync(state, token));
            }
        }

        /// <summary>单个客户端接收循环</summary>
        private async Task ReceiveLoopAsync(TcpClientState state, CancellationToken token)
        {
            try
            {
                while (_running && !token.IsCancellationRequested)
                {
                    var line = await state.Reader.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    line = line.TrimEnd('\r');
                    LineReceived?.Invoke(line);
                }
            }
            catch
            {
                // 读取异常：连接断开
            }
            finally
            {
                bool nowEmpty = false;
                lock (_clientsLock)
                {
                    _clients.Remove(state);
                    if (_clients.Count == 0) nowEmpty = true;
                }
                CleanupClient(state);
                if (nowEmpty) ConnectionChanged?.Invoke(false);
            }
        }

        /// <summary>向单个客户端写入一行</summary>
        private bool WriteToClient(TcpClientState state, string line)
        {
            try
            {
                lock (state.SendLock)
                {
                    state.Writer.Write(line);
                    state.Writer.Flush();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>清理单个客户端资源</summary>
        private void CleanupClient(TcpClientState state)
        {
            try { state.Writer?.Dispose(); } catch { }
            try { state.Reader?.Dispose(); } catch { }
            try { state.Client?.Close(); } catch { }
        }

        /// <summary>单个 TCP 客户端状态</summary>
        private class TcpClientState
        {
            public TcpClient Client = null!;
            public StreamReader Reader = null!;
            public StreamWriter Writer = null!;
            public readonly object SendLock = new object();
        }
    }
}
