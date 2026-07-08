using FreeSql.DataAnnotations;

namespace FingerprintLockManager
{
    /// <summary>
    /// 日志模型（对应 logs 表）
    /// 记录开锁/关锁等操作的日志
    /// </summary>
    public class LogEntry
    {
        /// <summary>日志 ID（自增主键）</summary>
        [Column(IsPrimary = true, IsIdentity = true)]
        public long Id { get; set; }

        /// <summary>设备 ID</summary>
        [Column(IsNullable = true)]
        public string DeviceId { get; set; }

        /// <summary>用户 ID（指纹验证失败时可能为空）</summary>
        [Column(IsNullable = true)]
        public string UserId { get; set; }

        /// <summary>锁编号：0-3</summary>
        [Column(IsNullable = false)]
        public int LockId { get; set; }

        /// <summary>操作类型：open / close</summary>
        [Column(IsNullable = false)]
        public string Action { get; set; }

        /// <summary>操作结果：success / fail</summary>
        [Column(IsNullable = false)]
        public string Result { get; set; }

        /// <summary>失败原因（成功时为空）</summary>
        [Column(IsNullable = true)]
        public string Reason { get; set; }

        /// <summary>操作时间</summary>
        [Column(IsNullable = false)]
        public DateTime CreateTime { get; set; }
    }
}
