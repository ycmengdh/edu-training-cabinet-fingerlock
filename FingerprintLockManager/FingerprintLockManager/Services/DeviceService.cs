namespace FingerprintLockManager
{
    /// <summary>
    /// 设备服务。设备注册和状态快照由根节点写入 devices.json。
    /// </summary>
    public class DeviceService
    {
        private readonly RootDataService _root = new RootDataService();

        public List<Device> GetAllDevices()
        {
            return _root.Read<Device>("devices")
                .OrderBy(d => d.DeviceId).ToList();
        }

        public List<Device> GetOnlineDevices()
        {
            return GetAllDevices().Where(d => d.IsOnline).ToList();
        }

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

        public Device? GetDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            return GetAllDevices().FirstOrDefault(d => d.DeviceId == deviceId);
        }
    }
}
