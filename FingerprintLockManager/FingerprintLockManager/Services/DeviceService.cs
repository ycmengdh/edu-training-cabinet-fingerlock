namespace FingerprintLockManager
{
    /// <summary>
    /// 设备管理服务
    /// 负责 ESP32 指纹锁设备的注册、查询与在线状态维护
    /// </summary>
    public class DeviceService
    {
        /// <summary>
        /// 获取所有设备
        /// </summary>
        /// <returns>设备列表；异常时返回空列表</returns>
        public List<Device> GetAllDevices()
        {
            try
            {
                return DatabaseService.Fsql.Select<Device>()
                    .OrderBy(d => d.RegisterTime)
                    .ToList();
            }
            catch
            {
                return new List<Device>();
            }
        }

        /// <summary>
        /// 获取在线设备
        /// </summary>
        /// <returns>在线设备列表；异常时返回空列表</returns>
        public List<Device> GetOnlineDevices()
        {
            try
            {
                return DatabaseService.Fsql.Select<Device>()
                    .Where(d => d.IsOnline)
                    .OrderBy(d => d.DeviceId)
                    .ToList();
            }
            catch
            {
                return new List<Device>();
            }
        }

        /// <summary>
        /// 注册或更新设备（ESP32 连接时调用）
        /// 设备已存在则更新名称、IP 与在线时间；不存在则新增
        /// </summary>
        /// <param name="deviceId">设备 ID</param>
        /// <param name="deviceName">设备名称</param>
        /// <param name="ipAddress">设备 IP 地址</param>
        public void RegisterDevice(string deviceId, string deviceName, string ipAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId)) return;

                var existing = DatabaseService.Fsql.Select<Device>()
                    .Where(d => d.DeviceId == deviceId)
                    .First();

                if (existing != null)
                {
                    // 更新已有设备
                    existing.DeviceName = string.IsNullOrEmpty(deviceName) ? existing.DeviceName : deviceName;
                    existing.IpAddress = ipAddress;
                    existing.IsOnline = true;
                    existing.LastOnlineTime = DateTime.Now;

                    DatabaseService.Fsql.Update<Device>()
                        .SetSource(existing)
                        .ExecuteAffrows();
                }
                else
                {
                    // 新增设备
                    var device = new Device
                    {
                        DeviceId = deviceId,
                        DeviceName = string.IsNullOrEmpty(deviceName) ? deviceId : deviceName,
                        IpAddress = ipAddress,
                        IsOnline = true,
                        RegisterTime = DateTime.Now,
                        LastOnlineTime = DateTime.Now
                    };

                    DatabaseService.Fsql.Insert(device).ExecuteAffrows();
                }
            }
            catch
            {
                // 注册设备失败时忽略，避免影响通讯流程
            }
        }

        /// <summary>
        /// 更新设备在线状态
        /// </summary>
        /// <param name="deviceId">设备 ID</param>
        /// <param name="isOnline">是否在线</param>
        public void UpdateDeviceStatus(string deviceId, bool isOnline)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId)) return;

                // 更新在线状态与最后在线时间
                DatabaseService.Fsql.Update<Device>()
                    .Set(d => d.IsOnline, isOnline)
                    .Set(d => d.LastOnlineTime, DateTime.Now)
                    .Where(d => d.DeviceId == deviceId)
                    .ExecuteAffrows();
            }
            catch
            {
                // 更新状态失败时忽略
            }
        }

        /// <summary>
        /// 获取单个设备
        /// </summary>
        /// <param name="deviceId">设备 ID</param>
        /// <returns>设备对象；不存在或异常返回 null</returns>
        public Device GetDevice(string deviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId)) return null;

                return DatabaseService.Fsql.Select<Device>()
                    .Where(d => d.DeviceId == deviceId)
                    .First();
            }
            catch
            {
                return null;
            }
        }
    }
}
