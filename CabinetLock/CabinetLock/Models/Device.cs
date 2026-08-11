using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 设备模型（对应 devices 表）
    /// 描述已注册的 ESP32 指纹锁设备（Mesh 节点）
    /// </summary>
    public class Device
    {
        /// <summary>设备唯一标识（主键，非自增，如 CABINET_001）</summary>
        [JsonProperty("device_id")]
        public string DeviceId { get; set; } = "";

        /// <summary>设备名称，如 "实训柜1"</summary>
        [JsonProperty("device_name")]
        public string DeviceName { get; set; } = "";

        /// <summary>现场管理使用的可编辑唯一编号，不参与通讯路由。</summary>
        [JsonProperty("device_number")]
        public string DeviceNumber { get; set; } = "";

        /// <summary>设备 IP 地址（连接时记录）</summary>
        [JsonProperty("ip_address")]
        public string IpAddress { get; set; } = "";

        /// <summary>是否在线</summary>
        [JsonProperty("online")]
        public bool IsOnline { get; set; }

        /// <summary>注册时间</summary>
        [JsonProperty("register_time")]
        public DateTime RegisterTime { get; set; }

        /// <summary>最后在线时间</summary>
        [JsonProperty("last_online_time")]
        public DateTime? LastOnlineTime { get; set; }

        /// <summary>根节点最近收到状态的 Unix 时间。</summary>
        [JsonProperty("last_seen")]
        public long LastSeenUnix { get; set; }

        [JsonProperty("offline_time")]
        public long OfflineTimeUnix { get; set; }

        /// <summary>Mesh MAC 地址（Root 路由用，可为空）</summary>
        [JsonProperty("mesh_mac")]
        public string MeshMac { get; set; } = "";

        /// <summary>是否为 Mesh 根节点（默认 false）</summary>
        [JsonProperty("is_root")]
        public bool IsRoot { get; set; } = false;

        [JsonProperty("firmware_version")]
        public string FirmwareVersion { get; set; } = "";

        [JsonProperty("hardware_version")]
        public string HardwareVersion { get; set; } = "";

        [JsonIgnore]
        public string FirmwareVersionText => string.IsNullOrWhiteSpace(FirmwareVersion)
            ? "未上报" : FirmwareVersion.Trim();

        [JsonIgnore]
        public string HardwareVersionText => string.IsNullOrWhiteSpace(HardwareVersion)
            ? "未上报" : HardwareVersion.Trim();

        [JsonProperty("status")]
        public DeviceRuntimeStatus Status { get; set; } = new();

        [JsonIgnore]
        public DateTime? LastSeenTime => LastSeenUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(LastSeenUnix).LocalDateTime
            : LastOnlineTime;

        [JsonIgnore]
        public string OnlineStatusText => IsOnline ? "在线" : "离线";

        [JsonIgnore]
        public string DisplayIdentity => string.IsNullOrWhiteSpace(DeviceNumber)
            ? DeviceName
            : $"{DeviceNumber} · {DeviceName}";

        /// <summary>根节点全局权限版本，用于列表比对（运行时填充，不落盘）。</summary>
        [JsonIgnore]
        public uint RootPermissionVersion { get; set; }

        [JsonIgnore]
        public bool IsSelected { get; set; }

        [JsonIgnore]
        public bool MaintenanceActive { get; set; }

        [JsonIgnore]
        public int MaintenanceLockMask { get; set; }

        [JsonIgnore]
        public string MaintenanceSource { get; set; } = "";

        [JsonIgnore]
        public string MaintenanceStatusText => !MaintenanceActive
            ? "正常"
            : $"{(MaintenanceSource == "remote" ? "远程" : "本地")}维护 · {MaintenanceLockText}";

        [JsonIgnore]
        public string MaintenanceLockText => string.Join("、",
            Enumerable.Range(0, 4)
                .Where(index => (MaintenanceLockMask & (1 << index)) != 0)
                .Select(index => LockNaming.ToDisplayName(index)));

        [JsonIgnore]
        public string PermissionSyncText
        {
            get
            {
                if (!IsOnline) return "离线";
                if (RootPermissionVersion == 0 && Status.PermissionVersion == 0) return "-";
                return Status.PermissionVersion == RootPermissionVersion ? "已同步" : "落后";
            }
        }

        [JsonIgnore]
        public string PermissionVersionPairText
        {
            get
            {
                string reported = Status.PermissionVersion == 0
                    ? "未上报"
                    : Status.PermissionVersion.ToString();
                string expected = RootPermissionVersion == 0
                    ? "未生成"
                    : RootPermissionVersion.ToString();
                return $"{reported} / {expected}";
            }
        }

        [JsonIgnore]
        public string PermissionVersionExplanation =>
            $"柜端上报：{(Status.PermissionVersion == 0 ? "未上报" : Status.PermissionVersion.ToString())}\n" +
            $"当前期望：{(RootPermissionVersion == 0 ? "未生成" : RootPermissionVersion.ToString())}\n" +
            "这是由用户、班级、权限和指纹相关数据版本组合计算的 32 位同步标识，" +
            "用于判断柜端权限数据是否一致；不是固件版本，也不是文件内容校验码。";

        /// <summary>列表筛选/着色：offline / lagging / ok / unknown</summary>
        [JsonIgnore]
        public string AttentionKind
        {
            get
            {
                if (!IsOnline) return "offline";
                if (PermissionSyncText == "落后") return "lagging";
                if (PermissionSyncText == "已同步") return "ok";
                return "unknown";
            }
        }

        [JsonIgnore]
        public bool NeedsAttention => AttentionKind is "offline" or "lagging";

        [JsonIgnore]
        public string TimeSyncedText => Status.TimeSynced ? "是" : "否";

        /// <summary>指纹槽占用提示（模块约 200）。</summary>
        [JsonIgnore]
        public string FingerprintSlotHint
        {
            get
            {
                int count = Status?.FingerprintCount ?? 0;
                if (count <= 0) return "0";
                if (count >= 180) return $"{count}⚠";
                return count.ToString();
            }
        }
    }

    public class DeviceRuntimeStatus
    {
        [JsonProperty("uptime")]
        public long UptimeSeconds { get; set; }

        [JsonProperty("lock_status")]
        public int[] LockStatus { get; set; } = Array.Empty<int>();

        [JsonProperty("fingerprint_count")]
        public int FingerprintCount { get; set; }

        [JsonProperty("fingerprint_ready")]
        public bool FingerprintReady { get; set; }

        [JsonProperty("perm_count")]
        public int PermissionCount { get; set; }

        [JsonProperty("perm_version")]
        public uint PermissionVersion { get; set; }

        [JsonProperty("log_pending")]
        public int PendingLogCount { get; set; }

        [JsonProperty("mesh_layer")]
        public int MeshLayer { get; set; }

        [JsonProperty("child_count")]
        public int ChildCount { get; set; }

        [JsonProperty("time_synced")]
        public bool TimeSynced { get; set; }

        [JsonProperty("mesh_send_failures")]
        public int MeshSendFailures { get; set; }

        [JsonProperty("mesh_send_retries")]
        public int MeshSendRetries { get; set; }

        [JsonProperty("mesh_queue_full")]
        public int MeshQueueFull { get; set; }

        [JsonProperty("mesh_rx_drops")]
        public int MeshRxDrops { get; set; }

        [JsonProperty("mesh_rx_queue_high_water")]
        public int MeshRxQueueHighWater { get; set; }

        [JsonProperty("mesh_recoveries")]
        public int MeshRecoveries { get; set; }

        [JsonProperty("mesh_disconnects")]
        public int MeshDisconnects { get; set; }

        [JsonProperty("mesh_reconnects")]
        public int MeshReconnects { get; set; }

        [JsonProperty("mesh_stack_restarts")]
        public int MeshStackRestarts { get; set; }

        [JsonProperty("mesh_last_disconnect_reason")]
        public int MeshLastDisconnectReason { get; set; }

        [JsonProperty("serial_tx_drops")]
        public int SerialTxDrops { get; set; }

        [JsonProperty("serial_tx_failures")]
        public int SerialTxFailures { get; set; }

        [JsonIgnore]
        public string UptimeText => UptimeSeconds <= 0
            ? "-"
            : TimeSpan.FromSeconds(UptimeSeconds).TotalDays >= 1
                ? $"{(int)TimeSpan.FromSeconds(UptimeSeconds).TotalDays}天 {TimeSpan.FromSeconds(UptimeSeconds):hh\\:mm}"
                : TimeSpan.FromSeconds(UptimeSeconds).ToString(@"hh\:mm\:ss");
    }
}
