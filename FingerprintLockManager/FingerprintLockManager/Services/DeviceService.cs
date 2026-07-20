namespace FingerprintLockManager
{
    /// <summary>
    /// 设备服务。设备注册和状态快照优先由根节点写入 devices.json；
    /// SD 不可用时降级为合并 MeshBridge 在线设备列表 + 本地缓存。
    /// </summary>
    public class DeviceService
    {
        private readonly RootDataService _root = new RootDataService();

        /// <summary>
        /// 获取全部设备（SD 不可用且无本地缓存时抛 RootDataUnavailableException）。
        /// 返回结果会与 MeshBridge 在线设备列表合并：补全缺失的设备、刷新在线状态。
        /// </summary>
        public List<Device> GetAllDevices()
        {
            List<Device> devices;
            try
            {
                devices = _root.Read<Device>("devices")
                    .OrderBy(d => d.DeviceId).ToList();
            }
            catch (RootDataUnavailableException)
            {
                // SD/缓存都不可用时，仍返回 Mesh 实时设备，保证设备页不空白
                return GetLiveDevices();
            }
            catch
            {
                devices = new List<Device>();
            }
            return MergeOnlineDevices(devices);
        }

        /// <summary>获取所有在线设备</summary>
        public List<Device> GetOnlineDevices()
        {
            return GetAllDevices().Where(d => d.IsOnline).ToList();
        }

        /// <summary>
        /// Mesh 曾经通讯过的柜子列表（含离线）。
        /// 见过就保留；IsOnline 反映当前心跳是否超时。
        /// </summary>
        public List<Device> GetLiveDevices()
        {
            var devices = new List<Device>();
            // 使用 KnownDevices：离线节点也保留在列表中
            foreach (var client in App.MeshBridge.GetKnownDevices())
            {
                if (IsTrueRoot(client)) continue;
                DateTime lastSeen = client.LastSeen == default ? client.ConnectTime : client.LastSeen;
                if (lastSeen == default) lastSeen = DateTime.Now;
                var device = new Device
                {
                    DeviceId = string.IsNullOrWhiteSpace(client.DeviceId)
                        ? (client.MeshMac ?? "") : client.DeviceId,
                    DeviceName = string.IsNullOrWhiteSpace(client.DeviceName)
                        ? (string.IsNullOrWhiteSpace(client.DeviceId) ? client.MeshMac : client.DeviceId)
                        : client.DeviceName,
                    IpAddress = "",
                    IsOnline = client.IsOnline,
                    RegisterTime = client.ConnectTime == default ? lastSeen : client.ConnectTime,
                    LastOnlineTime = lastSeen,
                    LastSeenUnix = new DateTimeOffset(lastSeen).ToUnixTimeSeconds(),
                    MeshMac = client.MeshMac ?? "",
                    IsRoot = false
                };
                // DeviceId 为空时用 MAC 兜底，避免被丢弃导致列表空白
                if (string.IsNullOrWhiteSpace(device.DeviceId))
                {
                    if (string.IsNullOrWhiteSpace(device.MeshMac)) continue;
                    device.DeviceId = device.MeshMac;
                    if (string.IsNullOrWhiteSpace(device.DeviceName))
                        device.DeviceName = device.MeshMac;
                }
                devices.Add(device);
            }

            // 再与本地缓存 devices 表合并，保证重启上位机后仍能看到历史柜子
            try
            {
                var cached = LocalCacheService.ReadTable("devices");
                if (cached != null)
                {
                    foreach (var token in cached)
                    {
                        var d = token.ToObject<Device>();
                        if (d == null || string.IsNullOrWhiteSpace(d.DeviceId)) continue;
                        if (IsTrueRoot(d)) continue;
                        if (devices.Any(x =>
                                (!string.IsNullOrEmpty(d.MeshMac) &&
                                 string.Equals(x.MeshMac, d.MeshMac, StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(x.DeviceId, d.DeviceId, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        d.IsOnline = false;
                        d.IsRoot = false;
                        devices.Add(d);
                    }
                }
            }
            catch { /* 缓存可选 */ }

            PersistSeenDevices(devices);
            return devices.OrderByDescending(d => d.IsOnline).ThenBy(d => d.DeviceId).ToList();
        }

        /// <summary>把见过的柜子写入本地缓存，重启后仍显示。</summary>
        private static void PersistSeenDevices(List<Device> devices)
        {
            try
            {
                var arr = new Newtonsoft.Json.Linq.JArray();
                foreach (var d in devices)
                {
                    if (IsTrueRoot(d)) continue;
                    arr.Add(Newtonsoft.Json.Linq.JObject.FromObject(d));
                }
                LocalCacheService.WriteTable("devices", arr);
            }
            catch { }
        }

        /// <summary>
        /// 判断是否为真正的 Mesh 根节点（柜子列表应过滤）。
        /// 规则收紧：只有明确 is_root 且 ID 像 ROOT 时才过滤；
        /// 宁可把根节点显示出来，也不要把柜子误过滤成空列表。
        /// </summary>
        public static bool IsTrueRoot(DeviceClient client)
        {
            if (client == null) return false;
            string id = client.DeviceId ?? "";
            if (id.Contains("CABINET", StringComparison.OrdinalIgnoreCase)) return false;
            // 必须同时满足：标记为根 + 名称像根节点
            if (client.IsRoot && id.Contains("ROOT", StringComparison.OrdinalIgnoreCase))
                return true;
            // 仅有 is_root、没有 CABINET/ROOT 关键字时，也当根（Root 默认 device_id）
            if (client.IsRoot && !string.IsNullOrWhiteSpace(id))
                return true;
            return false;
        }

        public static bool IsTrueRoot(Device device)
        {
            if (device == null) return false;
            string id = device.DeviceId ?? "";
            if (id.Contains("CABINET", StringComparison.OrdinalIgnoreCase)) return false;
            if (device.IsRoot && id.Contains("ROOT", StringComparison.OrdinalIgnoreCase))
                return true;
            if (device.IsRoot && !string.IsNullOrWhiteSpace(id))
                return true;
            return false;
        }

        /// <summary>注册设备：SD 可用时同时写 SD 和本地缓存；SD 不可用时仅写本地缓存</summary>
        public void RegisterDevice(string deviceId, string deviceName, string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var devices = _root.Read<Device>("devices");
            var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (device == null)
            {
                device = new Device
                {
                    DeviceId = deviceId,
                    RegisterTime = DateTime.Now
                };
                devices.Add(device);
            }

            if (!string.IsNullOrWhiteSpace(deviceName)) device.DeviceName = deviceName;
            device.DeviceName ??= deviceId;
            device.IpAddress = ipAddress;
            device.IsOnline = true;
            device.LastOnlineTime = DateTime.Now;
            _root.Save("devices", devices);
        }

        /// <summary>更新设备在线状态</summary>
        public void UpdateDeviceStatus(string deviceId, bool isOnline)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var devices = _root.Read<Device>("devices");
            var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (device == null) return;
            device.IsOnline = isOnline;
            device.LastOnlineTime = DateTime.Now;
            _root.Save("devices", devices);
        }

        /// <summary>获取单个设备</summary>
        public Device? GetDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            return GetAllDevices().FirstOrDefault(d => d.DeviceId == deviceId);
        }

        /// <summary>
        /// 合并 MeshBridge 在线设备列表：
        /// 1) Mesh 在线但 SD/本地缓存不存在 → 补充新条目
        /// 2) Mesh 在线且 SD/本地缓存已存在 → 刷新 IsOnline / LastOnlineTime / DeviceName / MeshMac
        /// 3) Mesh 不在线但缓存标为在线 → 标记为离线
        /// </summary>
        private static List<Device> MergeOnlineDevices(List<Device> devices)
        {
            // 在线合并必须忽略大小写：SD 与 Mesh 的 device_id 可能大小写不一致
            var onlineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var client in App.MeshBridge.GetOnlineDevices())
            {
                if (string.IsNullOrWhiteSpace(client.DeviceId)) continue;
                onlineIds.Add(client.DeviceId);

                // 优先按 MAC 合并，其次逻辑 device_id
                var existing = devices.FirstOrDefault(d =>
                        !string.IsNullOrWhiteSpace(client.MeshMac) &&
                        !string.IsNullOrWhiteSpace(d.MeshMac) &&
                        string.Equals(d.MeshMac, client.MeshMac, StringComparison.OrdinalIgnoreCase))
                    ?? devices.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, client.DeviceId, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    // SD 中不存在但 Mesh 在线：补充条目（否则界面永远看不到“仅 Mesh 可见”的柜子）
                    devices.Add(new Device
                    {
                        DeviceId = client.DeviceId,
                        DeviceName = string.IsNullOrWhiteSpace(client.DeviceName)
                            ? client.DeviceId : client.DeviceName,
                        IsOnline = true,
                        RegisterTime = client.ConnectTime == default ? DateTime.Now : client.ConnectTime,
                        LastOnlineTime = client.LastSeen == default ? DateTime.Now : client.LastSeen,
                        LastSeenUnix = client.LastSeen == default
                            ? DateTimeOffset.Now.ToUnixTimeSeconds()
                            : new DateTimeOffset(client.LastSeen).ToUnixTimeSeconds(),
                        MeshMac = client.MeshMac ?? "",
                        IsRoot = IsTrueRoot(client)
                    });
                }
                else
                {
                    existing.IsOnline = true;
                    DateTime lastSeen = client.LastSeen == default ? DateTime.Now : client.LastSeen;
                    existing.LastOnlineTime = lastSeen;
                    existing.LastSeenUnix = new DateTimeOffset(lastSeen).ToUnixTimeSeconds();
                    if (!string.IsNullOrWhiteSpace(client.DeviceName))
                        existing.DeviceName = client.DeviceName;
                    if (!string.IsNullOrWhiteSpace(client.DeviceId))
                        existing.DeviceId = client.DeviceId;
                    if (!string.IsNullOrWhiteSpace(client.MeshMac))
                        existing.MeshMac = client.MeshMac;
                    // 仅真正的根节点才保留 IsRoot；CABINET_* 强制非根
                    existing.IsRoot = IsTrueRoot(client);
                }
            }

            // Mesh 上不在线且非根节点 → 强制标记为离线
            foreach (var d in devices)
            {
                if (d.DeviceId.Contains("CABINET", StringComparison.OrdinalIgnoreCase))
                    d.IsRoot = false;
                if (!onlineIds.Contains(d.DeviceId) && !IsTrueRoot(d))
                {
                    d.IsOnline = false;
                }
            }
            return devices;
        }
    }
}
