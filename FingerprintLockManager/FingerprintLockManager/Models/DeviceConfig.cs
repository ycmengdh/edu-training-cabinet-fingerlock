namespace FingerprintLockManager
{
    /// <summary>
    /// 设备配置模型（AP 模式 / STA 模式配置用）
    /// 用于 READ_CONFIG / WRITE_CONFIG 命令的配置数据传输。
    /// 不持久化于 SD 卡表，仅作为通讯 DTO。
    /// </summary>
    public class DeviceConfig
    {
        /// <summary>设备 ID</summary>
        public string DeviceId { get; set; }

        /// <summary>设备名称</summary>
        public string DeviceName { get; set; }

        /// <summary>WiFi SSID</summary>
        public string WifiSsid { get; set; }

        /// <summary>WiFi 密码</summary>
        public string WifiPassword { get; set; }

        /// <summary>上位机服务器 IP（STA 模式下 ESP32 连接目标）</summary>
        public string ServerIp { get; set; }

        /// <summary>上位机服务器端口</summary>
        public int ServerPort { get; set; }

        /// <summary>静态 IP（启用静态 IP 时使用）</summary>
        public string StaticIp { get; set; }

        /// <summary>是否启用静态 IP</summary>
        public bool StaticIpEnable { get; set; }

        /// <summary>网关地址</summary>
        public string Gateway { get; set; }

        /// <summary>子网掩码</summary>
        public string Subnet { get; set; }
    }
}
