namespace FingerprintLockManager
{
    /// <summary>
    /// 设备模型
    /// 描述已注册的 ESP32 指纹锁设备（Mesh 节点）。
    /// 数据持久化于根节点 SD 卡 devices.json。
    /// </summary>
    public class Device
    {
        /// <summary>设备唯一标识，如 CABINET_001</summary>
        public string DeviceId { get; set; }

        /// <summary>设备名称，如 "实训柜1"</summary>
        public string DeviceName { get; set; }

        /// <summary>设备 IP 地址（连接时记录）</summary>
        public string IpAddress { get; set; }

        /// <summary>是否在线</summary>
        public bool IsOnline { get; set; }

        /// <summary>注册时间</summary>
        public DateTime RegisterTime { get; set; }

        /// <summary>最后在线时间</summary>
        public DateTime? LastOnlineTime { get; set; }

        /// <summary>Mesh MAC 地址（Root 路由用，可为空）</summary>
        public string MeshMac { get; set; }

        /// <summary>是否为 Mesh 根节点（默认 false）</summary>
        public bool IsRoot { get; set; } = false;
    }
}
