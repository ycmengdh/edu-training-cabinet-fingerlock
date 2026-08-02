using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    /// <summary>
    /// 指纹模板业务服务。
    /// 元数据与模板字节存于本机 business.db；同步时上传到根节点 SD。
    /// 录入与用户关联，不与设备绑定；下发到柜子为后续整理动作。
    /// </summary>
    public class FingerprintTemplateService
    {
        private readonly UserService _userService;

        public FingerprintTemplateService() : this(App.UserService)
        {
        }

        public FingerprintTemplateService(UserService userService)
        {
            _userService = userService;
        }

        // ===== 查询 =====

        /// <summary>
        /// 获取本机业务库中的全部指纹模板元数据。
        /// </summary>
        public List<FingerprintTemplate> GetAllTemplates()
        {
            var result = new List<FingerprintTemplate>();
            result.AddRange(BusinessDatabase.ReadAllFpTemplateMetas());

            try
            {
                var users = _userService.GetAllUsersBrief();
                Dictionary<string, UserBrief> usersById = users
                    .Where(user => !string.IsNullOrWhiteSpace(user.UserId))
                    .ToDictionary(user => user.UserId, StringComparer.OrdinalIgnoreCase);
                foreach (FingerprintTemplate template in result)
                {
                    if (!string.IsNullOrWhiteSpace(template.UserId) &&
                        usersById.TryGetValue(template.UserId, out UserBrief? owner))
                    {
                        template.UserCode = owner.UserCode;
                        template.UserName = owner.Name;
                    }
                }
            }
            catch
            {
                // 用户表失败不影响本机列表
            }

            return result.OrderByDescending(m => m.EnrollTime).ToList();
        }

        /// <summary>获取指定指纹 ID 的模板元数据；不存在返回 null</summary>
        public FingerprintTemplate? GetTemplate(int fingerprintId)
        {
            if (fingerprintId <= 0) return null;
            return BusinessDatabase.ReadFpTemplateMeta(fingerprintId);
        }

        /// <summary>
        /// 获取指定指纹 ID 的模板字节。
        /// 优先 business.db；缺失且 SD 可用时尝试从 SD 下载并回写本机。
        /// </summary>
        public async Task<byte[]?> GetTemplateBytesAsync(int fingerprintId)
        {
            if (fingerprintId <= 0) return null;

            var meta = BusinessDatabase.ReadFpTemplateMeta(fingerprintId);
            int fingerIndex = meta?.FingerIndex > 0 ? meta.FingerIndex : 1;
            byte[]? local = BusinessDatabase.ReadFpTemplateBytes(fingerprintId, fingerIndex);
            if (local != null && local.Length > 0) return local;

            string userId = meta?.UserId ?? "";
            string? userName = meta?.UserName;
            if (string.IsNullOrWhiteSpace(userId))
            {
                try
                {
                    UserBrief? owner = _userService.GetAllUsersBrief().FirstOrDefault(user =>
                        user.FingerprintId == fingerprintId);
                    userId = owner?.UserId ?? "";
                    userName = owner?.Name;
                }
                catch
                {
                }
            }

            if (App.SdStorageService.IsAvailable && !string.IsNullOrWhiteSpace(userId))
            {
                try
                {
                    var remote = await App.SdStorageService.DownloadTemplateAsync(
                        userId, fingerIndex);
                    if (remote != null && remote.Length > 0)
                    {
                        BusinessDatabase.SaveFpTemplateWithMeta(
                            fingerprintId, userId, fingerIndex, remote,
                            meta?.SourceDevice ?? "ROOT_SD");
                        BusinessDatabase.BindFpTemplateToUser(
                            fingerprintId, userId, userName);
                        BusinessDatabase.UpdateFpTemplateBackupStatus(fingerprintId, "sd");
                        return remote;
                    }
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        // ===== 采集保存 =====

        public bool SaveEnrolledTemplate(int fingerprintId, byte[] template,
            string sourceDevice, string? userId = null, int fingerIndex = 1,
            string? fingerName = null, int quality = 0)
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
                }
            }

            BusinessDatabase.SaveFpTemplateWithMeta(
                fingerprintId, userId, fingerIndex, template, sourceDevice ?? "");
            FingerprintTemplate? meta = BusinessDatabase.ReadFpTemplateMeta(fingerprintId);
            if (meta != null)
            {
                meta.FingerIndex = fingerIndex;
                meta.FingerName = fingerName ?? "";
                meta.Quality = Math.Max(0, quality);
                meta.Enabled = true;
                BusinessDatabase.WriteFpTemplateMeta(meta);
            }
            if (!string.IsNullOrWhiteSpace(userId))
                BusinessDatabase.BindFpTemplateToUser(fingerprintId, userId, userName);
            return true;
        }

        public IReadOnlyList<int> GetUsedFingerIndexes(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return Array.Empty<int>();
            return BusinessDatabase.ReadAllFpTemplateMetas()
                .Where(item => string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.FingerIndex)
                .Where(index => index is >= 1 and <= 10)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
        }

        // ===== 关联用户 =====

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
            }

            if (!BusinessDatabase.BindFpTemplateToUser(fingerprintId, userId, userName))
                return false;

            try
            {
                User? user = _userService.GetUser(userId);
                return user?.FingerprintId.HasValue == true ||
                    _userService.AssignFingerprint(userId, fingerprintId);
            }
            catch (RootDataUnavailableException)
            {
                return true;
            }
        }

        // ===== 下发到柜子 =====

        /// <summary>
        /// 下发单个模板到指定柜子。失败时 error 给出可读原因（无模板字节、链路、固件拒写等）。
        /// </summary>
        public async Task<(bool ok, string error)> DistributeToDeviceDetailedAsync(
            int fingerprintId, string deviceId)
        {
            if (fingerprintId <= 0 || string.IsNullOrWhiteSpace(deviceId))
                return (false, "参数无效");

            var meta = BusinessDatabase.ReadFpTemplateMeta(fingerprintId);
            if (meta == null)
                return (false, $"本地无指纹 {fingerprintId} 的模板元数据（可能只有用户关联、无模板字节）");

            byte[]? bytes = await GetTemplateBytesAsync(fingerprintId);
            if (bytes == null || bytes.Length == 0)
                return (false, $"指纹 {fingerprintId} 模板字节为空（请先录入或从 SD 拉取）");

            string userId = string.IsNullOrWhiteSpace(meta.UserId) ? "" : meta.UserId;
            try
            {
                CommandResult result = await App.CommandService.RestoreFingerprintAsync(
                    deviceId, userId, fingerprintId, bytes);
                if (result.Success)
                {
                    BusinessDatabase.UpdateFpTemplateBackupStatus(fingerprintId, "distributed");
                    return (true, "");
                }
                string err = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "柜子写入失败（无详细错误）"
                    : result.ErrorMessage;
                return (false, err);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<bool> DistributeToDeviceAsync(int fingerprintId, string deviceId)
        {
            var (ok, _) = await DistributeToDeviceDetailedAsync(fingerprintId, deviceId);
            return ok;
        }

        public async Task<Dictionary<string, bool>> DistributeToDevicesAsync(
            int fingerprintId, List<string> deviceIds)
        {
            var detailed = await DistributeToDevicesDetailedAsync(fingerprintId, deviceIds);
            var result = new Dictionary<string, bool>();
            foreach (var pair in detailed)
                result[pair.Key] = pair.Value.ok;
            return result;
        }

        /// <summary>批量下发并返回每台设备的成败与错误信息。</summary>
        public async Task<Dictionary<string, (bool ok, string error)>> DistributeToDevicesDetailedAsync(
            int fingerprintId, List<string> deviceIds)
        {
            var result = new Dictionary<string, (bool ok, string error)>();
            if (deviceIds == null || deviceIds.Count == 0) return result;

            foreach (var deviceId in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
            {
                result[deviceId] = await DistributeToDeviceDetailedAsync(fingerprintId, deviceId);
            }
            return result;
        }

        public async Task<List<(int fingerprintId, string deviceId, bool success)>>
            DistributeAllUnassignedAsync(List<string> deviceIds)
        {
            var result = new List<(int, string, bool)>();
            if (deviceIds == null || deviceIds.Count == 0) return result;

            var metas = BusinessDatabase.ReadAllFpTemplateMetas();
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

        public async Task<bool> UploadToSdAsync(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            if (!App.SdStorageService.IsAvailable) return false;

            var meta = BusinessDatabase.ReadFpTemplateMeta(fingerprintId);
            if (meta == null) return false;

            byte[]? bytes = BusinessDatabase.ReadFpTemplateBytes(fingerprintId, meta.FingerIndex);
            if (bytes == null || bytes.Length == 0) return false;

            string userId = string.IsNullOrWhiteSpace(meta.UserId)
                ? $"fp_{fingerprintId}" : meta.UserId;

            try
            {
                bool ok = await App.SdStorageService.UploadFpTemplateWithFallbackAsync(
                    userId, meta.FingerIndex, bytes);
                if (ok)
                    BusinessDatabase.UpdateFpTemplateBackupStatus(fingerprintId, "sd");
                return ok;
            }
            catch
            {
                return false;
            }
        }

        public async Task<int> UploadAllToSdAsync()
        {
            if (!App.SdStorageService.IsAvailable) return 0;

            var metas = BusinessDatabase.ReadAllFpTemplateMetas();
            int success = 0;
            foreach (var meta in metas)
            {
                try
                {
                    if (await UploadToSdAsync(meta.FingerprintId)) success++;
                }
                catch
                {
                }
            }
            return success;
        }

        // ===== 删除 =====

        public bool DeleteTemplate(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            return BusinessDatabase.DeleteFpTemplateByFingerprintId(fingerprintId);
        }

        // ===== 设备指纹清单查询 =====

        /// <summary>
        /// 查询指定柜子当前的指纹清单。
        /// 由于固件 READ_PERMISSIONS 仅返回 count/version（不返回用户列表），
        /// 这里通过 READ_STATUS 确认设备在线并获取实际指纹数，
        /// 再用本地 users 表构造"应该在该柜子上的指纹"清单。
        /// 用户表读取与 READ_STATUS 并行，且都在后台线程执行，避免卡 UI。
        /// </summary>
        public async Task<DeviceFingerprintListResult> GetDeviceFingerprintListAsync(
            string deviceId, int statusTimeoutMs = 2500)
        {
            var result = new List<DeviceFingerprintInfo>();
            if (string.IsNullOrWhiteSpace(deviceId))
                return new DeviceFingerprintListResult(result, null);

            Task<DeviceRuntimeStatus?> statusTask = QueryDeviceRuntimeStatusAsync(
                deviceId, statusTimeoutMs);
            Task<List<User>> usersTask = Task.Run(() =>
            {
                try { return _userService.GetAllUsers(); }
                catch { return new List<User>(); }
            });

            await Task.WhenAll(statusTask, usersTask).ConfigureAwait(false);
            DeviceRuntimeStatus? reportedStatus = statusTask.Result;
            int deviceFpCount = reportedStatus?.FingerprintCount ?? -1;
            List<User> users = usersTask.Result;
            HashSet<string> excluded = App.CabinetBindingService.GetExcludedUserIds(deviceId);

            foreach (var u in users)
            {
                if (excluded.Contains(u.UserId)) continue;
                IReadOnlyList<int> selectedFingerprintIds = App.CabinetBindingService
                    .GetSelectedFingerprintIds(u, deviceId);
                if (selectedFingerprintIds.Count == 0) continue;
                string permissionText = "-";
                bool[] permissions = new bool[4];
                try
                {
                    permissions = App.PermissionService.GetFinalPermissions(u.UserId);
                    if (!u.Enabled) Array.Fill(permissions, false);
                    permissionText = string.Join(" / ", permissions.Select((allowed, index) =>
                        $"L{index}:{(allowed ? "有" : "无")}"));
                }
                catch
                {
                    // 权限表暂时不可用时仍显示设备绑定用户。
                }
                foreach (int fingerprintId in selectedFingerprintIds)
                {
                    result.Add(new DeviceFingerprintInfo
                    {
                        FingerprintId = fingerprintId,
                        UserId = u.UserId,
                        UserCode = u.DisplayId,
                        UserName = u.Name,
                        Role = u.Role,
                        IsEnabled = u.Enabled,
                        HasPermission = permissions.Any(allowed => allowed),
                        Lock0Allowed = permissions.Length > 0 && permissions[0],
                        Lock1Allowed = permissions.Length > 1 && permissions[1],
                        Lock2Allowed = permissions.Length > 2 && permissions[2],
                        Lock3Allowed = permissions.Length > 3 && permissions[3],
                        PermissionText = permissionText,
                        DeviceReportedCount = deviceFpCount >= 0 ? deviceFpCount : null
                    });
                }
            }

            return new DeviceFingerprintListResult(result, reportedStatus);
        }

        /// <summary>
        /// 向柜子发送 READ_STATUS 并等待 STATUS_RESPONSE，返回完整实时状态。
        /// 由于 STATUS_RESPONSE 不走 ACK 通道，需订阅 OnStatusResponse 事件匹配 deviceId。
        /// 匹配时同时兼容业务 device_id 与 Mesh MAC，避免因身份字段不一致一直等到超时。
        /// </summary>
        public async Task<DeviceRuntimeStatus?> QueryDeviceRuntimeStatusAsync(
            string deviceId, int timeoutMs = 2500)
        {
            string targetId = (deviceId ?? "").Trim();
            if (string.IsNullOrEmpty(targetId)) return null;

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

            var tcs = new TaskCompletionSource<DeviceRuntimeStatus?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnStatus(string did, string json)
            {
                if (!IsSameDeviceIdentity(did, targetId, meshMac, routeId)) return;
                try
                {
                    var data = JObject.Parse(json ?? "{}");
                    tcs.TrySetResult(data.ToObject<DeviceRuntimeStatus>());
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
                if (!sent) return null;

                Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed == tcs.Task)
                    return await tcs.Task.ConfigureAwait(false);
                return null;
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

    public sealed class DeviceFingerprintListResult
    {
        public DeviceFingerprintListResult(
            List<DeviceFingerprintInfo> items, DeviceRuntimeStatus? reportedStatus)
        {
            Items = items;
            ReportedStatus = reportedStatus;
        }

        public List<DeviceFingerprintInfo> Items { get; }
        public DeviceRuntimeStatus? ReportedStatus { get; }
        public int? ReportedFingerprintCount => ReportedStatus?.FingerprintCount;
    }

    /// <summary>设备指纹清单条目</summary>
    public class DeviceFingerprintInfo
    {
        public int FingerprintId { get; set; }
        public string? UserId { get; set; }
        public string? UserCode { get; set; }
        public string? UserName { get; set; }
        public string? Role { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool HasPermission { get; set; }
        public bool Lock0Allowed { get; set; }
        public bool Lock1Allowed { get; set; }
        public bool Lock2Allowed { get; set; }
        public bool Lock3Allowed { get; set; }

        public string PermissionText { get; set; } = "-";

        public string RoleText => Role?.ToLowerInvariant() switch
        {
            "admin" => "管理员",
            "teacher" => "教师",
            "student" => "学生",
            _ => string.IsNullOrWhiteSpace(Role) ? "-" : Role
        };

        public string UserStatusText => IsEnabled ? "启用" : "停用";
        public string BindingStatusText => "已绑定";
        public string Lock0Text => IsEnabled && Lock0Allowed ? "允许" : "-";
        public string Lock1Text => IsEnabled && Lock1Allowed ? "允许" : "-";
        public string Lock2Text => IsEnabled && Lock2Allowed ? "允许" : "-";
        public string Lock3Text => IsEnabled && Lock3Allowed ? "允许" : "-";
        public string PermissionSummaryText
        {
            get
            {
                if (!IsEnabled) return "已停用";
                int count = new[] { Lock0Allowed, Lock1Allowed, Lock2Allowed, Lock3Allowed }
                    .Count(allowed => allowed);
                return count == 0 ? "无权限" : $"{count}/4 可用";
            }
        }

        /// <summary>设备 READ_STATUS 返回的 fingerprint_count（用于显示设备实际模板数）</summary>
        public int? DeviceReportedCount { get; set; }
    }
}
