using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// TCP 服务端（STA 模式用）
    /// 上位机作为服务端，监听端口，接受多个 ESP32 连接（支持 40+ 设备）。
    /// 每个连接创建 DeviceClient 管理，设备连接/断开/消息通过事件通知上层。
    /// </summary>
    public class TcpServer
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly object _devicesLock = new object();
        private readonly List<DeviceClient> _devices = new List<DeviceClient>();
        private bool _running;

        /// <summary>
        /// 所有已连接的设备（返回列表副本，保证线程安全）
        /// </summary>
        public List<DeviceClient> Devices
        {
            get
            {
                lock (_devicesLock)
                {
                    return new List<DeviceClient>(_devices);
                }
            }
        }

        /// <summary>设备连接事件</summary>
        public event Action<DeviceClient> DeviceConnected;

        /// <summary>设备断开事件</summary>
        public event Action<DeviceClient> DeviceDisconnected;

        /// <summary>收到消息事件</summary>
        public event Action<DeviceClient, Message> MessageReceived;

        /// <summary>
        /// 启动监听
        /// </summary>
        /// <param name="port">监听端口，默认 8888</param>
        public void Start(int port = 8888)
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            // 异步接受连接循环
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 停止监听并断开所有设备
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            // 断开所有设备并解除事件订阅
            List<DeviceClient> snapshot;
            lock (_devicesLock)
            {
                snapshot = new List<DeviceClient>(_devices);
                _devices.Clear();
            }
            foreach (var d in snapshot)
            {
                d.MessageReceived -= OnDeviceMessageReceived;
                d.Disconnected -= OnDeviceDisconnected;
                d.Disconnect();
            }
        }

        /// <summary>
        /// 接受连接循环
        /// </summary>
        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _running)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 监听异常（如 Stop 调用）时退出循环
                    break;
                }

                var device = new DeviceClient(client);
                device.MessageReceived += OnDeviceMessageReceived;
                device.Disconnected += OnDeviceDisconnected;

                lock (_devicesLock)
                {
                    _devices.Add(device);
                }

                DeviceConnected?.Invoke(device);
                device.StartReceiving();
            }
        }

        /// <summary>
        /// 设备消息回调
        /// </summary>
        private void OnDeviceMessageReceived(DeviceClient device, Message msg)
        {
            MessageReceived?.Invoke(device, msg);
        }

        /// <summary>
        /// 设备断开回调
        /// </summary>
        private void OnDeviceDisconnected(DeviceClient device)
        {
            lock (_devicesLock)
            {
                _devices.Remove(device);
            }
            DeviceDisconnected?.Invoke(device);
        }

        /// <summary>
        /// 向指定设备发送消息
        /// </summary>
        /// <param name="deviceId">目标设备 ID</param>
        /// <param name="msg">待发送的消息</param>
        public void SendToDevice(string deviceId, Message msg)
        {
            DeviceClient? target = null;
            lock (_devicesLock)
            {
                foreach (var d in _devices)
                {
                    if (d.DeviceId == deviceId)
                    {
                        target = d;
                        break;
                    }
                }
            }
            target?.Send(msg);
        }

        /// <summary>
        /// 向所有在线设备广播消息
        /// </summary>
        /// <param name="msg">待广播的消息</param>
        public void Broadcast(Message msg)
        {
            List<DeviceClient> snapshot;
            lock (_devicesLock)
            {
                snapshot = new List<DeviceClient>(_devices);
            }
            foreach (var d in snapshot)
            {
                d.Send(msg);
            }
        }

        /// <summary>
        /// 获取在线设备列表
        /// </summary>
        /// <returns>当前在线的设备列表</returns>
        public List<DeviceClient> GetOnlineDevices()
        {
            lock (_devicesLock)
            {
                var result = new List<DeviceClient>();
                foreach (var d in _devices)
                {
                    if (d.IsOnline) result.Add(d);
                }
                return result;
            }
        }
    }
}
