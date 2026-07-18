namespace FingerprintLockManager
{
    /// <summary>
    /// 传输层抽象接口
    /// 统一 USB 串口、TCP 客户端、TCP 服务端三种链路的收发行为。
    /// Send 接收 JSON，具体链路统一编码为 ESP 二进制协议帧。
    /// 上层 MeshBridge 通过本接口与 Mesh 根节点通讯，不关心具体物理链路。
    /// 通讯格式：JSON 负载封装在 ESP 二进制协议帧中，支持流式接收和分片。
    /// </summary>
    public interface ITransport
    {
        /// <summary>是否已连接</summary>
        bool IsConnected { get; }

        /// <summary>当前链路端点的可读说明。</summary>
        string Description { get; }

        /// <summary>最近一次传输层错误；无错误时为空。</summary>
        string LastError { get; }

        /// <summary>收到一行 JSON 消息事件（已去除尾部换行）</summary>
        event Action<string>? LineReceived;

        /// <summary>物理链路连接状态变化</summary>
        event Action<bool>? ConnectionChanged;

        /// <summary>连接、重连或异常等诊断信息。</summary>
        event Action<string>? DiagnosticMessage;

        /// <summary>未被协议帧解析器消费的原始数据，例如 ESP32 ROM 启动日志。</summary>
        event Action<byte[]>? UnframedDataReceived;

        /// <summary>启动传输（建立连接/开始监听）</summary>
        void Start();

        /// <summary>停止传输并释放资源</summary>
        void Stop();

        /// <summary>
        /// 发送一条 JSON 消息（自动补 \n）
        /// </summary>
        /// <param name="jsonLine">JSON 字符串（不含尾部换行）</param>
        /// <returns>发送成功返回 true；未连接或异常返回 false</returns>
        bool Send(string jsonLine);
    }
}
