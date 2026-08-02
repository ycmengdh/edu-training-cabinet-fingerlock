namespace CabinetLock
{
    /// <summary>
    /// 将根节点上的权限配置下发到柜子。柜子收到后写入本地 Flash，
    /// 后续指纹鉴权完全以本地缓存为准。
    /// 100 柜场景：按柜 unicast + 行间 pacing，避免 Mesh 广播风暴。
    /// </summary>
    public class CabinetSyncService
    {
        /// <summary>单柜 SYNC 行间隔（毫秒），与固件 PERM_SYNC_INTER_ROW_MS 对齐量级。</summary>
        private const int InterRowDelayMs = 40;

        /// <summary>柜与柜之间的间隔（毫秒）。</summary>
        private const int InterNodeDelayMs = 100;
        private const int MaxProbeConcurrency = 3;

        public async Task<IReadOnlyList<UserCabinetSyncResult>> VerifyAndSyncUserAsync(
            User user, IEnumerable<string>? targetDeviceIds = null,
            IProgress<UserCabinetSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (user == null || !user.Enabled)
                return Array.Empty<UserCabinetSyncResult>();

            HashSet<string>? requested = targetDeviceIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var onlineDevices = App.MeshBridge.GetOnlineDevices()
                .Where(device => device.IsOnline && !device.IsRoot &&
                    !string.IsNullOrWhiteSpace(device.DeviceId))
                .Where(device => requested == null || requested.Contains(device.DeviceId))
                .ToList();
            HashSet<string>? assignedDeviceIds = string.Equals(
                    user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? App.CabinetBindingService.GetAssignedDeviceIds(
                    user, onlineDevices.Select(device => device.DeviceId))
                : null;
            var devices = onlineDevices
                .Where(device => assignedDeviceIds == null || assignedDeviceIds.Contains(device.DeviceId))
                .OrderBy(device => device.DeviceId)
                .ToList();
            if (devices.Count == 0) return Array.Empty<UserCabinetSyncResult>();

            bool[] defaultPermissions = App.PermissionService.GetFinalPermissions(user.UserId);
            PermissionPolicy.Enforce(user.Role, defaultPermissions);
            uint expectedVersion = GetExpectedPermissionVersion();
            var results = new System.Collections.Concurrent.ConcurrentBag<UserCabinetSyncResult>();
            using var gate = new SemaphoreSlim(MaxProbeConcurrency);
            int completed = 0;
            await Task.WhenAll(devices.Select(async device =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    progress?.Report(new UserCabinetSyncProgress(
                        device.DeviceId, user.UserId, "正在校验", completed, devices.Count));
                    IReadOnlyList<int> fingerprintIds = App.CabinetBindingService
                        .GetSelectedFingerprintIds(user, device.DeviceId);
                    bool[] expectedPermissions = App.CabinetBindingService
                        .GetLockPermissions(user, device.DeviceId, defaultPermissions);
                    UserCabinetSyncResult result;
                    if (fingerprintIds.Count == 0)
                    {
                        result = UserCabinetSyncResult.Failed(
                            device.DeviceId, user.UserId, "该柜机尚未选择用户指纹");
                    }
                    else
                    {
                        var itemResults = new List<UserCabinetSyncResult>();
                        foreach (int fingerprintId in fingerprintIds)
                        {
                            byte[]? template = await App.FingerprintTemplateService
                                .GetTemplateBytesAsync(fingerprintId).ConfigureAwait(false);
                            itemResults.Add(template == null || template.Length == 0
                                ? UserCabinetSyncResult.Failed(device.DeviceId, user.UserId,
                                    $"指纹 #{fingerprintId} 的本地和 SD 模板均不可用")
                                : await VerifyAndSyncOneAsync(
                                    device.DeviceId, user, fingerprintId, template,
                                    expectedPermissions, expectedVersion, cancellationToken)
                                    .ConfigureAwait(false));
                        }
                        UserCabinetSyncResult? failed = itemResults.FirstOrDefault(item => !item.Success);
                        result = failed ?? new UserCabinetSyncResult
                        {
                            DeviceId = device.DeviceId,
                            UserId = user.UserId,
                            Success = true,
                            FingerprintUpdated = itemResults.Any(item => item.FingerprintUpdated),
                            PermissionUpdated = itemResults.Any(item => item.PermissionUpdated)
                        };
                    }
                    results.Add(result);
                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new UserCabinetSyncProgress(
                        device.DeviceId, user.UserId, result.StatusText, done, devices.Count));
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            List<UserCabinetSyncResult> ordered = results.OrderBy(result => result.DeviceId).ToList();
            foreach (UserCabinetSyncResult result in ordered)
                App.CabinetSyncQueueService.RecordUserOutcome(
                    user.UserId, result.DeviceId, result.Success, result.ErrorMessage);
            return ordered;
        }

        private static async Task<UserCabinetSyncResult> VerifyAndSyncOneAsync(
            string deviceId, User user, int fingerprintId, byte[] template, bool[] expectedPermissions,
            uint expectedVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FingerprintProbeResult? fingerprint = await App.CommandService.QueryFingerprintAsync(
                deviceId, fingerprintId, template).ConfigureAwait(false);
            if (fingerprint == null)
                return UserCabinetSyncResult.Failed(deviceId, user.UserId, "指纹校验超时");

            bool fingerprintUpdated = false;
            if (!fingerprint.Matches)
            {
                CommandResult restored = await App.CommandService.RestoreFingerprintAsync(
                    deviceId, user.UserId, fingerprintId, template)
                    .ConfigureAwait(false);
                if (!restored.Success)
                {
                    await App.CommandService.UpsertPermissionAsync(
                        deviceId, user, new bool[4], expectedVersion).ConfigureAwait(false);
                    return UserCabinetSyncResult.Failed(
                        deviceId, user.UserId, "指纹更新失败，已撤销该用户柜机权限");
                }
                fingerprintUpdated = true;
            }

            PermissionProbeResult? permission = await App.CommandService.QueryPermissionAsync(
                deviceId, user.UserId, fingerprintId).ConfigureAwait(false);
            bool permissionMatches = permission != null && permission.Found &&
                permission.FingerprintId == fingerprintId &&
                permission.Role == RoleToNumber(user.Role) &&
                permission.Permissions.SequenceEqual(expectedPermissions);
            bool permissionUpdated = false;
            if (!permissionMatches)
            {
                CommandResult upserted = await App.CommandService.UpsertPermissionAsync(
                    deviceId, WithFingerprint(user, fingerprintId), expectedPermissions.ToArray(), expectedVersion)
                    .ConfigureAwait(false);
                if (!upserted.Success)
                    return UserCabinetSyncResult.Failed(deviceId, user.UserId, "权限更新失败");
                permissionUpdated = true;
            }

            return new UserCabinetSyncResult
            {
                DeviceId = deviceId,
                UserId = user.UserId,
                Success = true,
                FingerprintUpdated = fingerprintUpdated,
                PermissionUpdated = permissionUpdated
            };
        }

        public async Task<UserCabinetSyncResult> CheckUserOnCabinetAsync(
            User user, string deviceId, CancellationToken cancellationToken = default)
        {
            if (user == null || !user.Enabled)
                return UserCabinetSyncResult.Failed(deviceId, user?.UserId ?? "", "用户已停用");
            IReadOnlyList<int> fingerprintIds = App.CabinetBindingService
                .GetSelectedFingerprintIds(user, deviceId);
            if (fingerprintIds.Count == 0)
                return UserCabinetSyncResult.Failed(deviceId, user.UserId, "该柜机尚未选择用户指纹");

            bool[] defaultPermissions = App.PermissionService.GetFinalPermissions(user.UserId);
            PermissionPolicy.Enforce(user.Role, defaultPermissions);
            bool[] expectedPermissions = App.CabinetBindingService
                .GetLockPermissions(user, deviceId, defaultPermissions);
            bool needsFingerprintUpdate = false;
            bool needsPermissionUpdate = false;
            foreach (int fingerprintId in fingerprintIds)
            {
                byte[]? template = await App.FingerprintTemplateService
                    .GetTemplateBytesAsync(fingerprintId).ConfigureAwait(false);
                if (template == null || template.Length == 0)
                    return UserCabinetSyncResult.Failed(deviceId, user.UserId,
                        $"指纹 #{fingerprintId} 模板不可用");
                cancellationToken.ThrowIfCancellationRequested();
                FingerprintProbeResult? fingerprint = await App.CommandService.QueryFingerprintAsync(
                    deviceId, fingerprintId, template).ConfigureAwait(false);
                if (fingerprint == null)
                    return UserCabinetSyncResult.Failed(deviceId, user.UserId, "指纹校验超时");
                PermissionProbeResult? permission = await App.CommandService.QueryPermissionAsync(
                    deviceId, user.UserId, fingerprintId).ConfigureAwait(false);
                if (permission == null)
                    return UserCabinetSyncResult.Failed(deviceId, user.UserId, "权限校验超时");
                needsFingerprintUpdate |= !fingerprint.Matches;
                needsPermissionUpdate |= !permission.Found || permission.FingerprintId != fingerprintId ||
                    permission.Role != RoleToNumber(user.Role) ||
                    !permission.Permissions.SequenceEqual(expectedPermissions);
            }
            return new UserCabinetSyncResult
            {
                DeviceId = deviceId,
                UserId = user.UserId,
                Success = true,
                NeedsFingerprintUpdate = needsFingerprintUpdate,
                NeedsPermissionUpdate = needsPermissionUpdate
            };
        }

        public BroadcastCommandResult SyncAllPermissions()
        {
            PermissionSnapshot? snapshot = ReadStableSnapshot(out string error);
            return snapshot == null
                ? BroadcastCommandResult.Failed(error)
                : SyncTransactionPaced(snapshot.Rows, snapshot.Version);
        }

        public BroadcastCommandResult SyncCabinetPermissions(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return BroadcastCommandResult.Failed("柜子 ID 不能为空");

            bool online = App.MeshBridge.GetOnlineDevices().Any(device =>
                device.IsOnline && !device.IsRoot &&
                string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (!online)
                return BroadcastCommandResult.Failed("柜子当前不在线", new[] { deviceId });

            PermissionSnapshot? snapshot = ReadStableSnapshot(out string error);
            if (snapshot == null)
                return BroadcastCommandResult.Failed(error, new[] { deviceId });

            try
            {
                var rows = BuildRowsForDevice(snapshot.Rows, deviceId);
                CommandResult transaction = SyncOneCabinet(deviceId, rows, snapshot.Version);
                if (transaction.Success)
                {
                    App.CabinetSyncQueueService.RecordCabinetOutcome(deviceId, true);
                    return BroadcastCommandResult.Succeeded(new[] { deviceId });
                }
                string reason = string.IsNullOrWhiteSpace(transaction.ErrorMessage)
                    ? $"柜子 {deviceId} 未确认权限同步"
                    : transaction.ErrorMessage;
                App.CabinetSyncQueueService.RecordCabinetOutcome(
                    deviceId, false, reason);
                return new BroadcastCommandResult
                {
                    ErrorMessage = reason,
                    FailedDeviceIds = new[] { deviceId }
                };
            }
            catch (Exception ex)
            {
                return new BroadcastCommandResult
                {
                    ErrorMessage = ex.Message,
                    FailedDeviceIds = new[] { deviceId }
                };
            }
        }

        /// <summary>
        /// 使单台柜机的用户指纹模板与权限记录同时收敛到上位机数据。
        /// 已存在且 CRC 一致的模板不会重复写入指纹模块。
        /// </summary>
        public async Task<CabinetDataSyncResult> SyncCabinetDataAsync(
            string deviceId, IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return CabinetDataSyncResult.Failed(deviceId, "柜子 ID 不能为空");

            bool online = App.MeshBridge.GetOnlineDevices().Any(device =>
                device.IsOnline && !device.IsRoot &&
                string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (!online)
                return CabinetDataSyncResult.Failed(deviceId, "柜子当前不在线");

            PermissionSnapshot? snapshot = ReadStableSnapshot(out string snapshotError);
            if (snapshot == null)
                return CabinetDataSyncResult.Failed(deviceId, snapshotError);

            var rows = BuildRowsForDevice(snapshot.Rows, deviceId);
            var failures = new List<string>();
            int currentCount = 0;
            int restoredCount = 0;

            for (int index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dictionary<string, object> row = rows[index];
                string userId = row["user_id"]?.ToString() ?? "";
                string name = row["name"]?.ToString() ?? userId;
                if (!int.TryParse(row["fingerprint_id"]?.ToString(), out int fingerprintId) ||
                    fingerprintId <= 0)
                {
                    failures.Add($"{name}（{userId}）：指纹 ID 无效");
                    continue;
                }

                progress?.Report($"正在校验用户指纹 {index + 1}/{rows.Count}：{name}");
                byte[]? template = await App.FingerprintTemplateService
                    .GetTemplateBytesAsync(fingerprintId).ConfigureAwait(false);
                if (template == null || template.Length == 0)
                {
                    failures.Add($"{name}（{userId}，ID {fingerprintId}）：本机和 SD 均无模板");
                    continue;
                }

                FingerprintProbeResult? probe = await App.CommandService.QueryFingerprintAsync(
                    deviceId, fingerprintId, template, 8_000).ConfigureAwait(false);
                if (probe?.Matches == true)
                {
                    currentCount++;
                    continue;
                }

                progress?.Report($"正在补写用户指纹 {index + 1}/{rows.Count}：{name}");
                CommandResult restored = await App.CommandService.RestoreFingerprintAsync(
                    deviceId, userId, fingerprintId, template).ConfigureAwait(false);
                if (restored.Success)
                {
                    restoredCount++;
                    continue;
                }

                string reason = string.IsNullOrWhiteSpace(restored.ErrorMessage)
                    ? "柜机未确认写入"
                    : restored.ErrorMessage;
                failures.Add($"{name}（{userId}，ID {fingerprintId}）：{reason}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在提交 {rows.Count} 条柜机权限");
            CommandResult permissionTransaction = await Task.Run(
                () => SyncOneCabinet(deviceId, rows, snapshot.Version), cancellationToken)
                .ConfigureAwait(false);
            BroadcastCommandResult permissionResult = permissionTransaction.Success
                ? BroadcastCommandResult.Succeeded(new[] { deviceId })
                : new BroadcastCommandResult
                {
                    ErrorMessage = string.IsNullOrWhiteSpace(permissionTransaction.ErrorMessage)
                        ? $"柜子 {deviceId} 未确认权限同步"
                        : permissionTransaction.ErrorMessage,
                    FailedDeviceIds = new[] { deviceId }
                };

            var syncResult = new CabinetDataSyncResult
            {
                DeviceId = deviceId,
                ExpectedFingerprintCount = rows.Count,
                CurrentFingerprintCount = currentCount,
                RestoredFingerprintCount = restoredCount,
                FingerprintFailures = failures.ToArray(),
                PermissionResult = permissionResult
            };
            App.CabinetSyncQueueService.RecordCabinetOutcome(
                deviceId, syncResult.Success, syncResult.Success ? "" : syncResult.FormatForDisplay());
            return syncResult;
        }

        private static PermissionSnapshot? ReadStableSnapshot(out string error)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                PermissionDataVersions before = ReadLocalVersions();

                var root = new RootDataService();
                var users = App.UserService.GetAllUsers();
                var classes = root.Read<ClassInfo>("classes");
                var roles = root.Read<RolePermission>("role_permissions");
                var overrides = root.Read<UserPermission>("permissions");
                PermissionDataVersions after = ReadLocalVersions();
                if (before != after)
                    continue;

                error = "";
                return new PermissionSnapshot(
                    BuildRows(users, classes, roles, overrides), ComposePermissionVersion(after));
            }

            error = "权限数据在读取过程中被并发修改，请重试";
            return null;
        }

        public static uint GetExpectedPermissionVersion() =>
            ComposePermissionVersion(ReadLocalVersions());

        private static PermissionDataVersions ReadLocalVersions() => new(
            BusinessDatabase.GetTableVersion("users"),
            BusinessDatabase.GetTableVersion("classes"),
            BusinessDatabase.GetTableVersion("permissions"),
            BusinessDatabase.GetTableVersion("role_permissions"),
            BusinessDatabase.GetTableVersion("fingerprints"));

        private static uint ComposePermissionVersion(PermissionDataVersions versions)
            => ComposePermissionVersion(versions.Users, versions.Classes,
                versions.Permissions, versions.RolePermissions, versions.Fingerprints);

        public static uint ComposePermissionVersion(uint usersVersion, uint permissionsVersion)
            => ComposePermissionVersion(usersVersion, 0, permissionsVersion, permissionsVersion, 0);

        private static uint ComposePermissionVersion(
            uint usersVersion, uint classesVersion, uint permissionsVersion,
            uint rolePermissionsVersion, uint fingerprintsVersion)
        {
            unchecked
            {
                uint value = 2166136261;
                value = (value ^ usersVersion) * 16777619;
                value = (value ^ classesVersion) * 16777619;
                value = (value ^ permissionsVersion) * 16777619;
                value = (value ^ rolePermissionsVersion) * 16777619;
                value = (value ^ fingerprintsVersion) * 16777619;
                return value == 0 ? 1u : value;
            }
        }

        private static List<Dictionary<string, object>> BuildRows(
            List<User> users, List<ClassInfo> classes,
            List<RolePermission> roles, List<UserPermission> overrides)
        {
            var rows = new List<Dictionary<string, object>>();
            HashSet<string> disabledClassIds = classes
                .Where(item => !item.Enabled && !string.IsNullOrWhiteSpace(item.ClassId))
                .Select(item => item.ClassId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<FingerprintTemplate> templates = BusinessDatabase.ReadAllFpTemplateMetas();
            Dictionary<string, RolePermission> roleMap = roles
                .Where(item => !string.IsNullOrWhiteSpace(item.Role))
                .GroupBy(item => item.Role, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<UserPermission>> permissionMap = overrides
                .Where(item => !string.IsNullOrWhiteSpace(item.UserId))
                .GroupBy(item => item.UserId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var user in users.Where(u => u.Enabled).Where(user =>
                         !string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase) ||
                         string.IsNullOrWhiteSpace(user.ClassId) ||
                         !disabledClassIds.Contains(user.ClassId)))
            {
                int? fingerprintId = App.CabinetBindingService.ResolveDefaultFingerprintId(user, templates);
                if (!fingerprintId.HasValue) continue;
                bool[] permissions = ResolveSnapshotPermissions(user, roleMap, permissionMap);
                rows.Add(new Dictionary<string, object>
                {
                    ["fingerprint_id"] = fingerprintId.Value,
                    ["user_id"] = user.UserId,
                    ["name"] = user.Name,
                    ["role"] = RoleToNumber(user.Role),
                    ["_resolved_permissions"] = permissions,
                    ["lock_permissions"] = new
                    {
                        lock_0 = permissions.Length > 0 && permissions[0],
                        lock_1 = permissions.Length > 1 && permissions[1],
                        lock_2 = permissions.Length > 2 && permissions[2],
                        lock_3 = permissions.Length > 3 && permissions[3]
                    }
                });
            }
            return rows;
        }

        private static bool[] ResolveSnapshotPermissions(
            User user,
            IReadOnlyDictionary<string, RolePermission> roleMap,
            IReadOnlyDictionary<string, List<UserPermission>> permissionMap)
        {
            bool[] permissions = roleMap.TryGetValue(user.Role, out RolePermission? role)
                ? role.ToArray()
                : user.Role switch
                {
                    "admin" => new[] { true, true, true, true },
                    "teacher" => new[] { false, true, true, true },
                    _ => new[] { false, false, false, false }
                };
            if (permissionMap.TryGetValue(user.UserId, out List<UserPermission>? userPermissions))
            {
                foreach (UserPermission permission in userPermissions)
                {
                    if (permission.LockId >= 0 && permission.LockId < permissions.Length)
                        permissions[permission.LockId] = permission.HasAccess;
                }
            }
            PermissionPolicy.Enforce(user.Role, permissions);
            return permissions;
        }

        /// <summary>
        /// 生成单柜权限行：每条记录以 user_id + fingerprint_id 唯一。
        /// 学生仅分配到该柜才下发；未特殊选择时默认下发第一枚有效指纹。
        /// </summary>
        private static List<Dictionary<string, object>> BuildRowsForDevice(
            IEnumerable<Dictionary<string, object>> rows, string deviceId)
        {
            Dictionary<string, User> users = App.UserService.GetAllUsers()
                .ToDictionary(user => user.UserId, StringComparer.OrdinalIgnoreCase);
            List<FingerprintTemplate> templates = BusinessDatabase.ReadAllFpTemplateMetas();
            var result = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> row in rows)
            {
                string userId = row["user_id"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(userId) || !users.TryGetValue(userId, out User? user))
                    continue;
                IReadOnlyList<int> fingerprintIds = App.CabinetBindingService
                    .GetSelectedFingerprintIds(user, deviceId, templates);
                bool[] fallbackPermissions = row.TryGetValue("_resolved_permissions", out object? resolved) &&
                    resolved is bool[] values ? values : new bool[4];
                bool[] devicePermissions = App.CabinetBindingService
                    .GetLockPermissions(user, deviceId, fallbackPermissions);
                foreach (int fingerprintId in fingerprintIds)
                {
                    var deviceRow = new Dictionary<string, object>(row)
                    {
                        ["fingerprint_id"] = fingerprintId,
                        ["lock_permissions"] = new
                        {
                            lock_0 = devicePermissions.ElementAtOrDefault(0),
                            lock_1 = devicePermissions.ElementAtOrDefault(1),
                            lock_2 = devicePermissions.ElementAtOrDefault(2),
                            lock_3 = devicePermissions.ElementAtOrDefault(3)
                        }
                    };
                    deviceRow.Remove("_resolved_permissions");
                    result.Add(deviceRow);
                }
            }
            return result
                .OrderBy(row => row["user_id"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => Convert.ToInt32(row["fingerprint_id"]))
                .ToList();
        }

        /// <summary>
        /// 按在线柜子 unicast 事务：BEGIN → N×SYNC（pacing）→ COMMIT（等 SYNC_ACK）。
        /// 失败柜单独汇总，不因单柜失败中止全部。
        /// </summary>
        private static BroadcastCommandResult SyncTransactionPaced(
            List<Dictionary<string, object>> rows, uint version)
        {
            string[] expectedDevices = App.MeshBridge.GetOnlineDevices()
                .Where(device => device.IsOnline && !device.IsRoot)
                .Select(device => device.DeviceId)
                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (expectedDevices.Length == 0)
                return BroadcastCommandResult.Failed("没有在线柜子可确认同步");

            var confirmed = new List<string>();
            var failed = new List<string>();
            string lastError = "";

            foreach (string deviceId in expectedDevices)
            {
                try
                {
                    var deviceRows = BuildRowsForDevice(rows, deviceId);
                    CommandResult transaction = SyncOneCabinet(deviceId, deviceRows, version);
                    if (transaction.Success) confirmed.Add(deviceId);
                    else
                    {
                        failed.Add(deviceId);
                        lastError = string.IsNullOrWhiteSpace(transaction.ErrorMessage)
                            ? $"柜子 {deviceId} 权限同步失败"
                            : transaction.ErrorMessage;
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(deviceId);
                    lastError = ex.Message;
                }
                Thread.Sleep(InterNodeDelayMs);
            }

            if (failed.Count == 0)
                return BroadcastCommandResult.Succeeded(confirmed.ToArray());

            return new BroadcastCommandResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrEmpty(lastError)
                    ? "部分柜子权限同步失败"
                    : lastError,
                ConfirmedDeviceIds = confirmed.ToArray(),
                FailedDeviceIds = failed.ToArray(),
                MissingDeviceIds = Array.Empty<string>(),
            };
        }

        private static CommandResult SyncOneCabinet(
            string deviceId, List<Dictionary<string, object>> rows, uint version)
        {
            CommandResult begin = App.CommandService.SendAsync(
                deviceId,
                Message.Create(Protocol.CmdBeginPermissionSync, deviceId,
                    new { version, total = rows.Count }),
                10_000).GetAwaiter().GetResult();
            if (!begin.Success)
                return StageFailure("开始权限同步", begin);

            Thread.Sleep(InterRowDelayMs);

            for (int sequence = 0; sequence < rows.Count; sequence++)
            {
                var row = new Dictionary<string, object>(rows[sequence])
                {
                    ["version"] = version,
                    ["total"] = rows.Count,
                    ["sequence"] = sequence
                };
                CommandResult staged = App.CommandService.SendAsync(
                    deviceId,
                    Message.Create(Protocol.CmdSyncPermission, deviceId, row),
                    10_000).GetAwaiter().GetResult();
                if (!staged.Success)
                    return StageFailure($"写入第 {sequence + 1}/{rows.Count} 条权限", staged);
                Thread.Sleep(InterRowDelayMs);
            }

            var commit = Message.Create(Protocol.CmdCommitPermissionSync, deviceId,
                new { version, total = rows.Count });
            CommandResult committed = App.CommandService.SendAsync(
                deviceId, commit, 15_000).GetAwaiter().GetResult();
            return committed.Success
                ? committed
                : StageFailure("提交权限同步", committed);
        }

        private static CommandResult StageFailure(string stage, CommandResult result)
        {
            string reason = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "柜机未确认"
                : result.ErrorMessage;
            return CommandResult.Failed($"{stage}失败：{reason}", result.ErrorCode);
        }

        public bool StartEnrollment(string deviceId, User user)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || user?.FingerprintId == null) return false;
            var message = Message.Create(Protocol.CmdAddFingerprint, deviceId, new
            {
                fingerprint_id = user.FingerprintId.Value,
                user_id = user.UserId
            });
            return App.MeshBridge.SendToDevice(deviceId, message);
        }

        public bool DeleteFingerprint(string deviceId, int fingerprintId)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || fingerprintId <= 0) return false;
            return App.MeshBridge.Send(deviceId, Protocol.CmdDeleteFingerprint,
                new { fingerprint_id = fingerprintId });
        }

        public bool DeleteFingerprintFromAll(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            return App.MeshBridge.Broadcast(Message.Create(
                Protocol.CmdDeleteFingerprint, "", new { fingerprint_id = fingerprintId }));
        }

        /// <summary>
        /// 逐柜删除指纹并等待 ACK，供学生详情和批量删除使用。
        /// 广播删除仍保留给兼容场景；涉及业务数据清理时必须使用此方法确认下位机结果。
        /// </summary>
        public async Task<BroadcastCommandResult> DeleteFingerprintFromOnlineCabinetsAsync(
            int fingerprintId, int timeoutMs = 10_000)
        {
            if (fingerprintId <= 0)
                return BroadcastCommandResult.Failed("指纹 ID 无效");

            string[] deviceIds = App.MeshBridge.GetOnlineDevices()
                .Where(device => device.IsOnline && !device.IsRoot && !string.IsNullOrWhiteSpace(device.DeviceId))
                .Select(device => device.DeviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (deviceIds.Length == 0)
                return BroadcastCommandResult.Failed("没有在线柜子可确认删除");

            var confirmed = new List<string>();
            var failed = new List<string>();
            foreach (string deviceId in deviceIds)
            {
                var result = await App.CommandService.SendAsync(
                    deviceId,
                    Message.Create(Protocol.CmdDeleteFingerprint, deviceId,
                        new { fingerprint_id = fingerprintId }),
                    timeoutMs);
                if (result.Success) confirmed.Add(deviceId);
                else failed.Add(deviceId);
            }

            return new BroadcastCommandResult
            {
                Success = failed.Count == 0,
                ConfirmedDeviceIds = confirmed.ToArray(),
                FailedDeviceIds = failed.ToArray(),
                ErrorMessage = failed.Count == 0 ? "" : "部分柜子未确认删除"
            };
        }

        public BroadcastCommandResult SyncCabinetPermissionsExcludingUsers(
            string deviceId, IReadOnlyCollection<string> excludedUserIds)
        {
            PermissionSnapshot? snapshot = ReadStableSnapshot(out string error);
            if (snapshot == null)
                return BroadcastCommandResult.Failed(error, new[] { deviceId });

            HashSet<string> excluded = excludedUserIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> filtered = snapshot.Rows
                .Where(row => !excluded.Contains(row["user_id"]?.ToString() ?? ""))
                .ToList();
            List<Dictionary<string, object>> rows = BuildRowsForDevice(filtered, deviceId);
            CommandResult result = SyncOneCabinet(deviceId, rows, snapshot.Version);
            return result.Success
                ? BroadcastCommandResult.Succeeded(new[] { deviceId })
                : BroadcastCommandResult.Failed(
                    string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? $"柜子 {deviceId} 未确认权限清理"
                        : result.ErrorMessage,
                    new[] { deviceId });
        }

        public async Task<CommandResult> DeleteFingerprintFromCabinetIdempotentAsync(
            string deviceId, int fingerprintId, int timeoutMs = 10_000)
        {
            CommandResult deleted = await App.CommandService.SendAsync(
                deviceId,
                Message.Create(Protocol.CmdDeleteFingerprint, deviceId,
                    new { fingerprint_id = fingerprintId }),
                timeoutMs).ConfigureAwait(false);
            if (!deleted.Success) return deleted;

            FingerprintProbeResult? probe = await App.CommandService.QueryFingerprintAsync(
                deviceId, fingerprintId, Array.Empty<byte>(), timeoutMs).ConfigureAwait(false);
            return probe == null
                ? CommandResult.Failed("删除后指纹校验超时")
                : probe.Exists
                    ? CommandResult.Failed("柜机确认后指纹仍存在")
                    : CommandResult.Succeeded("fingerprint_absent");
        }

        private static int RoleToNumber(string role)
        {
            return role switch
            {
                "admin" => 0,
                "teacher" => 1,
                _ => 2
            };
        }

        private static User WithFingerprint(User user, int fingerprintId) => new()
        {
            UserId = user.UserId,
            UserCode = user.UserCode,
            Name = user.Name,
            Gender = user.Gender,
            Role = user.Role,
            ClassId = user.ClassId,
            ClassIds = user.ClassIds?.ToList(),
            AssignedDeviceIds = user.AssignedDeviceIds,
            CabinetAssignments = user.CabinetAssignments,
            FingerprintId = fingerprintId,
            PasswordSalt = user.PasswordSalt,
            PasswordHash = user.PasswordHash,
            Enabled = user.Enabled,
            CreateTime = user.CreateTime,
            UpdateTime = user.UpdateTime
        };

        /// <summary>格式化同步结果，供界面展示失败/超时设备。</summary>
        public static string FormatSyncResult(
            BroadcastCommandResult result, string successText, string partialPrefix)
        {
            if (result.Success) return successText;

            var parts = new List<string> { partialPrefix };
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                parts.Add(result.ErrorMessage);
            if (result.FailedDeviceIds.Length > 0)
                parts.Add("失败设备: " + string.Join(", ", result.FailedDeviceIds));
            if (result.MissingDeviceIds.Length > 0)
                parts.Add("未确认设备: " + string.Join(", ", result.MissingDeviceIds));
            if (result.ConfirmedDeviceIds.Length > 0)
                parts.Add("已确认: " + string.Join(", ", result.ConfirmedDeviceIds));
            return string.Join("\n", parts);
        }

        private sealed class PermissionSnapshot
        {
            public PermissionSnapshot(List<Dictionary<string, object>> rows, uint version)
            {
                Rows = rows;
                Version = version;
            }

            public List<Dictionary<string, object>> Rows { get; }
            public uint Version { get; }
        }

        private readonly record struct PermissionDataVersions(
            uint Users, uint Classes, uint Permissions, uint RolePermissions, uint Fingerprints);
    }

    public sealed class UserCabinetSyncResult
    {
        public string DeviceId { get; init; } = "";
        public string UserId { get; init; } = "";
        public bool Success { get; init; }
        public bool FingerprintUpdated { get; init; }
        public bool PermissionUpdated { get; init; }
        public bool NeedsFingerprintUpdate { get; init; }
        public bool NeedsPermissionUpdate { get; init; }
        public string ErrorMessage { get; init; } = "";
        public bool Changed => FingerprintUpdated || PermissionUpdated;
        public bool NeedsUpdate => NeedsFingerprintUpdate || NeedsPermissionUpdate;
        public string StatusText => !Success ? ErrorMessage : NeedsUpdate ? "需同步" : Changed ? "已更新" : "已同步";

        public static UserCabinetSyncResult Failed(
            string deviceId, string userId, string error) => new()
        {
            DeviceId = deviceId,
            UserId = userId,
            ErrorMessage = error
        };
    }

    public sealed record UserCabinetSyncProgress(
        string DeviceId, string UserId, string Status, int Completed, int Total);

    public sealed class CabinetDataSyncResult
    {
        public string DeviceId { get; init; } = "";
        public int ExpectedFingerprintCount { get; init; }
        public int CurrentFingerprintCount { get; init; }
        public int RestoredFingerprintCount { get; init; }
        public string[] FingerprintFailures { get; init; } = Array.Empty<string>();
        public BroadcastCommandResult PermissionResult { get; init; } =
            BroadcastCommandResult.Failed("尚未执行权限同步");

        public int ConfirmedFingerprintCount =>
            CurrentFingerprintCount + RestoredFingerprintCount;

        public bool Success => PermissionResult.Success && FingerprintFailures.Length == 0;

        public string FormatForDisplay()
        {
            var lines = new List<string>
            {
                PermissionResult.Success
                    ? $"权限已确认：{ExpectedFingerprintCount} 条"
                    : $"权限未完成：{PermissionResult.ErrorMessage}",
                $"用户指纹已确认：{ConfirmedFingerprintCount}/{ExpectedFingerprintCount}（原有 {CurrentFingerprintCount}，补写 {RestoredFingerprintCount}）"
            };
            if (FingerprintFailures.Length > 0)
            {
                lines.Add("未完成项：");
                lines.AddRange(FingerprintFailures.Select(item => "• " + item));
            }
            return string.Join(Environment.NewLine, lines);
        }

        public static CabinetDataSyncResult Failed(string deviceId, string error) => new()
        {
            DeviceId = deviceId ?? "",
            PermissionResult = BroadcastCommandResult.Failed(error, new[] { deviceId ?? "" })
        };
    }
}
