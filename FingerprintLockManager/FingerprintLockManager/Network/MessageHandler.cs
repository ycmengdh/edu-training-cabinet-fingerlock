using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 消息处理器
    /// 解析收到的消息，根据 cmd 字段分发到对应的处理方法，
    /// 并通过事件通知 UI 层（UI 层再调用 UserService / PermissionService / LogService 等完成业务）。
    /// </summary>
    public class MessageHandler
    {
        /// <summary>指纹验证请求事件：参数为 deviceId, fingerprintId</summary>
        public event Action<string, string> OnFingerVerifyRequest;

        /// <summary>设备注册事件：参数为 deviceId, deviceName</summary>
        public event Action<string, string> OnDeviceRegistered;

        /// <summary>日志上报事件：参数为 deviceId, logJson</summary>
        public event Action<string, string> OnLogReport;

        /// <summary>状态上报事件：参数为 statusJson</summary>
        public event Action<string> OnStatusReport;

        /// <summary>配置读取响应事件：参数为 deviceId, configJson</summary>
        public event Action<string, string> OnConfigResponse;

        /// <summary>状态读取响应事件：参数为 deviceId, statusJson</summary>
        public event Action<string, string> OnStatusResponse;

        /// <summary>配置保存成功事件：参数为 deviceId</summary>
        public event Action<string> OnConfigSaved;

        /// <summary>
        /// 处理收到的消息，根据 cmd 字段分发到对应处理方法
        /// </summary>
        /// <param name="device">消息来源设备</param>
        /// <param name="msg">收到的消息</param>
        public void HandleMessage(DeviceClient? device, Message msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.Cmd)) return;

            // 同步设备 ID
            if (device != null && string.IsNullOrEmpty(device.DeviceId) && !string.IsNullOrEmpty(msg.DeviceId))
            {
                device.DeviceId = msg.DeviceId;
            }

            var cmdType = Protocol.ToCommandType(msg.Cmd);
            if (cmdType == null) return;

            switch (cmdType.Value)
            {
                case CommandType.Register:
                    HandleRegister(device, msg);
                    break;
                case CommandType.FingerVerify:
                    HandleFingerVerify(device, msg);
                    break;
                case CommandType.StatusReport:
                    HandleStatusReport(device, msg);
                    break;
                case CommandType.LogReport:
                    HandleLogReport(device, msg);
                    break;
                case CommandType.ConfigResponse:
                    HandleConfigResponse(device, msg);
                    break;
                case CommandType.StatusResponse:
                    HandleStatusResponse(device, msg);
                    break;
                case CommandType.ConfigSaved:
                    HandleConfigSaved(device, msg);
                    break;
                case CommandType.Heartbeat:
                    // 心跳包：仅维持连接，无需业务处理
                    break;
            }
        }

        /// <summary>
        /// 处理设备注册
        /// 触发 OnDeviceRegistered 事件让上层注册/更新设备信息
        /// </summary>
        private void HandleRegister(DeviceClient? device, Message msg)
        {
            var deviceName = TryGetStringData(msg, "device_name")
                ?? TryGetStringData(msg, "deviceName")
                ?? "未命名设备";

            if (device != null && string.IsNullOrEmpty(device.DeviceName))
            {
                device.DeviceName = deviceName;
            }

            var deviceId = device?.DeviceId ?? msg.DeviceId;
            OnDeviceRegistered?.Invoke(deviceId, deviceName);
        }

        /// <summary>
        /// 处理指纹验证请求
        /// 触发 OnFingerVerifyRequest 事件让上层查询用户权限并回复 AUTH_OK / AUTH_FAIL
        /// </summary>
        private void HandleFingerVerify(DeviceClient? device, Message msg)
        {
            var fingerprintId = TryGetStringData(msg, "fingerprint_id")
                ?? TryGetStringData(msg, "fingerprintId")
                ?? TryGetStringData(msg, "fp_id")
                ?? "";

            var deviceId = device?.DeviceId ?? msg.DeviceId;
            OnFingerVerifyRequest?.Invoke(deviceId, fingerprintId);
        }

        /// <summary>
        /// 处理状态上报
        /// 触发 OnStatusReport 事件让上层更新状态显示
        /// </summary>
        private void HandleStatusReport(DeviceClient? device, Message msg)
        {
            var statusJson = msg.Data != null ? JsonHelper.Serialize(msg.Data) : "{}";
            OnStatusReport?.Invoke(statusJson);
        }

        /// <summary>
        /// 处理日志上报
        /// 触发 OnLogReport 事件让上层保存日志
        /// </summary>
        private void HandleLogReport(DeviceClient? device, Message msg)
        {
            var logJson = msg.Data != null ? JsonHelper.Serialize(msg.Data) : "{}";
            var deviceId = device?.DeviceId ?? msg.DeviceId;
            OnLogReport?.Invoke(deviceId, logJson);
        }

        /// <summary>
        /// 处理配置读取响应
        /// 触发 OnConfigResponse 事件让上层显示/更新设备配置
        /// </summary>
        private void HandleConfigResponse(DeviceClient? device, Message msg)
        {
            var configJson = msg.Data != null ? JsonHelper.Serialize(msg.Data) : "{}";
            var deviceId = device?.DeviceId ?? msg.DeviceId;
            OnConfigResponse?.Invoke(deviceId, configJson);
        }

        /// <summary>
        /// 处理状态读取响应
        /// 触发 OnStatusResponse 事件让上层显示设备状态
        /// </summary>
        private void HandleStatusResponse(DeviceClient? device, Message msg)
        {
            var statusJson = msg.Data != null ? JsonHelper.Serialize(msg.Data) : "{}";
            var deviceId = device?.DeviceId ?? msg.DeviceId;
            OnStatusResponse?.Invoke(deviceId, statusJson);
        }

        /// <summary>
        /// 处理配置保存成功响应
        /// 触发 OnConfigSaved 事件让上层提示用户
        /// </summary>
        private void HandleConfigSaved(DeviceClient? device, Message msg)
        {
            var deviceId = device?.DeviceId ?? msg.DeviceId;
            OnConfigSaved?.Invoke(deviceId);
        }

        /// <summary>
        /// 从消息 Data 中尝试获取字符串字段
        /// Data 反序列化后为 JObject，支持按字段名查找
        /// </summary>
        /// <param name="msg">消息对象</param>
        /// <param name="fieldName">字段名</param>
        /// <returns>字段值字符串；不存在时返回 null</returns>
        private string TryGetStringData(Message msg, string fieldName)
        {
            if (msg?.Data == null) return null;
            try
            {
                if (msg.Data is JObject jobj)
                {
                    return jobj[fieldName]?.ToString();
                }
                // 兜底：序列化后重新解析为 JObject
                var json = JsonHelper.Serialize(msg.Data);
                var temp = JsonHelper.Deserialize<JObject>(json);
                return temp?[fieldName]?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
