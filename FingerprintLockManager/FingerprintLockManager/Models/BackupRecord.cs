namespace FingerprintLockManager
{
    /// <summary>
    /// 备份记录模型（存于上位机 SQLite）
    /// 需求 11：每次系统用户操作前自动备份上位机数据目录，备份仅用于还原。
    /// 备份内容：从根节点 SD 卡拉取的全量数据快照（users/classes/permissions/devices/fp_templates 等）。
    /// 数据以根节点为主，备份是只读副本。
    /// </summary>
    public class BackupRecord
    {
        /// <summary>备份 ID（时间戳格式，如 20260714120000）</summary>
        public string BackupId { get; set; }

        /// <summary>触发备份的操作描述（如 "分配柜子 CABINET_001 给学生 STU001"）</summary>
        public string TriggerAction { get; set; }

        /// <summary>操作人 UserId</summary>
        public string OperatorUserId { get; set; }

        /// <summary>备份文件路径（zip 压缩包，位于上位机数据目录 ./Backups/）</summary>
        public string FilePath { get; set; }

        /// <summary>备份文件大小（字节）</summary>
        public long FileSize { get; set; }

        /// <summary>包含的表清单（逗号分隔，如 "users,classes,device_authorizations"）</summary>
        public string Tables { get; set; }

        /// <summary>备份时的根节点全局版本号</summary>
        public long GlobalVersion { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreateTime { get; set; }
    }
}
