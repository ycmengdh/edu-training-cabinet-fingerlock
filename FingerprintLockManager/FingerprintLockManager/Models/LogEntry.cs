namespace FingerprintLockManager
{
    /// <summary>
    /// 日志模型
    /// 记录开锁/关锁等操作日志。
    /// 数据持久化于根节点 SD 卡 logs.json。
    /// </summary>
    public class LogEntry
    {
        /// <summary>日志 ID（内存自增，写入 SD 卡时保持）</summary>
        public long Id { get; set; }

        /// <summary>设备 ID</summary>
        public string DeviceId { get; set; }

        /// <summary>用户 ID（指纹验证失败时可能为空）</summary>
        public string UserId { get; set; }

        /// <summary>锁编号：0-3</summary>
        public int LockId { get; set; }

        /// <summary>操作类型：open / close</summary>
        public string Action { get; set; }

        /// <summary>操作结果：success / fail</summary>
        public string Result { get; set; }

        /// <summary>失败原因（成功时为空）</summary>
        public string Reason { get; set; }

        /// <summary>操作时间</summary>
        public DateTime CreateTime { get; set; }
    }
}
