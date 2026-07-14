namespace FingerprintLockManager
{
    /// <summary>
    /// 下发状态明细模型（存于上位机 SQLite）
    /// 记录某个下发任务针对每台柜子的接收状态，用于"下发状态监控"页面展示。
    /// 需求 7：上位机要显示发送老师权限的状态，确保录入后每台设备都能接收到。
    /// </summary>
    public class DeployStatus
    {
        /// <summary>状态记录 ID（SQLite 自增）</summary>
        public long Id { get; set; }

        /// <summary>所属下发任务 ID（关联 DeployTask.Id）</summary>
        public long TaskId { get; set; }

        /// <summary>目标柜子 DeviceId</summary>
        public string DeviceId { get; set; }

        /// <summary>该柜子的接收状态：pending / success / failed</summary>
        public string Status { get; set; }

        /// <summary>失败原因（成功时为空）</summary>
        public string ErrorMessage { get; set; }

        /// <summary>重试次数</summary>
        public int RetryCount { get; set; }

        /// <summary>最后重试时间</summary>
        public DateTime? LastRetryTime { get; set; }

        /// <summary>柜子确认接收时间（收到 ACK 时）</summary>
        public DateTime? AckTime { get; set; }
    }
}
