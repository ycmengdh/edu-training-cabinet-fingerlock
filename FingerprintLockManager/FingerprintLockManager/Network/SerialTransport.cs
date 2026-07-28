using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintLockManager
{
    /// <summary>
    /// USB 串口传输：单一 I/O 线程独占 SerialPort（读+写），
    /// 彻底避免 “另一个线程拥有该对象” 以及并发写导致的 CRC 损坏。
    /// </summary>
    public class SerialTransport : ITransport
    {
        public const int DefaultBaudRate = 921600;

        private const int ReconnectIntervalMs = 500;
        private const int ReadChunkSize = 4096;
        private const int IoIdleDelayMs = 2;
        // ESP32-S3 HWCDC historically defaults to a 256-byte RX queue. Pace
        // writes in USB packet-sized chunks so large SD_SAVE frames remain
        // reliable even before the root firmware is upgraded.
        private const int WriteChunkSize = 64;
        private const int WriteChunkDelayMs = 2;
        private const int ReceiveQueueCapacity = 4096;

        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _ioTask;
        private Task? _dispatchTask;
        private int _ioThreadId;
        private int _dispatchThreadId;
        private volatile bool _running;
        private readonly FrameStreamDecoder _decoder = new FrameStreamDecoder();
        private readonly byte[] _readBuffer = new byte[ReadChunkSize];
        private string _activePortName = "";
        private string _lastDiagnostic = "";
        private string _lastSendError = "";
        private DateTime _lastSendErrorAt = DateTime.MinValue;

        private readonly ConcurrentQueue<PendingWrite> _writeQueue = new();
        private readonly AutoResetEvent _writeSignal = new(false);
        private readonly BlockingCollection<ReceivedItem> _receiveQueue =
            new(new ConcurrentQueue<ReceivedItem>(), ReceiveQueueCapacity);
        private int _connectedFlag; // 0/1 for lock-free IsConnected snapshot

        private sealed class PendingWrite
        {
            public required byte[] Frame;
            public TaskCompletionSource<bool> Tcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class ReceivedItem
        {
            public required byte[] Data;
            public bool IsPayload;
        }

        public string PortName { get; }
        public int BaudRate { get; }

        public string Description => $"USB 串口 {(string.IsNullOrWhiteSpace(_activePortName) ?
            (string.IsNullOrWhiteSpace(PortName) ? "自动选择" : PortName) : _activePortName)} @ {BaudRate}";

        public string LastError { get; private set; } = "";

        public bool IsConnected => Volatile.Read(ref _connectedFlag) == 1;

        public event Action<string>? LineReceived;
        public event Action<byte[]>? PayloadReceived;
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? DiagnosticMessage;
        public event Action<byte[]>? UnframedDataReceived;

        public SerialTransport(string portName = "", int baudRate = DefaultBaudRate)
        {
            PortName = portName;
            BaudRate = baudRate;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            ReportDiagnostic($"正在打开 {Description}");
            _dispatchTask = Task.Factory.StartNew(
                () => DispatchLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _ioTask = Task.Factory.StartNew(
                () => IoLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _receiveQueue.CompleteAdding(); } catch { }
            try { _writeSignal.Set(); } catch { }
            if (Thread.CurrentThread.ManagedThreadId != _ioThreadId)
                try { _ioTask?.Wait(1500); } catch { }
            if (Thread.CurrentThread.ManagedThreadId != _dispatchThreadId)
                try { _dispatchTask?.Wait(1500); } catch { }
            // IoLoop 负责关闭端口
            FailAllPendingWrites();
            SetConnected(false, reportReconnect: false);
        }

        public bool Send(string jsonLine)
        {
            byte[]? frame = FrameCodec.Encode(jsonLine.TrimEnd('\n', '\r'));
            return EnqueueWrite(frame);
        }

        public bool SendPayload(byte[] appPayload)
        {
            if (appPayload == null || appPayload.Length == 0) return false;
            byte[]? frame = FrameCodec.Encode(appPayload);
            return EnqueueWrite(frame);
        }

        private bool EnqueueWrite(byte[]? frame)
        {
            if (frame == null || frame.Length == 0) return false;
            if (!_running)
            {
                LastError = "串口未启动";
                return false;
            }
            if (!IsConnected)
            {
                LastError = "串口未打开";
                return false;
            }

            var pending = new PendingWrite { Frame = frame };
            _writeQueue.Enqueue(pending);
            _writeSignal.Set();

            // 禁止在 I/O 线程上同步 Wait：Payload 回调若回写会自锁。
            if (_ioThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _ioThreadId)
            {
                // 已入队，由后续 IoLoop 写出；无法同步得知结果。
                return true;
            }

            // 等待 I/O 线程完成写入（最多写超时 + 余量）
            if (pending.Tcs.Task.Wait(1500))
                return pending.Tcs.Task.Result;

            LastError = "串口发送等待超时";
            ReportSendError(LastError);
            return false;
        }

        private void IoLoop(CancellationToken token)
        {
            _ioThreadId = Thread.CurrentThread.ManagedThreadId;
            while (_running && !token.IsCancellationRequested)
            {
                try
                {
                    if (_port?.IsOpen != true)
                    {
                        TryOpenPortOnIoThread();
                        if (_port?.IsOpen != true)
                        {
                            WaitSignal(ReconnectIntervalMs, token);
                            continue;
                        }
                    }

                    DrainWriteQueueOnIoThread();
                    ReadAvailableOnIoThread();

                    // 无写请求时短暂休眠，降低 CPU；有写则立刻被 signal 唤醒
                    if (_writeQueue.IsEmpty)
                        WaitSignal(IoIdleDelayMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ReportError($"串口 I/O 异常：{ex.Message}", disconnect: true);
                    ClosePortOnIoThread(reportReconnect: true);
                    WaitSignal(ReconnectIntervalMs, token);
                }
            }

            ClosePortOnIoThread(reportReconnect: false);
            FailAllPendingWrites();
        }

        private void WaitSignal(int ms, CancellationToken token)
        {
            try
            {
                WaitHandle.WaitAny(new WaitHandle[] { _writeSignal, token.WaitHandle }, ms);
            }
            catch (ObjectDisposedException) { }
        }

        private void TryOpenPortOnIoThread()
        {
            string name = string.IsNullOrWhiteSpace(PortName) ? DetectFirstPort() : PortName;
            if (string.IsNullOrWhiteSpace(name))
            {
                ReportError("未发现可用串口，请检查 USB 连接并刷新串口列表", disconnect: false);
                return;
            }

            SerialPort? port = null;
            try
            {
                port = new SerialPort(name, BaudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 200,
                    WriteTimeout = 2000,
                    Handshake = Handshake.None,
                    DtrEnable = false,
                    RtsEnable = false,
                    // 提高驱动缓冲，减少突发 SD_SAVE 时丢失
                    ReadBufferSize = 128 * 1024,
                    WriteBufferSize = 64 * 1024,
                };

                // 不使用 DataReceived：全部由本线程轮询 Read
                port.Open();
                try { port.DiscardOutBuffer(); } catch { }

                _port = port;
                port = null;
                _activePortName = name;
                _decoder.Reset();
                LastError = "";
                _lastSendError = "";
                ReportDiagnostic($"串口 {name} 已打开，波特率 {BaudRate}", force: true);
                SetConnected(true, reportReconnect: false);
            }
            catch (Exception ex)
            {
                try { port?.Dispose(); } catch { }
                ReportError($"打开串口 {name} 失败：{ex.Message}", disconnect: false);
            }
        }

        private void DrainWriteQueueOnIoThread()
        {
            while (_writeQueue.TryDequeue(out PendingWrite? pending))
            {
                if (pending == null) continue;
                bool ok = false;
                try
                {
                    if (_port?.IsOpen == true)
                    {
                        WriteFramePacedOnIoThread(_port, pending.Frame);
                        ok = true;
                    }
                    else
                    {
                        LastError = "串口未打开";
                    }
                }
                catch (TimeoutException ex)
                {
                    LastError = $"串口发送超时：{ex.Message}";
                    ReportSendError(LastError);
                    // 超时不立刻拆链路
                }
                catch (Exception ex)
                {
                    LastError = $"串口发送失败：{ex.Message}";
                    ReportSendError(LastError);
                    ClosePortOnIoThread(reportReconnect: true);
                    pending.Tcs.TrySetResult(false);
                    // 剩余队列失败
                    FailAllPendingWrites();
                    return;
                }
                pending.Tcs.TrySetResult(ok);
            }
        }

        private static void WriteFramePacedOnIoThread(SerialPort port, byte[] frame)
        {
            int offset = 0;
            while (offset < frame.Length)
            {
                int count = Math.Min(WriteChunkSize, frame.Length - offset);
                port.Write(frame, offset, count);
                offset += count;
                if (offset < frame.Length)
                    Thread.Sleep(WriteChunkDelayMs);
            }
        }

        private void ReadAvailableOnIoThread()
        {
            SerialPort? port = _port;
            if (port?.IsOpen != true) return;

            try
            {
                int guard = 0;
                while (port.BytesToRead > 0 && guard++ < 256)
                {
                    int toRead = Math.Min(_readBuffer.Length, port.BytesToRead);
                    if (toRead <= 0) break;
                    int count = port.Read(_readBuffer, 0, toRead);
                    if (count <= 0) break;

                    _decoder.AppendBytes(_readBuffer, 0, count,
                        payload =>
                        {
                            if (payload != null && payload.Length > 0)
                                EnqueueReceived(payload, isPayload: true);
                        },
                        bytes =>
                        {
                            if (bytes != null && bytes.Length > 0)
                                EnqueueReceived(bytes, isPayload: false);
                        });
                }
            }
            catch (TimeoutException)
            {
                // 轮询读超时正常
            }
            catch (Exception ex)
            {
                ReportError($"串口接收失败：{ex.Message}", disconnect: true);
                ClosePortOnIoThread(reportReconnect: true);
                return;
            }

        }

        private void EnqueueReceived(byte[] data, bool isPayload)
        {
            if (data == null || data.Length == 0 || _receiveQueue.IsAddingCompleted) return;
            try
            {
                if (!_receiveQueue.TryAdd(
                    new ReceivedItem { Data = data, IsPayload = isPayload }, 1000))
                {
                    ReportError("串口接收分发队列已满，业务处理持续阻塞", disconnect: false);
                }
            }
            catch (InvalidOperationException) { }
        }

        private void DispatchLoop(CancellationToken token)
        {
            _dispatchThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                foreach (ReceivedItem item in _receiveQueue.GetConsumingEnumerable(token))
                {
                    try
                    {
                        if (item.IsPayload)
                        {
                            PayloadReceived?.Invoke(item.Data);
                            if (item.Data.Length > 0 &&
                                (item.Data[0] == (byte)'{' || item.Data[0] == (byte)'['))
                            {
                                LineReceived?.Invoke(System.Text.Encoding.UTF8.GetString(item.Data));
                            }
                        }
                        else
                        {
                            UnframedDataReceived?.Invoke(item.Data);
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportDiagnostic($"接收业务回调异常：{ex.Message}", force: true);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void ClosePortOnIoThread(bool reportReconnect)
        {
            SerialPort? port = _port;
            _port = null;
            bool was = port?.IsOpen == true;
            if (port != null)
            {
                try { if (port.IsOpen) port.Close(); } catch { }
                try { port.Dispose(); } catch { }
            }
            try { _decoder.Reset(); } catch { }
            if (was)
                SetConnected(false, reportReconnect);
        }

        private void SetConnected(bool connected, bool reportReconnect)
        {
            int next = connected ? 1 : 0;
            int prev = Interlocked.Exchange(ref _connectedFlag, next);
            if (prev == next) return;
            try { ConnectionChanged?.Invoke(connected); } catch { }
            if (!connected && reportReconnect && _running)
                ReportDiagnostic("串口已断开，正在自动重连", force: true);
        }

        private void FailAllPendingWrites()
        {
            while (_writeQueue.TryDequeue(out PendingWrite? p))
                p?.Tcs.TrySetResult(false);
        }

        private void ReportSendError(string message)
        {
            DateTime now = DateTime.Now;
            if (string.Equals(_lastSendError, message, StringComparison.Ordinal) &&
                (now - _lastSendErrorAt).TotalMilliseconds < 1500)
                return;
            _lastSendError = message;
            _lastSendErrorAt = now;
            LastError = message;
            ReportDiagnostic(message, force: true);
        }

        private void ReportError(string message, bool disconnect)
        {
            LastError = message;
            ReportDiagnostic(message, force: true);
            if (disconnect)
                ClosePortOnIoThread(reportReconnect: true);
        }

        private void ReportDiagnostic(string message, bool force = false)
        {
            if (!force && string.Equals(_lastDiagnostic, message, StringComparison.Ordinal)) return;
            _lastDiagnostic = message;
            try { DiagnosticMessage?.Invoke(message); } catch { }
        }

        private static string DetectFirstPort()
        {
            return SerialPortDiscovery.GetPortNames().FirstOrDefault() ?? "";
        }
    }
}
