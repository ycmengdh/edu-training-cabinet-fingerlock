using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 上位机操作审计日志（本地持久化，与柜子开锁日志分离）。
    /// </summary>
    public class OperationLogEntry
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("time")]
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>操作者用户 ID（未登录时为空）</summary>
        [JsonProperty("operator_id")]
        public string OperatorId { get; set; } = "";

        /// <summary>操作者姓名</summary>
        [JsonProperty("operator_name")]
        public string OperatorName { get; set; } = "";

        /// <summary>模块：登录 / 用户 / 设备 / 权限 / 系统 等</summary>
        [JsonProperty("module")]
        public string Module { get; set; } = "";

        /// <summary>动作：登录成功、新增用户、远程开锁 等</summary>
        [JsonProperty("action")]
        public string Action { get; set; } = "";

        /// <summary>目标对象（用户ID、设备ID 等，可空）</summary>
        [JsonProperty("target")]
        public string Target { get; set; } = "";

        /// <summary>结果：success / fail / info</summary>
        [JsonProperty("result")]
        public string Result { get; set; } = "info";

        /// <summary>详情</summary>
        [JsonProperty("detail")]
        public string Detail { get; set; } = "";

        [JsonIgnore]
        public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
