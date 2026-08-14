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

        /// <summary>只读取指定用户的指纹元数据，供详情和录入窗口使用。</summary>
        public List<FingerprintTemplate> GetTemplatesForUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return new List<FingerprintTemplate>();
            return BusinessDatabase.ReadFpTemplateMetasForUsers(new[] { userId })
                .OrderBy(template => template.EnrollTime)
                .ThenBy(template => template.FingerprintId)
                .ToList();
        }

        public void ApplyFingerprintSummaries(IReadOnlyCollection<User> users)
        {
            if (users == null || users.Count == 0) return;
            List<FingerprintTemplate> templates = BusinessDatabase.ReadFpTemplateMetasForUsers(
                users.Select(user => user.UserId));
            Dictionary<string, List<FingerprintTemplate>> templatesByUser = templates
                .Where(template => template.Enabled && template.FingerprintId > 0 &&
                    !string.IsNullOrWhiteSpace(template.UserId))
                .GroupBy(template => template.UserId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.GroupBy(template => template.FingerprintId)
                        .Select(items => items.Last())
                        .OrderBy(template => template.FingerIndex)
                        .ThenBy(template => template.FingerprintId)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (User user in users)
            {
                List<FingerprintTemplate> owned = templatesByUser.GetValueOrDefault(user.UserId)
                    ?? new List<FingerprintTemplate>();
                user.FingerprintCount = owned.Count;
                user.EffectiveFingerprintId = owned.Any(template =>
                        template.FingerprintId == user.FingerprintId)
                    ? user.FingerprintId
                    : owned.Select(template => (int?)template.FingerprintId).FirstOrDefault();
            }
        }

        public static Dictionary<string, int> BuildEnabledTemplateCounts(
            IEnumerable<FingerprintTemplate> templates) => (templates ?? Array.Empty<FingerprintTemplate>())
            .Where(template => template.Enabled && template.FingerprintId > 0 &&
                !string.IsNullOrWhiteSpace(template.UserId))
            .GroupBy(template => template.UserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(template => template.FingerprintId).Distinct().Count(),
                StringComparer.OrdinalIgnoreCase);

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
        public Task<byte[]?> GetTemplateBytesAsync(int fingerprintId) =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.SdSync,
                $"读取指纹模板 {fingerprintId}",
                App.SdStorageService.RootDeviceId,
                _ => GetTemplateBytesCoreAsync(fingerprintId));

        private async Task<byte[]?> GetTemplateBytesCoreAsync(int fingerprintId)
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
            return BusinessDatabase.ReadFpTemplateMetasForUsers(new[] { userId })
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
            User? boundUser = null;
            try
            {
                boundUser = _userService.GetUser(userId);
                if (boundUser == null) return false;
                userName = boundUser.Name;
            }
            catch (RootDataUnavailableException)
            {
            }

            if (!BusinessDatabase.BindFpTemplateToUser(fingerprintId, userId, userName))
                return false;

            try
            {
                User? user = _userService.GetUser(userId);
                bool assigned = user?.FingerprintId.HasValue == true ||
                    _userService.AssignFingerprint(userId, fingerprintId);
                if (!assigned) return false;

                if (IsGlobalStaff(user ?? boundUser))
                    QueueGlobalStaffSync(user ?? boundUser!);
                return true;
            }
            catch (RootDataUnavailableException)
            {
                return true;
            }
        }

        /// <summary>
        /// 为升级前已存在的管理员/教师指纹补建全柜同步任务。
        /// 已有用户级任务（含已完成任务）不会在每次启动时重复创建。
        /// </summary>
        public int EnsureGlobalStaffSyncQueued()
        {
            List<User> staff = _userService.GetAllUsers()
                .Where(user => user.Enabled && IsGlobalStaff(user))
                .ToList();
            if (staff.Count == 0) return 0;

            HashSet<string> ownersWithFingerprint = BusinessDatabase.ReadAllFpTemplateMetas()
                .Where(template => template.Enabled && template.FingerprintId > 0 &&
                    !string.IsNullOrWhiteSpace(template.UserId))
                .Select(template => template.UserId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] deviceIds = App.DeviceService.GetAllDevices()
                .Where(device => !DeviceService.IsTrueRoot(device) &&
                    !string.IsNullOrWhiteSpace(device.DeviceId) &&
                    !CabinetSyncQueueService.IsRootTarget(device.DeviceId))
                .Select(device => device.DeviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (deviceIds.Length == 0) return 0;

            HashSet<string> existing = App.CabinetSyncQueueService.GetAll()
                .Where(job => string.Equals(job.JobKind, "user", StringComparison.OrdinalIgnoreCase))
                .Select(job => GlobalStaffJobKey(job.UserId, job.DeviceId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int queued = 0;
            foreach (User user in staff.Where(user => ownersWithFingerprint.Contains(user.UserId)))
            {
                foreach (string deviceId in deviceIds)
                {
                    if (!existing.Add(GlobalStaffJobKey(user.UserId, deviceId))) continue;
                    App.CabinetSyncQueueService.EnqueueUser(
                        user.UserId, new[] { deviceId }, "管理员/教师指纹自动同步");
                    queued++;
                }
            }
            if (queued > 0) App.CabinetSyncQueueService.Trigger();
            return queued;
        }

        private static void QueueGlobalStaffSync(User user)
        {
            try
            {
                string[] deviceIds = App.DeviceService.GetAllDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device) &&
                        !string.IsNullOrWhiteSpace(device.DeviceId) &&
                        !CabinetSyncQueueService.IsRootTarget(device.DeviceId))
                    .Select(device => device.DeviceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                App.CabinetSyncQueueService.EnqueueUser(
                    user.UserId, deviceIds, "管理员/教师指纹自动同步");
                App.CabinetSyncQueueService.Trigger();
            }
            catch
            {
                // 指纹已落库；队列将在启动补偿阶段再次建立。
            }
        }

        private static bool IsGlobalStaff(User? user) => user != null &&
            (string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(user.Role, "teacher", StringComparison.OrdinalIgnoreCase));

        private static string GlobalStaffJobKey(string userId, string deviceId) =>
            $"{userId.Trim()}\n{deviceId.Trim()}";

        // ===== 下发到柜子 =====

        /// <summary>
        /// 下发单个模板到指定柜子。失败时 error 给出可读原因（无模板字节、链路、固件拒写等）。
        /// </summary>
        public Task<(bool ok, string error)> DistributeToDeviceDetailedAsync(
            int fingerprintId, string deviceId) =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"向柜机 {deviceId} 下发指纹 {fingerprintId}",
                deviceId,
                _ => DistributeToDeviceDetailedCoreAsync(fingerprintId, deviceId));

        private async Task<(bool ok, string error)> DistributeToDeviceDetailedCoreAsync(
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

        public Task<bool> UploadToSdAsync(int fingerprintId) =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.SdSync,
                $"备份指纹模板 {fingerprintId} 到 SD",
                App.SdStorageService.RootDeviceId,
                _ => UploadToSdCoreAsync(fingerprintId));

        private async Task<bool> UploadToSdCoreAsync(int fingerprintId)
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
        /// 查询指定柜子传感器的实际占用槽位，并关联本地用户与模板元数据。
        /// 旧固件不支持槽位清单时，回退为本地绑定推算结果。
        /// </summary>
        public async Task<DeviceFingerprintListResult> GetDeviceFingerprintListAsync(
            string deviceId, int statusTimeoutMs = 2500, bool queryRuntimeStatus = true)
        {
            var result = new List<DeviceFingerprintInfo>();
            if (string.IsNullOrWhiteSpace(deviceId))
                return new DeviceFingerprintListResult(result, null);

            Task<DeviceRuntimeStatus?> statusTask = queryRuntimeStatus
                ? QueryDeviceRuntimeStatusAsync(deviceId, statusTimeoutMs)
                : Task.FromResult<DeviceRuntimeStatus?>(null);
            Task<IReadOnlyList<FingerprintSlotRecord>?> slotsTask =
                App.CommandService.GetFingerprintSlotsAsync(deviceId);
            Task<List<User>> usersTask = Task.Run(() =>
            {
                try { return _userService.GetAllUsers(); }
                catch { return new List<User>(); }
            });
            Task<List<FingerprintTemplate>> templatesTask = Task.Run(() =>
            {
                try { return GetAllTemplates(); }
                catch { return new List<FingerprintTemplate>(); }
            });

            await Task.WhenAll(statusTask, slotsTask, usersTask, templatesTask)
                .ConfigureAwait(false);
            DeviceRuntimeStatus? reportedStatus = statusTask.Result;
            int deviceFpCount = reportedStatus?.FingerprintCount ?? -1;
            List<User> users = usersTask.Result;
            IReadOnlyList<FingerprintSlotRecord>? slots = slotsTask.Result;
            Dictionary<string, User> usersById = users
                .Where(user => !string.IsNullOrWhiteSpace(user.UserId))
                .ToDictionary(user => user.UserId, StringComparer.OrdinalIgnoreCase);
            Dictionary<int, FingerprintTemplate> templatesById = templatesTask.Result
                .Where(template => template.FingerprintId > 0)
                .GroupBy(template => template.FingerprintId)
                .ToDictionary(group => group.Key, group => group.Last());

            if (slots != null)
            {
                foreach (FingerprintSlotRecord slot in slots.Where(item => item.Slot >= 0))
                {
                    usersById.TryGetValue(slot.UserId, out User? user);
                    templatesById.TryGetValue(slot.FingerprintId, out FingerprintTemplate? template);
                    string role = !slot.Bound ? "" : user?.Role ?? slot.Role switch
                    {
                        0 => "admin",
                        1 => "teacher",
                        _ => "student"
                    };
                    result.Add(new DeviceFingerprintInfo
                    {
                        SlotId = slot.Slot,
                        FingerprintId = slot.FingerprintId,
                        UserId = slot.UserId,
                        UserCode = user?.DisplayId ?? slot.UserId,
                        UserName = user?.Name ?? slot.Name,
                        FingerName = template?.FingerDisplayName ?? "",
                        Role = role,
                        IsEnabled = user?.Enabled ?? true,
                        IsBound = slot.Bound,
                        IsBackup = slot.IsBackup,
                        HasPermission = slot.LockMask != 0,
                        Lock0Allowed = (slot.LockMask & 0x01) != 0,
                        Lock1Allowed = (slot.LockMask & 0x02) != 0,
                        Lock2Allowed = (slot.LockMask & 0x04) != 0,
                        Lock3Allowed = (slot.LockMask & 0x08) != 0,
                        DeviceReportedCount = deviceFpCount >= 0
                            ? deviceFpCount : slots.Count
                    });
                }
                return new DeviceFingerprintListResult(result, reportedStatus);
            }

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
                        SlotId = fingerprintId,
                        FingerprintId = fingerprintId,
                        UserId = u.UserId,
                        UserCode = u.DisplayId,
                        UserName = u.Name,
                        Role = u.Role,
                        IsEnabled = u.Enabled,
                        IsBound = true,
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
        public Task<DeviceRuntimeStatus?> QueryDeviceRuntimeStatusAsync(
            string deviceId, int timeoutMs = 2500) =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"读取柜机 {deviceId} 实时状态",
                deviceId,
                _ => QueryDeviceRuntimeStatusCoreAsync(deviceId, timeoutMs));

        private async Task<DeviceRuntimeStatus?> QueryDeviceRuntimeStatusCoreAsync(
            string deviceId, int timeoutMs)
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
                int[] retryDelaysMs = { 250, 500, 1000 };
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                for (int attempt = 0; attempt <= retryDelaysMs.Length; attempt++)
                {
                    App.MeshBridge.SendToDevice(routeId, msg);
                    if (!string.Equals(routeId, targetId, StringComparison.OrdinalIgnoreCase))
                        App.MeshBridge.SendToDevice(targetId, msg);

                    int remainingMs = (int)Math.Max(0,
                        (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remainingMs == 0) break;
                    int waitMs = attempt < retryDelaysMs.Length
                        ? Math.Min(retryDelaysMs[attempt], remainingMs)
                        : remainingMs;
                    Task completed = await Task.WhenAny(tcs.Task, Task.Delay(waitMs))
                        .ConfigureAwait(false);
                    if (completed == tcs.Task)
                        return await tcs.Task.ConfigureAwait(false);
                }
                return tcs.Task.IsCompleted
                    ? await tcs.Task.ConfigureAwait(false)
                    : null;
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
        public int SlotId { get; set; }
        public int FingerprintId { get; set; }
        public string? UserId { get; set; }
        public string? UserCode { get; set; }
        public string? UserName { get; set; }
        public string FingerName { get; set; } = "";
        public string? Role { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsBound { get; set; }
        public bool IsBackup { get; set; }
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

        public string FingerprintIdText => FingerprintId > 0
            ? FingerprintId.ToString() : "-";
        public string UserCodeText => string.IsNullOrWhiteSpace(UserCode) ? "-" : UserCode;
        public string UserNameText => string.IsNullOrWhiteSpace(UserName) ? "未绑定用户" : UserName;
        public string FingerNameText => string.IsNullOrWhiteSpace(FingerName) ? "-" : FingerName;
        public string UserStatusText => IsEnabled ? "启用" : "停用";
        public string BindingStatusText => SlotId == 0 ? "临时槽" :
            IsBackup ? "本机副指纹" : IsBound ? "正式绑定" : "未绑定残留";
        public string Lock0Text => IsBound && IsEnabled && Lock0Allowed ? "允许" : "-";
        public string Lock1Text => IsBound && IsEnabled && Lock1Allowed ? "允许" : "-";
        public string Lock2Text => IsBound && IsEnabled && Lock2Allowed ? "允许" : "-";
        public string Lock3Text => IsBound && IsEnabled && Lock3Allowed ? "允许" : "-";
        public string PermissionSummaryText
        {
            get
            {
                if (!IsBound) return "未绑定";
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
