namespace FingerprintLockManager
{
    /// <summary>
    /// 指纹模板元数据模型
    /// 记录每个用户的指纹在根节点 SD 卡上的存储信息。
    /// 实际指纹模板二进制数据存于 SD 卡 /sdcard/fp_templates/FP_&lt;userId&gt;.bin。
    /// 本表仅存元数据，用于上位机追踪录入状态、下发来源等。
    /// 数据持久化于根节点 SD 卡 fingerprint_templates.json。
    /// </summary>
    public class FingerprintTemplate
    {
        /// <summary>用户 UserId（主键，一个用户一枚指纹）</summary>
        public string UserId { get; set; }

        /// <summary>AS608 模块内的指纹 ID（页号），下发到柜子时写入传感器的位置</summary>
        public int FingerprintId { get; set; }

        /// <summary>模板数据大小（字节，AS608 典型 512 字节）</summary>
        public int TemplateSize { get; set; }

        /// <summary>SD 卡上的文件名（如 FP_admin.bin）</summary>
        public string FileName { get; set; }

        /// <summary>录入时间</summary>
        public DateTime EnrollTime { get; set; }

        /// <summary>录入所在的柜子 DeviceId（需求 5：可在任意柜子录入）</summary>
        public string EnrollDeviceId { get; set; }

        /// <summary>已下发到哪些柜子（DeviceId 列表，逗号分隔，便于快速查询）</summary>
        public string DeployedDevices { get; set; }
    }
}
