using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 用户在单台柜机上的业务绑定。一个用户可以向同一柜机下发多枚指纹。
    /// </summary>
    public sealed class CabinetAssignment
    {
        [JsonProperty("device_id")]
        public string DeviceId { get; set; } = "";

        [JsonProperty("fingerprint_ids")]
        public List<int> FingerprintIds { get; set; } = new();

        /// <summary>null 表示继承用户全局权限；非 null 表示本柜独立锁位权限。</summary>
        [JsonProperty("lock_ids", NullValueHandling = NullValueHandling.Ignore)]
        public List<int>? LockIds { get; set; }

        [JsonProperty("update_time")]
        public DateTime UpdateTime { get; set; }
    }
}
