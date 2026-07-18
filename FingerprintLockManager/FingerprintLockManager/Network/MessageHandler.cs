using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 消息处理器
    /// 解析收到的消息，根据 cmd 字段分发到对应的处理方法，
    /// 并通过事件通知 UI 层（UI 层再调用 UserService / PermissionService / LogService 等完成业务）。
    /// 维护最近 100 条 MsgId 的 LRU 缓存用于消息去重，避免 ACK/转发重复处理。
    /// </summary>
    public class MessageHandler
    {
        /// <summary>LRU 去重缓存容量</summary>
        private const int DedupCapacity = 100;

        /// <summary>最近处理过的 MsgId 缓存（链表头部为最新，尾部为最旧）</summary>
        private readonly LinkedList<string> _recentMsgIds = new LinkedList<string>();

        /// <summary>MsgId 快速查找索引（同一 MsgId 在链表中的节点）</summary>
        private readonly Dictionary<string, LinkedListNode<string>> _msgIdIndex = new Dictionary<string, LinkedListNode<string>>();

        /// <summary>去重缓存锁</summary>
        private readonly object _dedupLock = new object();

        /// <summary>设备注册事件：参数为 deviceId, deviceName</summary>
        public event Action<string, string>? OnDeviceRegistered;

        /// <summary>日志上报事件：参数为 deviceId, logJson</summary>
        public event Action<string, string>? OnLogReport;

        /// <summary>状态上报事件：参数为 statusJson</summary>
        public event Action<string>? OnStatusReport;

        /// <summary>配置读取响应事件：参数为 deviceId, configJson</summary>
        public event Action<string, string>? OnConfigResponse;

        /// <summary>状态读取响应事件：参数为 deviceId, statusJson</summary>
        public event Action<string, string>? OnStatusResponse;

        /// <summary>配置保存成功事件：参数为 deviceId</summary>
        public event Action<string>? OnConfigSaved;

        /// <summary>根节点注册事件：参数为 rootDeviceId（用于 SD 卡集中存储定位根节点）</summary>
        public event Action<string, bool?>? OnRootDeviceRegistered;

        /// <summary>ACK 应答事件：参数为 msgId（原命令消息 ID）, result（结果/错误码）</summary>
        public event Action<string, string>? OnAckReceived;

        /// <summary>错误响应事件：msgId, errorCode, message</summary>
        public event Action<string, string, string>? OnErrorReceived;

        /// <summary>指纹录入最终结果事件。</summary>
        public event Action<string, FingerprintEnrollmentResult>? OnFingerprintEnrollmentResult;

        /// <summary>权限事务提交结果事件：deviceId, msgId, result。</summary>
        public event Action<string, string, string>? OnPermissionSyncResult;

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

            // SD_QUERY_PART intentionally reuses the request MsgId for every
            // part. Deduplicating it would discard all parts after the first.
            if (cmdType != CommandType.SdQueryPart && !string.IsNullOrEmpty(msg.MsgId))
            {
                string sourceId = device?.DeviceId ?? msg.SourceDeviceId ?? msg.DeviceId;
                if (IsDuplicate($"{sourceId}|{msg.Cmd}|{msg.MsgId}")) return;
            }

            switch (cmdType.Value)
            {
                case CommandType.Register:
                    HandleRegister(device, msg);
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
                case CommandType.Ack:
                    HandleAck(device, msg);
                    break;
                case CommandType.AddFingerprintResult:
                    HandleFingerprintEnrollmentResult(device, msg);
                    break;
                case CommandType.SyncAck:
                    HandlePermissionSyncResult(device, msg);
                    break;
                case CommandType.Heartbeat:
                    // 心跳包：仅维持连接，无需业务处理
                    break;
                // SD 卡集中存储响应（交给 SdStorageService 处理请求-响应匹配）
                case CommandType.SdQueryResponse:
                case CommandType.SdQueryPart:
                case CommandType.SdSaveResponse:
                case CommandType.SdVersionResponse:
                case CommandType.FpTemplateUploadResponse:
                case CommandType.FpTemplateDownloadResponse:
                case CommandType.FpTemplateDeleteResponse:
                    App.SdStorageService.HandleResponse(msg);
                    break;
                case CommandType.Error:
                    App.SdStorageService.HandleResponse(msg);
                    HandleError(msg);
                    break;
            }
        }

        /// <summary>
        /// 判断 MsgId 是否为重复消息，并记录到 LRU 缓存
        /// </summary>
        /// <param name="msgId">消息 ID</param>
        /// <returns>重复返回 true；首次出现返回 false</returns>
        private bool IsDuplicate(string msgId)
        {
            lock (_dedupLock)
            {
                if (_msgIdIndex.TryGetValue(msgId, out var node))
                {
                    // 已存在：移到链表头部（最新），视为重复
                    _recentMsgIds.Remove(node);
                    _recentMsgIds.AddFirst(node);
                    return true;
                }

                // 新 MsgId：加入头部
                var newNode = _recentMsgIds.AddFirst(msgId);
                _msgIdIndex[msgId] = newNode;

                // 超容量则淘汰尾部
                while (_recentMsgIds.Count > DedupCapacity)
                {
                    var oldest = _recentMsgIds.Last;
                    if (oldest != null)
                    {
                        _recentMsgIds.RemoveLast();
                        _msgIdIndex.Remove(oldest.Value);
                    }
                }
                return false;
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

            // 检测根节点（is_root=true），通知 SdStorageService
            bool isRoot = TryGetBoolData(msg, "is_root");
            if (device != null) device.IsRoot = isRoot;
            if (isRoot && !string.IsNullOrEmpty(deviceId))
            {
                OnRootDeviceRegistered?.Invoke(deviceId, TryGetNullableBoolData(msg, "sd_ready"));
            }
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
        /// 处理 ACK 应答
        /// 触发 OnAckReceived 事件，携带原命令 MsgId 与结果码
        /// </summary>
        private void HandleAck(DeviceClient? device, Message msg)
        {
            var msgId = msg.MsgId;
            var result = TryGetStringData(msg, "result")
                ?? TryGetStringData(msg, "code")
                ?? Protocol.ErrOk;
            if (!string.IsNullOrEmpty(msgId))
            {
                OnAckReceived?.Invoke(msgId, result);
            }
        }

        private void HandleError(Message msg)
        {
            string code = TryGetStringData(msg, "error_code") ?? Protocol.ErrUnknown;
            string message = TryGetStringData(msg, "message") ?? "设备处理命令失败";
            OnErrorReceived?.Invoke(msg.MsgId, code, message);
        }

        private void HandleFingerprintEnrollmentResult(DeviceClient? device, Message msg)
        {
            string deviceId = device?.DeviceId ?? msg.SourceDeviceId ?? msg.DeviceId;
            string result = TryGetStringData(msg, "result") ?? "fail";
            string message = TryGetStringData(msg, "message") ?? "";
            int fingerprintId = (msg.Data as JObject)?["fingerprint_id"]?.Value<int>() ?? -1;
            string userId = TryGetStringData(msg, "user_id") ?? "";
            byte[]? template = null;
            string? hex = TryGetStringData(msg, "template_hex");
            if (!string.IsNullOrEmpty(hex) && hex.Length % 2 == 0)
            {
                try
                {
                    template = Convert.FromHexString(hex);
                }
                catch (FormatException)
                {
                    message = "设备返回的指纹模板格式无效";
                    result = "fail";
                }
            }

            OnFingerprintEnrollmentResult?.Invoke(msg.MsgId, new FingerprintEnrollmentResult
            {
                Success = string.Equals(result, "success", StringComparison.OrdinalIgnoreCase),
                DeviceId = deviceId,
                UserId = userId,
                FingerprintId = fingerprintId,
                TemplateBytes = template,
                ErrorMessage = message
            });
        }

        private void HandlePermissionSyncResult(DeviceClient? device, Message msg)
        {
            string deviceId = device?.DeviceId ?? msg.SourceDeviceId ?? msg.DeviceId;
            string result = TryGetStringData(msg, "result") ?? "fail";
            OnPermissionSyncResult?.Invoke(deviceId, msg.MsgId, result);
        }

        /// <summary>
        /// 从消息 Data 中尝试获取字符串字段
        /// Data 反序列化后为 JObject，支持按字段名查找
        /// </summary>
        /// <param name="msg">消息对象</param>
        /// <param name="fieldName">字段名</param>
        /// <returns>字段值字符串；不存在时返回 null</returns>
        private string? TryGetStringData(Message msg, string fieldName)
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

        /// <summary>
        /// 尝试从消息 data 中读取布尔字段（兼容 true/"true"/1）
        /// </summary>
        private bool TryGetBoolData(Message msg, string fieldName)
        {
            string? val = TryGetStringData(msg, fieldName);
            if (string.IsNullOrEmpty(val)) return false;
            return val.Equals("true", StringComparison.OrdinalIgnoreCase)
                || val == "1";
        }

        private bool? TryGetNullableBoolData(Message msg, string fieldName)
        {
            string? val = TryGetStringData(msg, fieldName);
            if (string.IsNullOrWhiteSpace(val)) return null;
            if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1") return true;
            if (val.Equals("false", StringComparison.OrdinalIgnoreCase) || val == "0") return false;
            return null;
        }
    }
}
