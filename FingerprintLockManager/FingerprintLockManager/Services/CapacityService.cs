namespace FingerprintLockManager
{
    /// <summary>
    /// 设备容量监控服务（需求 10）
    ///
    /// 需求 10：ESP32-S3 N16R8 Flash 存本地老师/学生/指纹，最多 200 个。
    /// 上位机做数据统计管理、预警。到 190 提示清除不需要的用户。
    /// 可按班级删除（学生毕业全班删），归还 Flash 空间。
    ///
    /// 容量数据来源：向柜子发送 READ_CAPACITY 命令，柜子返回 used/max。
    /// </summary>
    public class CapacityService
    {
        /// <summary>查询某台柜子的本地容量（发送 READ_CAPACITY 命令）</summary>
        public void QueryCapacity(string deviceId)
        {
            var msg = Message.Create(Protocol.CmdReadCapacity, deviceId, new Dictionary<string, object>());
            App.MeshBridge.SendToDevice(deviceId, msg);
        }

        /// <summary>查询所有在线柜子的容量</summary>
        public void QueryAllCapacities()
        {
            var devices = DataStore.Current.GetDevices()
                .Where(d => d.IsOnline && !d.IsRoot);
            foreach (var device in devices)
            {
                QueryCapacity(device.DeviceId);
            }
        }

        /// <summary>判断是否达到预警阈值（190）</summary>
        public bool IsWarning(int usedCount)
        {
            return usedCount >= Protocol.CapacityWarnThreshold;
        }

        /// <summary>判断是否已满（200）</summary>
        public bool IsFull(int usedCount)
        {
            return usedCount >= Protocol.DeviceMaxUsers;
        }

        /// <summary>获取预警级别：normal / warning / full</summary>
        public string GetCapacityLevel(int usedCount)
        {
            if (IsFull(usedCount)) return "full";
            if (IsWarning(usedCount)) return "warning";
            return "normal";
        }

        /// <summary>获取容量利用率百分比</summary>
        public int GetUsagePercent(int usedCount)
        {
            return (int)Math.Round((double)usedCount / Protocol.DeviceMaxUsers * 100);
        }

        /// <summary>
        /// 统计某台柜子已授权的学生数（基于 DeviceAuthorization 表）
        /// 注意：这是上位机记录的授权数，可能与柜子实际存储数有差异（需以 READ_CAPACITY 为准）
        /// </summary>
        public int GetAuthorizedUserCount(string deviceId)
        {
            return DataStore.Current.GetDeviceAuthorizations()
                .Count(a => a.DeviceId == deviceId);
        }

        /// <summary>
        /// 获取需要清理的柜子列表（已达到预警阈值的）
        /// </summary>
        public List<DeviceCapacityInfo> GetWarningDevices()
        {
            var devices = DataStore.Current.GetDevices().Where(d => !d.IsRoot).ToList();
            var result = new List<DeviceCapacityInfo>();
            foreach (var device in devices)
            {
                int used = GetAuthorizedUserCount(device.DeviceId);
                var level = GetCapacityLevel(used);
                if (level != "normal")
                {
                    result.Add(new DeviceCapacityInfo
                    {
                        DeviceId = device.DeviceId,
                        DeviceName = device.DeviceName,
                        IsOnline = device.IsOnline,
                        UsedCount = used,
                        MaxCount = Protocol.DeviceMaxUsers,
                        UsagePercent = GetUsagePercent(used),
                        Level = level
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// 按班级从所有柜子删除学生（需求 10 学生毕业全班删）
        /// 委托 DeployService 逐台发送 DELETE_CLASS_USERS
        /// </summary>
        public void DeleteClassFromAllDevices(string classId, string? operatorUserId)
        {
            var devices = DataStore.Current.GetDevices()
                .Where(d => d.IsOnline && !d.IsRoot);
            foreach (var device in devices)
            {
                App.DeployService?.DeleteClassFromDevice(classId, device.DeviceId, operatorUserId);
            }

            // 删除根节点上的 DeviceAuthorization 记录
            DataStore.Current.MutateDeviceAuthorizations(list =>
            {
                // 查找该班级学生的授权记录并删除
                var studentIds = DataStore.Current.GetUsers()
                    .Where(u => u.Role == "student" && u.ClassId == classId)
                    .Select(u => u.UserId)
                    .ToHashSet();
                list.RemoveAll(a => studentIds.Contains(a.UserId));
            });
        }
    }

    /// <summary>设备容量信息</summary>
    public class DeviceCapacityInfo
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public bool IsOnline { get; set; }
        public int UsedCount { get; set; }
        public int MaxCount { get; set; }
        public int UsagePercent { get; set; }
        /// <summary>normal / warning / full</summary>
        public string Level { get; set; }
    }
}
