using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// TCP 客户端（AP 模式用）
    /// 上位机作为客户端，主动连接 ESP32 的 AP 热点（默认 192.168.4.1:8888）。
    /// 内部使用 StreamReader/StreamWriter 按行读取（\n 分隔）。
    /// </summary>
    public class DeviceConfigClient : IDisposable
    {
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _cts;
        private readonly object _sendLock = new object();
        private bool _disposed;
        private bool _connected;
        private string? _deviceId;

        /// <summary>是否已连接</summary>
        public bool IsConnected => _connected;

        /// <summary>收到消息事件</summary>
        public event Action<Message> MessageReceived;

        /// <summary>连接断开事件</summary>
        public event Action Disconnected;

        /// <summary>
        /// 连接 ESP32 AP
        /// </summary>
        /// <param name="ip">ESP32 AP 模式 IP，默认 192.168.4.1</param>
        /// <param name="port">端口，默认 8888</param>
        public async Task ConnectAsync(string ip = "192.168.4.1", int port = 8888)
        {
            if (_connected) return;

            _client = new TcpClient();
            await _client.ConnectAsync(ip, port);
            _client.NoDelay = true;
            _connected = true;

            var stream = _client.GetStream();
            _reader = new StreamReader(stream);
            _writer = new StreamWriter(stream) { NewLine = "\n", AutoFlush = true };

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 接收循环：按行读取直到连接断开
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _connected)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var msg = Message.FromJson(line);
                    if (msg == null) continue;

                    // 同步 DeviceId
                    if (string.IsNullOrEmpty(_deviceId) && !string.IsNullOrEmpty(msg.DeviceId))
                    {
                        _deviceId = msg.DeviceId;
                    }

                    MessageReceived?.Invoke(msg);
                }
            }
            catch
            {
                // 读取异常：连接已断开，进入 finally 处理
            }
            finally
            {
                Disconnect();
            }
        }

        /// <summary>
        /// 发送消息
        /// </summary>
        /// <param name="msg">待发送的消息</param>
        public void Send(Message msg)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg));
            if (!_connected || _writer == null) return;

            // 若消息未带 deviceId，则使用当前已记录的设备 ID
            if (string.IsNullOrEmpty(msg.DeviceId) && !string.IsNullOrEmpty(_deviceId))
            {
                msg.DeviceId = _deviceId;
            }

            var json = msg.ToJson();
            lock (_sendLock)
            {
                try
                {
                    _writer.Write(json);
                    _writer.Flush();
                }
                catch
                {
                    // 写入异常视为连接已断开
                    Disconnect();
                }
            }
        }

        /// <summary>
        /// 发送消息（便捷重载，自动构造 Message 并填充当前设备 ID）
        /// </summary>
        /// <param name="cmd">命令字符串</param>
        /// <param name="data">附加数据，可为 null</param>
        public void Send(string cmd, object data = null)
        {
            var msg = Message.Create(cmd, _deviceId ?? "", data);
            Send(msg);
        }

        /// <summary>
        /// 断开连接，并触发 Disconnected 事件
        /// </summary>
        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;

            try { _cts?.Cancel(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }

            Disconnected?.Invoke();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
