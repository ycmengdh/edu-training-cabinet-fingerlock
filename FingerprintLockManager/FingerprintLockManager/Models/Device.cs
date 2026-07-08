using FreeSql.DataAnnotations;

namespace FingerprintLockManager
{
    /// <summary>
    /// 设备模型（对应 devices 表）
    /// 描述已注册的 ESP32 指纹锁设备
    /// </summary>
    public class Device
    {
        /// <summary>设备唯一标识（主键，非自增，如 CABINET_001）</summary>
        [Column(IsPrimary = true, IsIdentity = false)]
        public string DeviceId { get; set; }

        /// <summary>设备名称，如 "实训柜1"</summary>
        [Column(IsNullable = false)]
        public string DeviceName { get; set; }

        /// <summary>设备 IP 地址（连接时记录）</summary>
        [Column(IsNullable = true)]
        public string IpAddress { get; set; }

        /// <summary>是否在线</summary>
        [Column(IsNullable = false)]
        public bool IsOnline { get; set; }

        /// <summary>注册时间</summary>
        [Column(IsNullable = false)]
        public DateTime RegisterTime { get; set; }

        /// <summary>最后在线时间</summary>
        [Column(IsNullable = true)]
        public DateTime? LastOnlineTime { get; set; }
    }
}
