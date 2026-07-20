using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 本地缓存的指纹模板元数据。
    /// 录入指纹只是采集工作，模板存到 PC 或 SD 卡（与用户关联，不与设备关联）；
    /// 后续再做整理分配，下发到具体柜子。
    /// </summary>
    public class FingerprintTemplate
    {
        /// <summary>指纹 ID（全局唯一，对应柜子传感器内的指纹编号）</summary>
        [JsonProperty("fingerprint_id")]
        public int FingerprintId { get; set; }

        /// <summary>关联用户 ID（未分配时为 null）</summary>
        [JsonProperty("user_id")]
        public string? UserId { get; set; }

        /// <summary>关联用户姓名（缓存便于显示，不保证实时同步）</summary>
        [JsonProperty("user_name")]
        public string? UserName { get; set; }

        /// <summary>手指索引（默认 1 = 食指）</summary>
        [JsonProperty("finger_index")]
        public int FingerIndex { get; set; } = 1;

        /// <summary>录入时间</summary>
        [JsonProperty("enroll_time")]
        public DateTime EnrollTime { get; set; }

        /// <summary>模板字节数</summary>
        [JsonProperty("template_size")]
        public int TemplateSize { get; set; }

        /// <summary>采集设备 ID（哪个柜子录入的）</summary>
        [JsonProperty("source_device")]
        public string SourceDevice { get; set; } = "";

        /// <summary>备份状态："local" 仅本地 / "sd" 已上传 SD / "distributed" 已下发</summary>
        [JsonProperty("backup_status")]
        public string? BackupStatus { get; set; }

        /// <summary>备注</summary>
        [JsonProperty("note")]
        public string? Note { get; set; }

        [JsonIgnore]
        public string DisplayUser => string.IsNullOrWhiteSpace(UserName)
            ? (string.IsNullOrWhiteSpace(UserId) ? "（未关联）" : UserId)
            : $"{UserName} ({UserId})";

        [JsonIgnore]
        public string BackupStatusText => BackupStatus switch
        {
            "local" => "本地",
            "sd" => "已上传 SD",
            "distributed" => "已下发",
            _ => string.IsNullOrEmpty(BackupStatus) ? "本地" : BackupStatus
        };
    }
}
