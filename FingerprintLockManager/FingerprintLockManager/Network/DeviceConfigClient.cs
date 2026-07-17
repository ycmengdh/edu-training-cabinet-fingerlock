using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace FingerprintLockManager
{
    /// <summary>
    /// TCP 客户端（AP 模式用）
    /// 上位机作为客户端，主动连接 ESP32 的 AP 热点（默认 192.168.4.1:8888）。
    /// 内部使用 ESP 二进制协议帧读取，和主 Mesh 桥接链路保持一致。
    /// </summary>
    public class DeviceConfigClient : IDisposable
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private readonly object _sendLock = new object();
        private readonly FrameStreamDecoder _decoder = new FrameStreamDecoder();
        private readonly ConcurrentDictionary<string, PendingConfigRequest> _pending = new();
        private bool _disposed;
        private bool _connected;
        private string? _deviceId;

        /// <summary>是否已连接</summary>
        public bool IsConnected => _connected;

        /// <summary>收到消息事件</summary>
        public event Action<Message>? MessageReceived;

        /// <summary>连接断开事件</summary>
        public event Action? Disconnected;

        /// <summary>
        /// 连接 ESP32 AP
        /// </summary>
        /// <param name="ip">ESP32 AP 模式 IP，默认 192.168.4.1</param>
        /// <param name="port">端口，默认 8888</param>
        public async Task ConnectAsync(string ip = "192.168.4.1", int port = 8888,
            CancellationToken cancellationToken = default)
        {
            if (_connected) return;

            _client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await _client.ConnectAsync(ip, port, timeout.Token);
            }
            catch
            {
                _client.Dispose();
                _client = null;
                throw;
            }
            _client.NoDelay = true;
            _connected = true;

            _stream = _client.GetStream();
            _decoder.Reset();

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 接收循环：按协议帧读取直到连接断开
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                byte[] buffer = new byte[8192];
                while (!token.IsCancellationRequested && _connected && _stream != null)
                {
                    int count = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (count == 0) break;

                    _decoder.Append(buffer, 0, count, json =>
                    {
                        var msg = Message.FromJson(json);
                        if (msg == null) return;

                        // 同步 DeviceId
                        if (string.IsNullOrEmpty(_deviceId) && !string.IsNullOrEmpty(msg.DeviceId))
                        {
                            _deviceId = msg.DeviceId;
                        }

                        CompletePending(msg);
                        MessageReceived?.Invoke(msg);
                    });
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
        public bool Send(Message msg)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg));
            if (!_connected || _stream == null) return false;

            // 若消息未带 deviceId，则使用当前已记录的设备 ID
            if (string.IsNullOrEmpty(msg.DeviceId) && !string.IsNullOrEmpty(_deviceId))
            {
                msg.DeviceId = _deviceId;
            }

            var frame = FrameCodec.Encode(msg.ToJson().TrimEnd('\r', '\n'));
            if (frame == null) return false;
            lock (_sendLock)
            {
                try
                {
                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();
                    return true;
                }
                catch
                {
                    // 写入异常视为连接已断开
                    Disconnect();
                    return false;
                }
            }
        }

        /// <summary>
        /// 发送消息（便捷重载，自动构造 Message 并填充当前设备 ID）
        /// </summary>
        /// <param name="cmd">命令字符串</param>
        /// <param name="data">附加数据，可为 null</param>
        public bool Send(string cmd, object? data = null)
        {
            var msg = Message.Create(cmd, _deviceId ?? "", data);
            return Send(msg);
        }

        public async Task<Message?> SendRequestAsync(
            Message message, string expectedCommand, int timeoutMs = 5000)
        {
            var tcs = new TaskCompletionSource<Message?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new PendingConfigRequest(expectedCommand, tcs);
            if (!_pending.TryAdd(message.MsgId, request)) return null;
            if (!Send(message))
            {
                _pending.TryRemove(message.MsgId, out _);
                return null;
            }

            Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed == tcs.Task) return await tcs.Task;
            _pending.TryRemove(message.MsgId, out _);
            return null;
        }

        /// <summary>
        /// 断开连接，并触发 Disconnected 事件
        /// </summary>
        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;

            try { _cts?.Cancel(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }

            _stream = null;
            _client = null;
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var pending))
                    pending.Completion.TrySetResult(null);
            }
            Disconnected?.Invoke();
        }

        private void CompletePending(Message message)
        {
            if (!_pending.TryGetValue(message.MsgId, out var pending)) return;
            if (message.Cmd != pending.ExpectedCommand && message.Cmd != Protocol.CmdError) return;
            if (_pending.TryRemove(message.MsgId, out pending))
                pending.Completion.TrySetResult(message);
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

        private sealed record PendingConfigRequest(
            string ExpectedCommand, TaskCompletionSource<Message?> Completion);
    }
}
