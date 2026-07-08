using FreeSql.DataAnnotations;

namespace FingerprintLockManager
{
    /// <summary>
    /// 设备配置模型（AP 模式 / STA 模式配置用）
    /// 用于 READ_CONFIG / WRITE_CONFIG 命令的配置数据传输，亦可持久化保存
    /// </summary>
    public class DeviceConfig
    {
        /// <summary>设备 ID（主键）</summary>
        [Column(IsPrimary = true, IsIdentity = false)]
        public string DeviceId { get; set; }

        /// <summary>设备名称</summary>
        [Column(IsNullable = true)]
        public string DeviceName { get; set; }

        /// <summary>WiFi SSID</summary>
        [Column(IsNullable = true)]
        public string WifiSsid { get; set; }

        /// <summary>WiFi 密码</summary>
        [Column(IsNullable = true)]
        public string WifiPassword { get; set; }

        /// <summary>上位机服务器 IP（STA 模式下 ESP32 连接目标）</summary>
        [Column(IsNullable = true)]
        public string ServerIp { get; set; }

        /// <summary>上位机服务器端口</summary>
        [Column(IsNullable = true)]
        public int ServerPort { get; set; }

        /// <summary>静态 IP（启用静态 IP 时使用）</summary>
        [Column(IsNullable = true)]
        public string StaticIp { get; set; }

        /// <summary>是否启用静态 IP</summary>
        [Column(IsNullable = false)]
        public bool StaticIpEnable { get; set; }

        /// <summary>网关地址</summary>
        [Column(IsNullable = true)]
        public string Gateway { get; set; }

        /// <summary>子网掩码</summary>
        [Column(IsNullable = true)]
        public string Subnet { get; set; }
    }
}
