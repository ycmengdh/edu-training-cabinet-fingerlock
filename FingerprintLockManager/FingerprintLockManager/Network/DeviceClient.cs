using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// 单个 ESP32 设备的 TCP 连接管理
    /// 无论该连接是 STA 模式下接受的还是 AP 模式下主动连接的，均通过本类管理。
    /// 内部使用 StreamReader/StreamWriter 按行读取（\n 分隔），收到的消息通过事件回调。
    /// </summary>
    public class DeviceClient : IDisposable
    {
        private readonly TcpClient _client;
        private StreamReader _reader;
        private StreamWriter _writer;
        private CancellationTokenSource _cts;
        private readonly object _sendLock = new object();
        private bool _disposed;
        private bool _online;

        /// <summary>设备 ID（设备注册后由消息同步，或上层手动设置）</summary>
        public string DeviceId { get; set; }

        /// <summary>设备名称</summary>
        public string DeviceName { get; set; }

        /// <summary>是否在线</summary>
        public bool IsOnline => _online;

        /// <summary>连接建立时间</summary>
        public DateTime ConnectTime { get; }

        /// <summary>收到消息事件</summary>
        public event Action<DeviceClient, Message> MessageReceived;

        /// <summary>连接断开事件</summary>
        public event Action<DeviceClient> Disconnected;

        /// <summary>
        /// 构造函数：传入已连接的 TcpClient
        /// </summary>
        /// <param name="client">已建立连接的 TcpClient</param>
        public DeviceClient(TcpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ConnectTime = DateTime.Now;
            _online = true;
            _client.NoDelay = true;

            var stream = _client.GetStream();
            _reader = new StreamReader(stream);
            // 统一使用 \n 作为行结束符，与 ESP32 端协议一致
            _writer = new StreamWriter(stream) { NewLine = "\n", AutoFlush = true };
        }

        /// <summary>
        /// 发送消息
        /// </summary>
        /// <param name="msg">待发送的消息</param>
        public void Send(Message msg)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg));
            if (!_online || _writer == null) return;

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
            var msg = Message.Create(cmd, DeviceId, data);
            Send(msg);
        }

        /// <summary>
        /// 启动接收循环（后台异步按行读取，直到连接断开）
        /// </summary>
        public void StartReceiving()
        {
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
                while (!token.IsCancellationRequested && _online)
                {
                    // ReadLineAsync 在对端关闭连接时返回 null
                    var line = await _reader.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var msg = Message.FromJson(line);
                    if (msg == null) continue;

                    // 收到携带 device_id 的消息时同步本对象 DeviceId
                    if (string.IsNullOrEmpty(DeviceId) && !string.IsNullOrEmpty(msg.DeviceId))
                    {
                        DeviceId = msg.DeviceId;
                    }

                    MessageReceived?.Invoke(this, msg);
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
        /// 主动断开连接，并触发 Disconnected 事件
        /// </summary>
        public void Disconnect()
        {
            if (!_online) return;
            _online = false;

            try { _cts?.Cancel(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }

            Disconnected?.Invoke(this);
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
