namespace FingerprintLockManager
{
    /// <summary>
    /// 下发任务模型（存于上位机 SQLite）
    /// 记录每一次权限/指纹下发任务，用于追踪下发状态和重发。
    /// 任务类型：teacher_broadcast（老师指纹广播）/ student_assign（学生权限下发）/
    ///           remove_user（删除柜子上的用户）/ delete_class（按班级批量删除）。
    /// </summary>
    public class DeployTask
    {
        /// <summary>任务 ID（SQLite 自增）</summary>
        public long Id { get; set; }

        /// <summary>任务类型：teacher_broadcast / student_assign / remove_user / delete_class</summary>
        public string TaskType { get; set; }

        /// <summary>目标用户 UserId（remove_user/student_assign 用）</summary>
        public string UserId { get; set; }

        /// <summary>目标柜子 DeviceId（student_assign/remove_user 用；teacher_broadcast 时为 "*"）</summary>
        public string DeviceId { get; set; }

        /// <summary>目标班级 ClassId（delete_class 用）</summary>
        public string ClassId { get; set; }

        /// <summary>下发载荷摘要（JSON，含指纹 ID、权限位等，便于审计）</summary>
        public string Payload { get; set; }

        /// <summary>触发操作人 UserId</summary>
        public string OperatorUserId { get; set; }

        /// <summary>任务状态：pending / running / success / partial / failed</summary>
        public string Status { get; set; }

        /// <summary>应接收的柜子总数（广播时为在线柜子数）</summary>
        public int TotalDevices { get; set; }

        /// <summary>已确认接收的柜子数</summary>
        public int AckedDevices { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreateTime { get; set; }

        /// <summary>完成时间（全部 ACK 或失败时）</summary>
        public DateTime? CompleteTime { get; set; }
    }
}
