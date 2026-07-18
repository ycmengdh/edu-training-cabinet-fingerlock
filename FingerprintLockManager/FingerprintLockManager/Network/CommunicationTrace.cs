namespace FingerprintLockManager
{
    public enum CommunicationDirection
    {
        System,
        Transmit,
        Receive
    }

    /// <summary>供通讯测试窗口展示的一条链路记录。</summary>
    public sealed class CommunicationTraceEntry
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public CommunicationDirection Direction { get; init; }
        public string Category { get; init; } = "";
        public string Content { get; init; } = "";

        public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
        public string DirectionText => Direction switch
        {
            CommunicationDirection.Transmit => "发送",
            CommunicationDirection.Receive => "接收",
            _ => "状态"
        };

        public string CopyText => $"[{TimeText}] [{DirectionText}] [{Category}] {Content}";
    }
}
