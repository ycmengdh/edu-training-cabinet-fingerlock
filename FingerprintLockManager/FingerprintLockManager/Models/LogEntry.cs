using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 日志模型（对应 logs 表）
    /// 记录开锁/关锁等操作的日志
    /// </summary>
    public class LogEntry
    {
        /// <summary>日志 ID（自增主键）</summary>
        [JsonProperty("id")]
        public long Id { get; set; }

        /// <summary>设备 ID</summary>
        [JsonProperty("device_id")]
        public string DeviceId { get; set; } = "";

        /// <summary>用户 ID（指纹验证失败时可能为空）</summary>
        [JsonProperty("user_id")]
        public string UserId { get; set; } = "";

        /// <summary>锁编号：0-3</summary>
        [JsonProperty("lock_id")]
        public int LockId { get; set; }

        /// <summary>操作类型：open / close</summary>
        [JsonProperty("action")]
        public string Action { get; set; } = "";

        /// <summary>操作结果：success / fail</summary>
        [JsonProperty("result")]
        public string Result { get; set; } = "";

        /// <summary>失败原因（成功时为空）</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; } = "";

        /// <summary>操作时间</summary>
        [JsonProperty("create_time")]
        public DateTime CreateTime { get; set; }
    }
}
