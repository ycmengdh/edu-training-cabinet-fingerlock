namespace FingerprintLockManager
{
    /// <summary>
    /// 设备管理服务
    /// 负责 ESP32 指纹锁设备的注册、查询与在线状态维护。
    /// 数据持久化于根节点 SD 卡 devices.json。
    /// </summary>
    public class DeviceService
    {
        /// <summary>获取所有设备</summary>
        public List<Device> GetAllDevices()
        {
            try
            {
                return DataStore.Current.GetDevices()
                    .OrderBy(d => d.RegisterTime)
                    .ToList();
            }
            catch
            {
                return new List<Device>();
            }
        }

        /// <summary>获取在线设备</summary>
        public List<Device> GetOnlineDevices()
        {
            try
            {
                return DataStore.Current.GetDevices()
                    .Where(d => d.IsOnline)
                    .OrderBy(d => d.DeviceId)
                    .ToList();
            }
            catch
            {
                return new List<Device>();
            }
        }

        /// <summary>注册或更新设备（ESP32 连接时调用）</summary>
        public void RegisterDevice(string deviceId, string deviceName, string ipAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId)) return;

                DataStore.Current.MutateDevices(list =>
                {
                    int idx = list.FindIndex(d => d.DeviceId == deviceId);
                    if (idx >= 0)
                    {
                        list[idx].DeviceName = string.IsNullOrEmpty(deviceName) ? list[idx].DeviceName : deviceName;
                        list[idx].IpAddress = ipAddress;
                        list[idx].IsOnline = true;
                        list[idx].LastOnlineTime = DateTime.Now;
                    }
                    else
                    {
                        list.Add(new Device
                        {
                            DeviceId = deviceId,
                            DeviceName = string.IsNullOrEmpty(deviceName) ? deviceId : deviceName,
                            IpAddress = ipAddress,
                            IsOnline = true,
                            RegisterTime = DateTime.Now,
                            LastOnlineTime = DateTime.Now
                        });
                    }
                });
            }
            catch
            {
                // 注册设备失败时忽略
            }
        }

        /// <summary>更新设备在线状态</summary>
        public void UpdateDeviceStatus(string deviceId, bool isOnline)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId)) return;

                DataStore.Current.MutateDevices(list =>
                {
                    int idx = list.FindIndex(d => d.DeviceId == deviceId);
                    if (idx >= 0)
                    {
                        list[idx].IsOnline = isOnline;
                        list[idx].LastOnlineTime = DateTime.Now;
                    }
                });
            }
            catch
            {
                // 更新状态失败时忽略
            }
        }

        /// <summary>获取单个设备</summary>
        public Device GetDevice(string deviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId)) return null;
                return DataStore.Current.GetDevices()
                    .FirstOrDefault(d => d.DeviceId == deviceId);
            }
            catch
            {
                return null;
            }
        }
    }
}
