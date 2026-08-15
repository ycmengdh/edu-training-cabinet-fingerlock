using System.ComponentModel;
using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 设备模型（对应 devices 表）
    /// 描述已注册的 ESP32 指纹锁设备（Mesh 节点）
    /// </summary>
    public class Device : INotifyPropertyChanged
    {
        private int _runtimeDisplayHash;
        private bool _runtimeDisplayHashInitialized;

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

        /// <summary>实时快照对应的设备状态接收时间；仅用于防止元数据消息覆盖状态。</summary>
        [JsonIgnore]
        public DateTime? LastRuntimeStatusAt { get; set; }

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

        /// <summary>按当前业务数据生成的本柜权限/用户指纹记录数。</summary>
        [JsonIgnore]
        public int ExpectedFingerprintCount { get; set; } = -1;

        /// <summary>本次程序运行中逐枚核验成功时对应的数据版本。</summary>
        [JsonIgnore]
        public uint FingerprintVerificationVersion { get; set; }

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
        public string DeviceStateText => MaintenanceActive ? "维护" : "正常";

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
                if (RootPermissionVersion == 0 || Status.PermissionVersion == 0) return "未知";
                if (Status.PermissionVersion != RootPermissionVersion) return "落后";
                if (ExpectedFingerprintCount >= 0 &&
                    Status.PermissionCount < ExpectedFingerprintCount) return "不完整";
                return "已同步";
            }
        }

        [JsonIgnore]
        public string FingerprintSyncText
        {
            get
            {
                if (!IsOnline) return "离线";
                if (ExpectedFingerprintCount < 0 || RootPermissionVersion == 0) return "未知";
                if (Status.FingerprintCount < ExpectedFingerprintCount) return "缺失";
                return FingerprintVerificationVersion == RootPermissionVersion
                    ? "已核验" : "待核验";
            }
        }

        [JsonIgnore]
        public string DataSyncText
        {
            get
            {
                if (!IsOnline) return "离线";
                if (PermissionSyncText == "落后") return "权限落后";
                if (PermissionSyncText == "不完整") return "权限不完整";
                if (PermissionSyncText == "未知") return "未知";
                return FingerprintSyncText switch
                {
                    "已核验" => "已同步",
                    "缺失" => "指纹缺失",
                    "待核验" => "待核验",
                    _ => "未知"
                };
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
                if (DataSyncText == "已同步") return "ok";
                if (DataSyncText is "权限落后" or "权限不完整" or "指纹缺失")
                    return "lagging";
                return "unknown";
            }
        }

        [JsonIgnore]
        public bool NeedsAttention => AttentionKind != "ok";

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

        [JsonIgnore]
        public string FingerprintPermissionCountText =>
            $"{FingerprintSlotHint} / {Status?.PermissionCount ?? 0}";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyRuntimeDataChanged() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        public void CaptureRuntimeDataSnapshot()
        {
            _runtimeDisplayHash = ComputeRuntimeDisplayHash();
            _runtimeDisplayHashInitialized = true;
        }

        public bool NotifyRuntimeDataChangedIfNeeded()
        {
            int current = ComputeRuntimeDisplayHash();
            if (_runtimeDisplayHashInitialized && current == _runtimeDisplayHash) return false;
            _runtimeDisplayHash = current;
            _runtimeDisplayHashInitialized = true;
            NotifyRuntimeDataChanged();
            return true;
        }

        public void NotifySelectionChanged() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));

        private int ComputeRuntimeDisplayHash()
        {
            var hash = new HashCode();
            hash.Add(DeviceId, StringComparer.OrdinalIgnoreCase);
            hash.Add(DeviceName, StringComparer.Ordinal);
            hash.Add(DeviceNumber, StringComparer.Ordinal);
            hash.Add(IsOnline);
            DateTime? displayedLastSeen = LastSeenTime;
            hash.Add(displayedLastSeen.HasValue
                ? displayedLastSeen.Value.Ticks / TimeSpan.TicksPerMinute
                : 0L);
            hash.Add(MeshMac, StringComparer.OrdinalIgnoreCase);
            hash.Add(FirmwareVersion, StringComparer.Ordinal);
            hash.Add(HardwareVersion, StringComparer.Ordinal);
            hash.Add(Status?.FingerprintCount ?? 0);
            hash.Add(Status?.PermissionCount ?? 0);
            hash.Add(Status?.PermissionVersion ?? 0);
            hash.Add(RootPermissionVersion);
            hash.Add(ExpectedFingerprintCount);
            hash.Add(FingerprintVerificationVersion);
            hash.Add(MaintenanceActive);
            hash.Add(MaintenanceLockMask);
            hash.Add(MaintenanceSource, StringComparer.Ordinal);
            return hash.ToHashCode();
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
