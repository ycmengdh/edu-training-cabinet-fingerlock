using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 设备配置模型（AP 模式 / STA 模式配置用）
    /// 用于 READ_CONFIG / WRITE_CONFIG 命令的配置数据传输，亦可持久化保存
    /// </summary>
    public class DeviceConfig
    {
        /// <summary>设备 ID（主键）</summary>
        [JsonProperty("device_id")]
        public string DeviceId { get; set; } = "";

        /// <summary>设备名称</summary>
        [JsonProperty("device_name")]
        public string DeviceName { get; set; } = "";

        /// <summary>WiFi SSID</summary>
        [JsonProperty("wifi_ssid")]
        public string WifiSsid { get; set; } = "";

        /// <summary>WiFi 密码</summary>
        [JsonProperty("wifi_password")]
        public string WifiPassword { get; set; } = "";

        /// <summary>上位机服务器 IP（STA 模式下 ESP32 连接目标）</summary>
        [JsonProperty("server_ip")]
        public string ServerIp { get; set; } = "";

        /// <summary>上位机服务器端口</summary>
        [JsonProperty("server_port")]
        public int ServerPort { get; set; }

        /// <summary>静态 IP（启用静态 IP 时使用）</summary>
        [JsonProperty("static_ip")]
        public string StaticIp { get; set; } = "";

        /// <summary>是否启用静态 IP</summary>
        [JsonProperty("static_ip_enable")]
        public bool StaticIpEnable { get; set; }

        /// <summary>网关地址</summary>
        [JsonProperty("gateway")]
        public string Gateway { get; set; } = "";

        /// <summary>子网掩码</summary>
        [JsonProperty("subnet")]
        public string Subnet { get; set; } = "";
    }
}
