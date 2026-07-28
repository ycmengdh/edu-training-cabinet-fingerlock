using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 通信消息模型（上位机内存表示）。
    /// 线上主路径：A5/5A 帧 + B1/0F 二进制信封；cmd 以 cmd_id 传输，
    /// data 对复杂命令序列化为 UTF-8 JSON 放入信封 payload。
    /// 本类的 JSON 字段布局保留用于：
    /// 1) 调试日志与兼容解析；2) AppMessageMapper 与固件 payload 对齐。
    /// </summary>
    public class Message
    {
        /// <summary>消息 ID（ACK 时原样回传，用于命令确认匹配）</summary>
        [JsonProperty("msg_id")]
        public string MsgId { get; set; } = "";

        /// <summary>命令字段（与 CommandType 枚举对应）</summary>
        [JsonProperty("cmd")]
        public string Cmd { get; set; } = "";

        /// <summary>目标设备 ID（如 CABINET_001）</summary>
        [JsonProperty("device_id")]
        public string DeviceId { get; set; } = "";

        /// <summary>原始发送方设备 ID（Root 转发时区分真实来源节点）</summary>
        [JsonProperty("source_device_id")]
        public string SourceDeviceId { get; set; } = "";

        /// <summary>消息数据负载（可为 null，反序列化后为 JObject）</summary>
        [JsonProperty("data")]
        public object? Data { get; set; }

        /// <summary>时间戳字符串（格式 yyyy-MM-dd HH:mm:ss）</summary>
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = "";

        /// <summary>HMAC 秒级时间戳（可选）</summary>
        [JsonProperty("hmac_ts")]
        public long? HmacTs { get; set; }

        /// <summary>HMAC 随机数（可选）</summary>
        [JsonProperty("hmac_nonce")]
        public string? HmacNonce { get; set; }

        /// <summary>HMAC-SHA256 十六进制签名（可选）</summary>
        [JsonProperty("hmac_sig")]
        public string? HmacSig { get; set; }

        /// <summary>
        /// 创建消息的静态方法
        /// </summary>
        /// <param name="cmd">命令字符串</param>
        /// <param name="deviceId">设备 ID</param>
        /// <param name="data">附加数据，可为 null</param>
        /// <returns>构造好的 Message 对象（自动填充消息 ID 与当前时间戳）</returns>
        public static Message Create(string cmd, string deviceId, object? data = null)
        {
            var message = new Message
            {
                MsgId = GenerateMsgId(),
                Cmd = cmd,
                DeviceId = deviceId,
                Data = data,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            MessageHmac.ApplyIfEnabled(message);
            return message;
        }

        /// <summary>
        /// 创建消息的静态方法（指定消息 ID，用于 ACK 匹配）
        /// </summary>
        public static Message Create(string msgId, string cmd, string deviceId, object? data = null)
        {
            var message = new Message
            {
                MsgId = msgId,
                Cmd = cmd,
                DeviceId = deviceId,
                Data = data,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            MessageHmac.ApplyIfEnabled(message);
            return message;
        }

        /// <summary>
        /// 生成消息 ID：滚动 ushort（1..65535），与固件二进制 msg_id 对齐。
        /// 字符串形式便于 CommandService 字典匹配。
        /// </summary>
        private static int _msgIdSeq;
        private static string GenerateMsgId()
        {
            int next = System.Threading.Interlocked.Increment(ref _msgIdSeq);
            ushort id = (ushort)(next & 0xFFFF);
            if (id == 0) id = 1;
            return id.ToString();
        }

        /// <summary>
        /// 将消息序列化为 JSON 字符串（带 \n 结尾，供兼容调用方使用；网络传输由 Transport 负责封帧）
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
        public static Message? FromJson(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            // 去除尾部换行符，避免反序列化异常
            var trimmed = json.TrimEnd('\r', '\n');
            return JsonHelper.Deserialize<Message>(trimmed);
        }
    }
}
