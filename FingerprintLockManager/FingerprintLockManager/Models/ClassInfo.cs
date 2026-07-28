using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>班级模型（对应根节点 classes.json）</summary>
    public class ClassInfo
    {
        [JsonProperty("class_id")]
        public string ClassId { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("create_time")]
        public DateTime CreateTime { get; set; }

        [JsonIgnore]
        public string StatusText => Enabled ? "启用" : "停用";

        [JsonIgnore]
        public string TeacherText { get; set; } = "未分配";

        [JsonIgnore]
        public int StudentCount { get; set; }
    }
}
