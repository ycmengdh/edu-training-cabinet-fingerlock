using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 通信消息模型
    /// 上位机与 ESP32 之间通过 JSON 消息进行通信，消息以换行符 \n 分隔。
    /// JSON 格式：
    /// {
    ///   "cmd": "FINGER_VERIFY",
    ///   "device_id": "CABINET_001",
    ///   "data": { ... },
    ///   "timestamp": "2024-01-01 00:00:00"
    /// }
    /// </summary>
    public class Message
    {
        /// <summary>命令字段（与 CommandType 枚举对应，如 "FINGER_VERIFY"）</summary>
        [JsonProperty("cmd")]
        public string Cmd { get; set; }

        /// <summary>设备 ID（如 CABINET_001）</summary>
        [JsonProperty("device_id")]
        public string DeviceId { get; set; }

        /// <summary>消息数据负载（可为 null，反序列化后为 JObject）</summary>
        [JsonProperty("data")]
        public object Data { get; set; }

        /// <summary>时间戳字符串（格式 yyyy-MM-dd HH:mm:ss）</summary>
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        /// <summary>
        /// 创建消息的静态方法
        /// </summary>
        /// <param name="cmd">命令字符串</param>
        /// <param name="deviceId">设备 ID</param>
        /// <param name="data">附加数据，可为 null</param>
        /// <returns>构造好的 Message 对象（自动填充当前时间戳）</returns>
        public static Message Create(string cmd, string deviceId, object data = null)
        {
            return new Message
            {
                Cmd = cmd,
                DeviceId = deviceId,
                Data = data,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        /// <summary>
        /// 将消息序列化为 JSON 字符串（带 \n 结尾，可直接写入网络流）
        /// </summary>
        /// <returns>以换行符结尾的 JSON 字符串</returns>
        public string ToJson()
        {
            return JsonHelper.Serialize(this) + "\n";
        }

        /// <summary>
        /// 从 JSON 字符串解析消息
        /// </summary>
        /// <param name="json">JSON 字符串（可带或不带尾部 \n）</param>
        /// <returns>解析后的 Message 对象；输入为空或解析失败时返回 null</returns>
        public static Message FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            // 去除尾部换行符，避免反序列化异常
            var trimmed = json.TrimEnd('\r', '\n');
            return JsonHelper.Deserialize<Message>(trimmed);
        }
    }
}
