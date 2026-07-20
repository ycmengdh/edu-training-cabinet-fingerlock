using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 指纹模板业务服务。
    /// 实现录入指纹（采集）— 模板存储（PC 本地 / SD 卡）— 整理分配（下发到柜子）的解耦管理。
    /// 录入只是采集工作，模板与用户关联而非与设备关联；下发是后续的整理动作。
    /// 线程安全：所有方法均通过 LocalCacheService 的锁保护元数据读写；
    /// 异步方法（SD 上传、下发到柜子）使用 Task.Run 包装避免阻塞 UI。
    /// </summary>
    public class FingerprintTemplateService
    {
        private readonly UserService _userService;
        private readonly object _lock = new();

        public FingerprintTemplateService() : this(App.UserService)
        {
        }

        public FingerprintTemplateService(UserService userService)
        {
            _userService = userService;
        }

        // ===== 查询 =====

        /// <summary>
        /// 获取所有模板的合并视图：本地缓存的元数据 + SD 上的模板（SD 可用时）。
        /// SD 不可用或读取失败时仅返回本地。
        /// </summary>
        public List<FingerprintTemplate> GetAllTemplates()
        {
            var result = new List<FingerprintTemplate>();
            lock (_lock)
            {
                result.AddRange(LocalCacheService.ReadAllFpTemplateMetas());
            }

            // SD 可用时补充 SD 上的模板（按 user_id + finger_index 索引，避免与本地重复）
            if (App.SdStorageService.IsAvailable)
            {
                try
                {
                    var users = _userService.GetAllUsersBrief();
                    foreach (var u in users)
                    {
                        if (string.IsNullOrWhiteSpace(u.UserId)) continue;
                        if (u.FingerprintId == null) continue;

                        // 本地已有同 fingerprintId 的元数据则跳过
                        if (result.Any(m => m.FingerprintId == u.FingerprintId.Value)) continue;

                        result.Add(new FingerprintTemplate
                        {
                            FingerprintId = u.FingerprintId.Value,
                            UserId = u.UserId,
                            UserName = u.Name,
                            FingerIndex = 1,
                            EnrollTime = DateTime.MinValue,
                            TemplateSize = 0,
                            SourceDevice = "",
                            BackupStatus = "sd"
                        });
                    }
                }
                catch
                {
                    // SD 读取失败不影响本地列表
                }
            }

            return result.OrderByDescending(m => m.EnrollTime).ToList();
        }

        /// <summary>获取指定指纹 ID 的模板元数据；不存在返回 null</summary>
        public FingerprintTemplate? GetTemplate(int fingerprintId)
        {
            if (fingerprintId <= 0) return null;
            lock (_lock)
            {
                return LocalCacheService.ReadFpTemplateMeta(fingerprintId);
            }
        }

        /// <summary>
        /// 获取指定指纹 ID 的模板字节。
        /// 优先从本地读取；本地不存在且 SD 可用时尝试从 SD 下载。
        /// </summary>
        public async Task<byte[]?> GetTemplateBytesAsync(int fingerprintId)
        {
            if (fingerprintId <= 0) return null;

            FingerprintTemplate? meta;
            lock (_lock)
            {
                meta = LocalCacheService.ReadFpTemplateMeta(fingerprintId);
            }
            if (meta == null) return null;

            // 1. 本地字节文件
            byte[]? local = LocalCacheService.ReadFpTemplateByFingerprintId(fingerprintId, meta.FingerIndex);
            if (local != null && local.Length > 0) return local;

            // 2. SD 下载
            if (App.SdStorageService.IsAvailable && !string.IsNullOrWhiteSpace(meta.UserId))
            {
                try
                {
                    return await App.SdStorageService.DownloadTemplateAsync(meta.UserId, meta.FingerIndex);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        // ===== 采集保存 =====

        /// <summary>
        /// 保存录入结果到本地指纹模板库。
        /// </summary>
        /// <param name="fingerprintId">指纹 ID</param>
        /// <param name="template">模板字节</param>
        /// <param name="sourceDevice">采集设备 ID</param>
        /// <param name="userId">可选的关联用户 ID</param>
        public bool SaveEnrolledTemplate(int fingerprintId, byte[] template,
            string sourceDevice, string? userId = null)
        {
            if (fingerprintId <= 0 || template == null || template.Length == 0) return false;

            string? userName = null;
            if (!string.IsNullOrWhiteSpace(userId))
            {
                try
                {
                    var user = _userService.GetUser(userId);
                    userName = user?.Name;
                }
                catch
                {
                    // SD 不可用时忽略
                }
            }

            lock (_lock)
            {
                LocalCacheService.SaveFpTemplateWithMeta(
                    fingerprintId, userId, 1, template, sourceDevice ?? "");
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    LocalCacheService.BindFpTemplateToUser(fingerprintId, userId, userName);
                }
            }
            return true;
        }

        // ===== 关联用户 =====

        /// <summary>
        /// 绑定模板到用户：更新本地元数据，并同步到 users 表（更新用户的 fingerprint_id）。
        /// </summary>
        public bool BindToUser(int fingerprintId, string userId)
        {
            if (fingerprintId <= 0 || string.IsNullOrWhiteSpace(userId)) return false;

            string? userName = null;
            try
            {
                var user = _userService.GetUser(userId);
                if (user == null) return false;
                userName = user.Name;
            }
            catch (RootDataUnavailableException)
            {
                // SD 不可用时仅绑定本地元数据，不更新 users 表
            }

            lock (_lock)
            {
                if (!LocalCacheService.BindFpTemplateToUser(fingerprintId, userId, userName))
                    return false;
            }

            // 同步 users 表的 fingerprint_id（SD 可用时）
            try
            {
                return _userService.AssignFingerprint(userId, fingerprintId);
            }
            catch (RootDataUnavailableException)
            {
                return true; // 本地已绑定，SD 恢复后由用户手动同步
            }
        }

        // ===== 下发到柜子 =====

        /// <summary>
        /// 把模板下发到指定柜子（用 RESTORE_FINGERPRINT 命令写入传感器）。
        /// </summary>
        public async Task<bool> DistributeToDeviceAsync(int fingerprintId, string deviceId)
        {
            if (fingerprintId <= 0 || string.IsNullOrWhiteSpace(deviceId)) return false;

            FingerprintTemplate? meta;
            lock (_lock)
            {
                meta = LocalCacheService.ReadFpTemplateMeta(fingerprintId);
            }
            if (meta == null) return false;

            byte[]? bytes = await GetTemplateBytesAsync(fingerprintId);
            if (bytes == null || bytes.Length == 0) return false;

            string userId = string.IsNullOrWhiteSpace(meta.UserId) ? "" : meta.UserId;
            try
            {
                CommandResult result = await App.CommandService.RestoreFingerprintAsync(
                    deviceId, userId, fingerprintId, bytes);
                if (result.Success)
                {
                    LocalCacheService.UpdateFpTemplateBackupStatus(fingerprintId, "distributed");
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>批量下发到多个柜子，返回每个柜子的成功状态</summary>
        public async Task<Dictionary<string, bool>> DistributeToDevicesAsync(
            int fingerprintId, List<string> deviceIds)
        {
            var result = new Dictionary<string, bool>();
            if (deviceIds == null || deviceIds.Count == 0) return result;

            foreach (var deviceId in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
            {
                bool ok = await DistributeToDeviceAsync(fingerprintId, deviceId);
                result[deviceId] = ok;
            }
            return result;
        }

        /// <summary>
        /// 批量下发所有未关联的本地模板到指定柜子列表。
        /// 返回 (fingerprintId, deviceId, success) 列表。
        /// </summary>
        public async Task<List<(int fingerprintId, string deviceId, bool success)>>
            DistributeAllUnassignedAsync(List<string> deviceIds)
        {
            var result = new List<(int, string, bool)>();
            if (deviceIds == null || deviceIds.Count == 0) return result;

            List<FingerprintTemplate> metas;
            lock (_lock)
            {
                metas = LocalCacheService.ReadAllFpTemplateMetas();
            }

            foreach (var meta in metas)
            {
                foreach (var deviceId in deviceIds
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
                {
                    bool ok = await DistributeToDeviceAsync(meta.FingerprintId, deviceId);
                    result.Add((meta.FingerprintId, deviceId, ok));
                }
            }
            return result;
        }

        // ===== 上传到 SD =====

        /// <summary>上传指定模板到 SD 卡（带 fallback：SD 不可用时仅保留本地）</summary>
        public async Task<bool> UploadToSdAsync(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            if (!App.SdStorageService.IsAvailable) return false;

            FingerprintTemplate? meta;
            lock (_lock)
            {
                meta = LocalCacheService.ReadFpTemplateMeta(fingerprintId);
            }
            if (meta == null) return false;

            byte[]? bytes = LocalCacheService.ReadFpTemplateByFingerprintId(
                fingerprintId, meta.FingerIndex);
            if (bytes == null || bytes.Length == 0) return false;

            string userId = string.IsNullOrWhiteSpace(meta.UserId)
                ? $"fp_{fingerprintId}" : meta.UserId;

            try
            {
                bool ok = await App.SdStorageService.UploadFpTemplateWithFallbackAsync(
                    userId, meta.FingerIndex, bytes);
                if (ok)
                {
                    LocalCacheService.UpdateFpTemplateBackupStatus(fingerprintId, "sd");
                }
                return ok;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>上传所有本地模板到 SD 卡，返回成功上传的数量</summary>
        public async Task<int> UploadAllToSdAsync()
        {
            if (!App.SdStorageService.IsAvailable) return 0;

            List<FingerprintTemplate> metas;
            lock (_lock)
            {
                metas = LocalCacheService.ReadAllFpTemplateMetas();
            }

            int success = 0;
            foreach (var meta in metas)
            {
                try
                {
                    if (await UploadToSdAsync(meta.FingerprintId)) success++;
                }
                catch
                {
                    // 单条失败不影响其他
                }
            }
            return success;
        }

        // ===== 删除 =====

        /// <summary>删除本地模板（不删除 SD 上的备份）</summary>
        public bool DeleteTemplate(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            lock (_lock)
            {
                return LocalCacheService.DeleteFpTemplateByFingerprintId(fingerprintId);
            }
        }

        // ===== 设备指纹清单查询 =====

        /// <summary>
        /// 查询指定柜子当前的指纹清单。
        /// 由于固件 READ_PERMISSIONS 仅返回 count/version（不返回用户列表），
        /// 这里通过 READ_STATUS 确认设备在线并获取实际指纹数，
        /// 再用本地 users 表构造"应该在该柜子上的指纹"清单。
        /// 用户表读取与 READ_STATUS 并行，且都在后台线程执行，避免卡 UI。
        /// </summary>
        public async Task<List<DeviceFingerprintInfo>> GetDeviceFingerprintListAsync(
            string deviceId, int statusTimeoutMs = 2500)
        {
            var result = new List<DeviceFingerprintInfo>();
            if (string.IsNullOrWhiteSpace(deviceId)) return result;

            Task<int> countTask = QueryDeviceFingerprintCountAsync(deviceId, statusTimeoutMs);
            Task<List<UserBrief>> usersTask = Task.Run(() =>
            {
                try { return _userService.GetAllUsersBrief(); }
                catch { return new List<UserBrief>(); }
            });

            await Task.WhenAll(countTask, usersTask).ConfigureAwait(false);
            int deviceFpCount = countTask.Result;
            List<UserBrief> users = usersTask.Result;

            foreach (var u in users)
            {
                if (u.FingerprintId == null) continue;
                result.Add(new DeviceFingerprintInfo
                {
                    FingerprintId = u.FingerprintId.Value,
                    UserId = u.UserId,
                    UserName = u.Name,
                    Role = u.Role,
                    HasPermission = true,
                    DeviceReportedCount = deviceFpCount >= 0 ? deviceFpCount : null
                });
            }

            return result;
        }

        /// <summary>
        /// 向柜子发送 READ_STATUS 并等待 STATUS_RESPONSE，返回 fingerprint_count。
        /// 由于 STATUS_RESPONSE 不走 ACK 通道，需订阅 OnStatusResponse 事件匹配 deviceId。
        /// 匹配时同时兼容业务 device_id 与 Mesh MAC，避免因身份字段不一致一直等到超时。
        /// </summary>
        private async Task<int> QueryDeviceFingerprintCountAsync(string deviceId, int timeoutMs)
        {
            string targetId = (deviceId ?? "").Trim();
            if (string.IsNullOrEmpty(targetId)) return -1;

            // 解析 Mesh 侧在线身份，优先用能真正发出去的路由键。
            string? meshMac = null;
            string routeId = targetId;
            try
            {
                foreach (var dc in App.MeshBridge.GetOnlineDevices())
                {
                    if (!dc.IsOnline) continue;
                    bool idMatch = string.Equals(dc.DeviceId, targetId, StringComparison.OrdinalIgnoreCase);
                    bool macMatch = !string.IsNullOrWhiteSpace(dc.MeshMac) &&
                                    string.Equals(dc.MeshMac, targetId, StringComparison.OrdinalIgnoreCase);
                    if (!idMatch && !macMatch) continue;

                    if (!string.IsNullOrWhiteSpace(dc.MeshMac))
                        meshMac = dc.MeshMac;
                    if (!string.IsNullOrWhiteSpace(dc.DeviceId))
                        routeId = dc.DeviceId;
                    else if (!string.IsNullOrWhiteSpace(dc.MeshMac))
                        routeId = dc.MeshMac;
                    break;
                }
            }
            catch
            {
                // 在线表读取失败时仍尝试按传入 ID 发送
            }

            var tcs = new TaskCompletionSource<int?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnStatus(string did, string json)
            {
                if (!IsSameDeviceIdentity(did, targetId, meshMac, routeId)) return;
                try
                {
                    var data = JObject.Parse(json ?? "{}");
                    int count = data.Value<int?>("fingerprint_count") ?? -1;
                    tcs.TrySetResult(count);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
            }

            App.MessageHandler.OnStatusResponse += OnStatus;
            try
            {
                var msg = Message.Create(Protocol.CmdReadStatus, routeId);
                bool sent = App.MeshBridge.SendToDevice(routeId, msg);
                if (!sent && !string.Equals(routeId, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    // 路由键失败时回退原始 ID 再试一次
                    msg = Message.Create(Protocol.CmdReadStatus, targetId);
                    sent = App.MeshBridge.SendToDevice(targetId, msg);
                }
                if (!sent) return -1;

                Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed == tcs.Task)
                    return (await tcs.Task.ConfigureAwait(false)) ?? -1;
                return -1;
            }
            finally
            {
                App.MessageHandler.OnStatusResponse -= OnStatus;
            }
        }

        private static bool IsSameDeviceIdentity(
            string responseId, string requestedId, string? meshMac, string routeId)
        {
            if (string.IsNullOrWhiteSpace(responseId)) return false;
            if (string.Equals(responseId, requestedId, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(responseId, routeId, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(meshMac) &&
                string.Equals(responseId, meshMac, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>设备指纹清单条目</summary>
    public class DeviceFingerprintInfo
    {
        public int FingerprintId { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? Role { get; set; }
        public bool HasPermission { get; set; }

        /// <summary>设备 READ_STATUS 返回的 fingerprint_count（用于显示设备实际模板数）</summary>
        public int? DeviceReportedCount { get; set; }
    }
}
