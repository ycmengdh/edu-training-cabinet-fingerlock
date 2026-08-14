namespace CabinetLock
{
    /// <summary>
    /// 设备服务。设备注册和状态快照优先由根节点写入 devices.json；
    /// SD 不可用时降级为合并 MeshBridge 在线设备列表 + 本地缓存。
    /// </summary>
    public class DeviceService
    {
        private const string LegacyDefaultCabinetName = "实训柜";
        private const string FirmwareDefaultCabinetName = "Cabinet Node";
        private const string EspIdfDefaultCabinetName = "ESP-IDF Cabinet";
        private static readonly object DevicePersistenceLock = new();
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
            List<Device> merged = MergeOnlineDevices(devices);
            PersistNewlySeenDevices(merged);
            return merged;
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
                    DeviceName = "",
                    IpAddress = "",
                    IsOnline = client.IsOnline,
                    RegisterTime = client.ConnectTime == default ? lastSeen : client.ConnectTime,
                    LastOnlineTime = lastSeen,
                    LastSeenUnix = new DateTimeOffset(lastSeen).ToUnixTimeSeconds(),
                    MeshMac = client.MeshMac ?? "",
                    IsRoot = false,
                    FirmwareVersion = client.FirmwareVersion,
                    HardwareVersion = client.HardwareVersion,
                    Status = client.Status ?? new DeviceRuntimeStatus()
                };
                ApplyDefaultIdentity(device);
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

            // 再与本机业务库 devices 表合并，保证重启上位机后仍能看到历史柜子
            try
            {
                var cached = BusinessDatabase.ReadArray("devices");
                if (cached != null)
                {
                    foreach (var token in cached)
                    {
                        var d = token.ToObject<Device>();
                        if (d == null || string.IsNullOrWhiteSpace(d.DeviceId)) continue;
                        if (IsTrueRoot(d)) continue;
                        var live = devices.FirstOrDefault(x =>
                                (!string.IsNullOrEmpty(d.MeshMac) &&
                                 string.Equals(x.MeshMac, d.MeshMac, StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(x.DeviceId, d.DeviceId, StringComparison.OrdinalIgnoreCase));
                        if (live != null)
                        {
                            if (!string.IsNullOrWhiteSpace(d.DeviceNumber))
                                live.DeviceNumber = d.DeviceNumber.Trim();
                            if (HasStoredCabinetName(d.DeviceName, d.DeviceNumber))
                                live.DeviceName = d.DeviceName.Trim();
                            if (string.IsNullOrWhiteSpace(live.FirmwareVersion) &&
                                !string.IsNullOrWhiteSpace(d.FirmwareVersion))
                                live.FirmwareVersion = d.FirmwareVersion.Trim();
                            if (string.IsNullOrWhiteSpace(live.HardwareVersion) &&
                                !string.IsNullOrWhiteSpace(d.HardwareVersion))
                                live.HardwareVersion = d.HardwareVersion.Trim();
                            continue;
                        }
                        d.IsOnline = false;
                        d.IsRoot = false;
                        devices.Add(d);
                    }
                }
            }
            catch { /* 本地库可选 */ }

            PersistNewlySeenDevices(devices);
            return devices.OrderByDescending(d => d.IsOnline).ThenBy(d => d.DeviceId).ToList();
        }

        /// <summary>
        /// 判断是否为真正的 Mesh 根节点（柜子列表应过滤）。
        /// ROOT_* 是协议保留的根节点 ID；CABINET_* 即使误报 is_root 仍按柜机处理。
        /// </summary>
        public static bool IsTrueRoot(DeviceClient client)
        {
            if (client == null) return false;
            string id = client.DeviceId ?? "";
            if (id.Contains("CABINET", StringComparison.OrdinalIgnoreCase)) return false;
            if (id.StartsWith("ROOT_", StringComparison.OrdinalIgnoreCase)) return true;
            if (client.IsRoot && id.Contains("ROOT", StringComparison.OrdinalIgnoreCase))
                return true;
            if (client.IsRoot && !string.IsNullOrWhiteSpace(id))
                return true;
            return false;
        }

        public static bool IsTrueRoot(Device device)
        {
            if (device == null) return false;
            string id = device.DeviceId ?? "";
            if (id.Contains("CABINET", StringComparison.OrdinalIgnoreCase)) return false;
            if (id.StartsWith("ROOT_", StringComparison.OrdinalIgnoreCase)) return true;
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

            // 名称只由上位机业务库维护，注册报文中的名称不落库。
            ApplyDefaultIdentity(device);
            device.IpAddress = ipAddress;
            device.IsOnline = true;
            device.LastOnlineTime = DateTime.Now;
            _root.Save("devices", devices);
        }

        /// <summary>迁移固件占位名，并清空根节点的历史名称。</summary>
        public int NormalizeManagedDeviceNames()
        {
            List<Device> devices = _root.Read<Device>("devices");
            int changed = 0;
            foreach (Device device in devices)
            {
                string oldName = device.DeviceName ?? "";
                string oldNumber = device.DeviceNumber ?? "";
                ApplyDefaultIdentity(device);
                if (!string.Equals(oldName, device.DeviceName, StringComparison.Ordinal) ||
                    !string.Equals(oldNumber, device.DeviceNumber, StringComparison.Ordinal))
                    changed++;
            }

            if (changed > 0 && !_root.Save("devices", devices))
                throw new InvalidOperationException("柜机名称规范化保存失败");
            return changed;
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
        /// 保存首次见到的柜机，以及注册报文中发生变化的稳定元数据。
        /// 心跳和空字段不会覆盖已保存的固件/硬件版本。
        /// </summary>
        private static void PersistNewlySeenDevices(IReadOnlyCollection<Device> merged)
        {
            lock (DevicePersistenceLock)
            {
                try
                {
                    List<Device> stored = BusinessDatabase.ReadArray("devices")
                        .ToObject<List<Device>>() ?? new List<Device>();
                    bool changed = false;
                    foreach (Device device in merged)
                    {
                        if (IsTrueRoot(device)) continue;
                        Device? existing = stored.FirstOrDefault(candidate =>
                            IsSamePhysicalDevice(candidate, device));
                        if (existing == null)
                        {
                            stored.Add(CloneForStorage(device));
                            changed = true;
                            continue;
                        }
                        changed |= MergeStableReportedMetadata(existing, device);
                    }
                    if (!changed) return;

                    BusinessDatabase.ReplaceTable(
                        "devices",
                        Newtonsoft.Json.Linq.JArray.FromObject(stored),
                        BusinessDatabase.GetTableVersion("devices") + 1);
                }
                catch
                {
                    // 设备页仍返回实时合并结果；下一次刷新会再次尝试持久化。
                }
            }
        }

        private static bool MergeStableReportedMetadata(Device target, Device source)
        {
            bool changed = false;
            if (!HasStoredCabinetName(target.DeviceName, target.DeviceNumber) &&
                !string.IsNullOrWhiteSpace(source.DeviceName) &&
                !string.Equals(target.DeviceName, source.DeviceName,
                    StringComparison.Ordinal))
            {
                target.DeviceName = source.DeviceName.Trim();
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(target.DeviceNumber) &&
                !string.IsNullOrWhiteSpace(source.DeviceNumber))
            {
                target.DeviceNumber = source.DeviceNumber.Trim();
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(source.FirmwareVersion) &&
                !string.Equals(target.FirmwareVersion, source.FirmwareVersion,
                    StringComparison.Ordinal))
            {
                target.FirmwareVersion = source.FirmwareVersion.Trim();
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(source.HardwareVersion) &&
                !string.Equals(target.HardwareVersion, source.HardwareVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                target.HardwareVersion = source.HardwareVersion.Trim();
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(target.MeshMac) &&
                !string.IsNullOrWhiteSpace(source.MeshMac))
            {
                target.MeshMac = source.MeshMac.Trim();
                changed = true;
            }
            return changed;
        }

        private static Device CloneForStorage(Device source) => new()
        {
            DeviceId = source.DeviceId,
            DeviceName = source.DeviceName,
            DeviceNumber = source.DeviceNumber,
            IpAddress = source.IpAddress,
            IsOnline = source.IsOnline,
            RegisterTime = source.RegisterTime,
            LastOnlineTime = source.LastOnlineTime,
            LastSeenUnix = source.LastSeenUnix,
            OfflineTimeUnix = source.OfflineTimeUnix,
            MeshMac = source.MeshMac,
            IsRoot = source.IsRoot,
            FirmwareVersion = source.FirmwareVersion,
            HardwareVersion = source.HardwareVersion,
            Status = source.Status
        };

        public Device? GetByNumber(string deviceNumber)
        {
            if (string.IsNullOrWhiteSpace(deviceNumber)) return null;
            return GetAllDevices().FirstOrDefault(device =>
                string.Equals(device.DeviceNumber, deviceNumber.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        public bool UpdateDeviceInfo(
            Device target,
            string deviceName,
            string deviceNumber,
            out string error)
        {
            error = "";
            if (target == null)
            {
                error = "设备不存在";
                return false;
            }

            string name = deviceName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "柜子名称不能为空";
                return false;
            }
            if (name.Length > 32 || name.Any(char.IsControl))
            {
                error = "柜子名称长度不能超过 32 个字符，且不能包含控制字符";
                return false;
            }

            string number = deviceNumber?.Trim() ?? "";
            if (number.Length > 32 || number.Any(char.IsControl))
            {
                error = "设备编号长度不能超过 32 个字符，且不能包含控制字符";
                return false;
            }

            var devices = _root.Read<Device>("devices");
            if (!string.IsNullOrWhiteSpace(number) && devices.Any(device =>
                    !IsSamePhysicalDevice(device, target) &&
                    string.Equals(device.DeviceNumber, number,
                        StringComparison.OrdinalIgnoreCase)))
            {
                error = $"设备编号 {number} 已被其它柜机使用";
                return false;
            }

            var current = devices.FirstOrDefault(device => IsSamePhysicalDevice(device, target));
            if (current == null)
            {
                current = new Device
                {
                    DeviceId = target.DeviceId,
                    DeviceName = target.DeviceName,
                    DeviceNumber = target.DeviceNumber,
                    IpAddress = target.IpAddress,
                    MeshMac = target.MeshMac,
                    RegisterTime = target.RegisterTime == default ? DateTime.Now : target.RegisterTime,
                    IsOnline = target.IsOnline,
                    LastOnlineTime = target.LastOnlineTime,
                    LastSeenUnix = target.LastSeenUnix,
                    OfflineTimeUnix = target.OfflineTimeUnix,
                    IsRoot = target.IsRoot,
                    FirmwareVersion = target.FirmwareVersion,
                    HardwareVersion = target.HardwareVersion,
                    Status = target.Status
                };
                devices.Add(current);
            }
            current.DeviceName = name;
            current.DeviceNumber = number;
            if (!_root.Save("devices", devices))
            {
                error = "柜子信息保存失败";
                return false;
            }
            target.DeviceName = name;
            target.DeviceNumber = number;
            return true;
        }

        public bool UpdateDeviceNumber(Device target, string deviceNumber, out string error) =>
            UpdateDeviceInfo(target, target?.DeviceName ?? "", deviceNumber, out error);

        public bool DeleteDevice(
            Device target, out int removedStudentCount, out string error)
        {
            removedStudentCount = 0;
            error = "";
            if (target == null || string.IsNullOrWhiteSpace(target.DeviceId))
            {
                error = "柜机不存在";
                return false;
            }

            List<Device> originalDevices = _root.Read<Device>("devices");
            List<Device> devices = originalDevices.ToList();
            devices.RemoveAll(device => IsSamePhysicalDevice(device, target) ||
                string.Equals(device.DeviceId, target.DeviceId,
                    StringComparison.OrdinalIgnoreCase));
            if (!_root.Save("devices", devices))
            {
                error = "柜机记录删除失败";
                return false;
            }

            try
            {
                if (!App.CabinetBindingService.RemoveDeviceAssignments(
                        target.DeviceId, out removedStudentCount))
                {
                    _root.Save("devices", originalDevices);
                    error = "学生柜机绑定清理失败，已恢复柜机记录";
                    return false;
                }
            }
            catch
            {
                _root.Save("devices", originalDevices);
                throw;
            }

            App.CabinetSyncQueueService.RemoveDeviceJobs(target.DeviceId);
            App.MeshBridge.ForgetDevice(target.DeviceId, target.MeshMac);
            return true;
        }

        private static bool IsSamePhysicalDevice(Device left, Device right)
        {
            if (!string.IsNullOrWhiteSpace(left.MeshMac) &&
                !string.IsNullOrWhiteSpace(right.MeshMac))
            {
                return string.Equals(left.MeshMac, right.MeshMac,
                    StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(left.DeviceId, right.DeviceId,
                StringComparison.OrdinalIgnoreCase);
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
                        DeviceName = "",
                        IsOnline = true,
                        RegisterTime = client.ConnectTime == default ? DateTime.Now : client.ConnectTime,
                        LastOnlineTime = client.LastSeen == default ? DateTime.Now : client.LastSeen,
                        LastSeenUnix = client.LastSeen == default
                            ? DateTimeOffset.Now.ToUnixTimeSeconds()
                            : new DateTimeOffset(client.LastSeen).ToUnixTimeSeconds(),
                        MeshMac = client.MeshMac ?? "",
                        IsRoot = IsTrueRoot(client),
                        FirmwareVersion = client.FirmwareVersion,
                        HardwareVersion = client.HardwareVersion,
                        Status = client.LastStatusAt.HasValue
                            ? client.Status ?? new DeviceRuntimeStatus()
                            : new DeviceRuntimeStatus()
                    });
                    ApplyDefaultIdentity(devices[^1]);
                }
                else
                {
                    existing.IsOnline = true;
                    DateTime lastSeen = client.LastSeen == default ? DateTime.Now : client.LastSeen;
                    existing.LastOnlineTime = lastSeen;
                    existing.LastSeenUnix = new DateTimeOffset(lastSeen).ToUnixTimeSeconds();
                    if (!string.IsNullOrWhiteSpace(client.DeviceId))
                        existing.DeviceId = client.DeviceId;
                    if (!string.IsNullOrWhiteSpace(client.MeshMac))
                        existing.MeshMac = client.MeshMac;
                    // 仅真正的根节点才保留 IsRoot；CABINET_* 强制非根
                    existing.IsRoot = IsTrueRoot(client);
                    if (!string.IsNullOrWhiteSpace(client.FirmwareVersion))
                        existing.FirmwareVersion = client.FirmwareVersion;
                    if (!string.IsNullOrWhiteSpace(client.HardwareVersion))
                        existing.HardwareVersion = client.HardwareVersion;
                    ApplyLiveRuntimeStatus(existing, client);
                    ApplyDefaultIdentity(existing);
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

        private static void ApplyLiveRuntimeStatus(Device target, DeviceClient source)
        {
            if (source.LastStatusAt.HasValue && source.Status != null)
                target.Status = source.Status;
        }

        private static void ApplyDefaultIdentity(Device device)
        {
            if (IsTrueRoot(device))
            {
                device.DeviceName = "";
                return;
            }

            string identity = BuildCabinetIdentity(device.DeviceId, device.MeshMac);
            if (string.IsNullOrWhiteSpace(identity)) return;

            if (!HasStoredCabinetName(device.DeviceName, device.DeviceNumber))
                device.DeviceName = identity;
            if (string.IsNullOrWhiteSpace(device.DeviceNumber))
                device.DeviceNumber = identity;
        }

        private static string BuildCabinetIdentity(string? deviceId, string? meshMac)
        {
            string macHex = new string((meshMac ?? "").Where(Uri.IsHexDigit).ToArray())
                .ToUpperInvariant();
            if (macHex.Length == 12) return $"CAB_{macHex}";
            return deviceId?.Trim() ?? "";
        }

        private static bool HasStoredCabinetName(string? name, string? deviceNumber)
        {
            string value = name?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, FirmwareDefaultCabinetName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, EspIdfDefaultCabinetName,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // 旧版本自动生成“实训柜”且编号为空；把这类记录迁移到 CAB_<MAC>。
            return !string.Equals(value, LegacyDefaultCabinetName,
                       StringComparison.OrdinalIgnoreCase) ||
                   !string.IsNullOrWhiteSpace(deviceNumber);
        }
    }
}
