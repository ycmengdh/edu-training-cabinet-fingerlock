namespace FingerprintLockManager
{
    /// <summary>
    /// 传输层抽象接口
    /// 统一 USB 串口、TCP 客户端、TCP 服务端三种链路的收发行为。
    /// 上层 MeshBridge 通过本接口与 Mesh 根节点通讯，不关心具体物理链路。
    /// 通讯格式：按行 JSON（每条消息以 \n 结尾）。
    /// </summary>
    public interface ITransport
    {
        /// <summary>是否已连接</summary>
        bool IsConnected { get; }

        /// <summary>收到一行 JSON 消息事件（已去除尾部换行）</summary>
        event Action<string> LineReceived;

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
