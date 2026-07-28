using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 学生在单台柜机上的业务绑定。每台柜机只启用该学生的一枚指纹。
    /// </summary>
    public sealed class CabinetAssignment
    {
        [JsonProperty("device_id")]
        public string DeviceId { get; set; } = "";

        [JsonProperty("active_fingerprint_id")]
        public int? ActiveFingerprintId { get; set; }

        [JsonProperty("update_time")]
        public DateTime UpdateTime { get; set; }
    }
}
