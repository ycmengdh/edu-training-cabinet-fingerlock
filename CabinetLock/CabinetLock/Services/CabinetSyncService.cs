using System.Collections.Concurrent;

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
        private const int MaxProbeConcurrency = 1;
        private readonly ConcurrentDictionary<string, FingerprintVerification>
            _fingerprintVerifications = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CabinetExpectedSyncState>
            _expectedStateCache = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string, CabinetExpectedSyncState>? SyncStateChanged;

        public Task<IReadOnlyList<UserCabinetSyncResult>> VerifyAndSyncUserAsync(
            User? user, IEnumerable<string>? targetDeviceIds = null,
            IProgress<UserCabinetSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                user == null ? "同步用户" : $"同步用户 {user.UserId}",
                "",
                token => VerifyAndSyncUserCoreAsync(
                    user, targetDeviceIds, progress, token),
                cancellationToken);

        private async Task<IReadOnlyList<UserCabinetSyncResult>> VerifyAndSyncUserCoreAsync(
            User? user, IEnumerable<string>? targetDeviceIds,
            IProgress<UserCabinetSyncProgress>? progress,
            CancellationToken cancellationToken)
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
                    string reason = string.IsNullOrWhiteSpace(restored.ErrorMessage)
                        ? "柜机未返回失败原因"
                        : restored.ErrorMessage;
                    string code = string.IsNullOrWhiteSpace(restored.ErrorCode)
                        ? ""
                        : $"，错误码 {restored.ErrorCode}";
                    return UserCabinetSyncResult.Failed(
                        deviceId, user.UserId,
                        $"指纹更新失败（{reason}{code}），已撤销该用户柜机权限");
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

        public Task<UserCabinetSyncResult> CheckUserOnCabinetAsync(
            User? user, string deviceId, CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"校验柜机 {deviceId} 用户 {user?.UserId}",
                deviceId,
                token => CheckUserOnCabinetCoreAsync(user, deviceId, token),
                cancellationToken);

        private async Task<UserCabinetSyncResult> CheckUserOnCabinetCoreAsync(
            User? user, string deviceId, CancellationToken cancellationToken)
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

        public BroadcastCommandResult SyncAllPermissions() =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                "同步全部柜机权限",
                "",
                _ => Task.FromResult(SyncAllPermissionsCore()))
                .GetAwaiter().GetResult();

        private BroadcastCommandResult SyncAllPermissionsCore()
        {
            PermissionSnapshot? snapshot = ReadStableSnapshot(out string error);
            return snapshot == null
                ? BroadcastCommandResult.Failed(error)
                : SyncTransactionPaced(snapshot.Rows, snapshot.Version);
        }

        public BroadcastCommandResult SyncCabinetPermissions(string deviceId) =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"同步柜机 {deviceId} 权限",
                deviceId,
                _ => Task.FromResult(SyncCabinetPermissionsCore(deviceId)))
                .GetAwaiter().GetResult();

        private BroadcastCommandResult SyncCabinetPermissionsCore(string deviceId)
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
                    return BroadcastCommandResult.Succeeded(new[] { deviceId });
                }
                string reason = string.IsNullOrWhiteSpace(transaction.ErrorMessage)
                    ? $"柜子 {deviceId} 未确认权限同步"
                    : transaction.ErrorMessage;
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
        public Task<CabinetDataSyncResult> SyncCabinetDataAsync(
            string deviceId, IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"同步柜机 {deviceId} 数据",
                deviceId,
                token => SyncCabinetDataCoreAsync(deviceId, progress, token),
                cancellationToken);

        private async Task<CabinetDataSyncResult> SyncCabinetDataCoreAsync(
            string deviceId, IProgress<string>? progress,
            CancellationToken cancellationToken)
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
            var permissionRows = new List<Dictionary<string, object>>(rows);
            var failures = new List<string>();
            int currentCount = 0;
            int restoredCount = 0;

            progress?.Report("正在读取柜机实际指纹槽位");
            IReadOnlyList<FingerprintSlotRecord>? slots = await App.CommandService
                .GetFingerprintSlotsAsync(deviceId).ConfigureAwait(false);
            if (slots == null)
            {
                failures.Add("无法读取柜机实际指纹槽位，未提交权限以保护本机副指纹");
            }
            else
            {
                HashSet<int> expectedFingerprintIds = rows
                    .Select(row => Convert.ToInt32(row["fingerprint_id"]))
                    .ToHashSet();
                FingerprintSlotRecord[] staleSlots = slots.Where(slot =>
                        slot.Slot > 0 && !slot.IsBackup &&
                        !expectedFingerprintIds.Contains(slot.Slot))
                    .ToArray();
                for (int index = 0; index < staleSlots.Length; index++)
                {
                    FingerprintSlotRecord stale = staleSlots[index];
                    progress?.Report($"正在清理残留指纹 {index + 1}/{staleSlots.Length}：槽位 {stale.Slot}");
                    CommandResult deleted = await DeleteFingerprintFromCabinetIdempotentAsync(
                        deviceId, stale.Slot).ConfigureAwait(false);
                    if (!deleted.Success)
                        failures.Add($"残留槽位 {stale.Slot}：{deleted.ErrorMessage}");
                }
                permissionRows.AddRange(slots
                    .Where(slot => slot.IsBackup && slot.Slot > 0 && slot.FingerprintId > 0 &&
                        !string.IsNullOrWhiteSpace(slot.UserId))
                    .Select(BuildBackupPermissionRow));
            }

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
            BroadcastCommandResult permissionResult;
            if (slots == null)
            {
                permissionResult = BroadcastCommandResult.Failed(
                    "未读取到柜机实际槽位，已跳过权限提交", new[] { deviceId });
            }
            else
            {
                progress?.Report($"正在提交 {permissionRows.Count} 条柜机权限");
                CommandResult permissionTransaction = await Task.Run(
                    () => SyncOneCabinet(deviceId, permissionRows, snapshot.Version), cancellationToken)
                    .ConfigureAwait(false);
                permissionResult = permissionTransaction.Success
                    ? BroadcastCommandResult.Succeeded(new[] { deviceId })
                    : new BroadcastCommandResult
                    {
                        ErrorMessage = string.IsNullOrWhiteSpace(permissionTransaction.ErrorMessage)
                            ? $"柜子 {deviceId} 未确认权限同步"
                            : permissionTransaction.ErrorMessage,
                        FailedDeviceIds = new[] { deviceId }
                    };
            }

            var syncResult = new CabinetDataSyncResult
            {
                DeviceId = deviceId,
                ExpectedFingerprintCount = rows.Count,
                PermissionRecordCount = permissionRows.Count,
                CurrentFingerprintCount = currentCount,
                RestoredFingerprintCount = restoredCount,
                FingerprintFailures = failures.ToArray(),
                PermissionResult = permissionResult
            };
            string verificationKey = deviceId.Trim();
            if (failures.Count == 0)
            {
                _fingerprintVerifications[verificationKey] =
                    new FingerprintVerification(snapshot.Version, rows.Count);
                App.MeshBridge.MarkFingerprintSyncConfirmed(
                    deviceId, rows.Count + permissionRows.Count(row =>
                        row.TryGetValue("is_backup", out object? value) && value is true));
            }
            else
                _fingerprintVerifications.TryRemove(verificationKey, out _);
            try
            {
                SyncStateChanged?.Invoke(deviceId,
                    new CabinetExpectedSyncState(snapshot.Version, rows.Count));
            }
            catch
            {
            }
            return syncResult;
        }

        public IReadOnlyDictionary<string, CabinetExpectedSyncState>
            GetExpectedCabinetSyncStates(IEnumerable<string> deviceIds)
        {
            string[] requested = (deviceIds ?? Array.Empty<string>())
                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            uint currentVersion = GetExpectedPermissionVersion();
            if (requested.All(deviceId =>
                    _expectedStateCache.TryGetValue(deviceId, out CabinetExpectedSyncState cached) &&
                    cached.Version == currentVersion))
            {
                return requested.ToDictionary(
                    deviceId => deviceId,
                    deviceId => _expectedStateCache[deviceId],
                    StringComparer.OrdinalIgnoreCase);
            }

            PermissionSnapshot? snapshot = ReadStableSnapshot(out string error);
            if (snapshot == null) throw new InvalidOperationException(error);

            IReadOnlyDictionary<string, List<Dictionary<string, object>>> rowsByDevice =
                BuildRowsForDevices(snapshot.Rows, requested);
            var result = new Dictionary<string, CabinetExpectedSyncState>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string deviceId in requested)
            {
                var expected = new CabinetExpectedSyncState(
                    snapshot.Version, rowsByDevice[deviceId].Count);
                _expectedStateCache[deviceId] = expected;
                result[deviceId] = expected;
            }
            return result;
        }

        public void ApplyExpectedSyncState(Device device, CabinetExpectedSyncState expected)
        {
            ArgumentNullException.ThrowIfNull(device);
            device.RootPermissionVersion = expected.Version;
            device.ExpectedFingerprintCount = expected.ExpectedFingerprintCount;
            device.FingerprintVerificationVersion =
                _fingerprintVerifications.TryGetValue(device.DeviceId, out FingerprintVerification verified) &&
                verified.Version == expected.Version &&
                verified.ExpectedFingerprintCount == expected.ExpectedFingerprintCount
                    ? verified.Version : 0;
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
            => ComposePermissionVersion(usersVersion, 0, permissionsVersion, 0, 0);

        public static uint ComposePermissionVersion(
            uint usersVersion, uint classesVersion, uint permissionsVersion,
            uint fingerprintsVersion)
            => ComposePermissionVersion(usersVersion, classesVersion, permissionsVersion,
                0, fingerprintsVersion);

        public static uint ComposePermissionVersion(
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

        private static Dictionary<string, object> BuildBackupPermissionRow(
            FingerprintSlotRecord slot) => new()
        {
            ["fingerprint_id"] = slot.FingerprintId,
            ["local_fp_id"] = slot.Slot,
            ["is_backup"] = true,
            ["user_id"] = slot.UserId,
            ["name"] = slot.Name,
            ["role"] = slot.Role,
            ["lock_permissions"] = new
            {
                lock_0 = (slot.LockMask & 0x01) != 0,
                lock_1 = (slot.LockMask & 0x02) != 0,
                lock_2 = (slot.LockMask & 0x04) != 0,
                lock_3 = (slot.LockMask & 0x08) != 0
            }
        };

        /// <summary>
        /// 生成单柜权限行：每条记录以 user_id + fingerprint_id 唯一。
        /// 学生仅分配到该柜才下发；未特殊选择时默认下发第一枚有效指纹。
        /// </summary>
        private static List<Dictionary<string, object>> BuildRowsForDevice(
            IEnumerable<Dictionary<string, object>> rows, string deviceId)
        {
            List<User> userList = App.UserService.GetAllUsers();
            List<FingerprintTemplate> templates = BusinessDatabase.ReadAllFpTemplateMetas();
            Dictionary<string, IReadOnlyList<CabinetAssignment>> assignments =
                App.CabinetBindingService.GetAssignments(userList, new[] { deviceId });
            return BuildRowsForDevice(rows, deviceId, userList, templates, assignments);
        }

        private static IReadOnlyDictionary<string, List<Dictionary<string, object>>>
            BuildRowsForDevices(
                IEnumerable<Dictionary<string, object>> rows,
                IReadOnlyCollection<string> deviceIds)
        {
            string[] requested = deviceIds
                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            List<User> users = App.UserService.GetAllUsers();
            List<FingerprintTemplate> templates = BusinessDatabase.ReadAllFpTemplateMetas();
            Dictionary<string, IReadOnlyList<CabinetAssignment>> assignments =
                App.CabinetBindingService.GetAssignments(users, requested);
            return requested.ToDictionary(
                deviceId => deviceId,
                deviceId => BuildRowsForDevice(
                    rows, deviceId, users, templates, assignments),
                StringComparer.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> BuildRowsForDevice(
            IEnumerable<Dictionary<string, object>> rows, string deviceId,
            IReadOnlyCollection<User> userList,
            IReadOnlyCollection<FingerprintTemplate> templates,
            IReadOnlyDictionary<string, IReadOnlyList<CabinetAssignment>> assignments)
        {
            Dictionary<string, User> users = userList
                .ToDictionary(user => user.UserId, StringComparer.OrdinalIgnoreCase);
            var result = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> row in rows)
            {
                string userId = row["user_id"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(userId) || !users.TryGetValue(userId, out User? user))
                    continue;
                assignments.TryGetValue(user.UserId,
                    out IReadOnlyList<CabinetAssignment>? userAssignments);
                CabinetAssignment? assignment = userAssignments?.FirstOrDefault(item =>
                    string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                List<FingerprintTemplate> userTemplates = templates.Where(item =>
                    string.Equals(item.UserId, user.UserId,
                        StringComparison.OrdinalIgnoreCase)).ToList();
                HashSet<int> enabledIds = userTemplates.Where(item => item.Enabled)
                    .Select(item => item.FingerprintId).ToHashSet();
                IReadOnlyList<int> fingerprintIds = (assignment?.FingerprintIds ?? new List<int>())
                    .Where(id => id > 0 && enabledIds.Contains(id))
                    .Distinct().OrderBy(id => id).ToArray();
                if (fingerprintIds.Count == 0)
                {
                    // 管理员和教师自动绑定全部柜机，历史空绑定仍回退到其默认指纹。
                    // 学生必须在当前柜机存在有效的显式指纹选择。
                    if (string.Equals(
                            user.Role, "student", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int? fallback = App.CabinetBindingService
                        .ResolveDefaultFingerprintId(user, userTemplates);
                    fingerprintIds = fallback.HasValue
                        ? new[] { fallback.Value } : Array.Empty<int>();
                }
                bool[] fallbackPermissions = row.TryGetValue("_resolved_permissions", out object? resolved) &&
                    resolved is bool[] values ? values : new bool[4];
                bool[] devicePermissions = fallbackPermissions.Take(4).ToArray();
                Array.Resize(ref devicePermissions, 4);
                if (assignment?.LockIds != null)
                {
                    devicePermissions = new bool[4];
                    foreach (int lockId in assignment.LockIds.Where(id => id >= 0 && id < 4))
                        devicePermissions[lockId] = true;
                }
                PermissionPolicy.Enforce(user.Role, devicePermissions);
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

            IReadOnlyDictionary<string, List<Dictionary<string, object>>> rowsByDevice =
                BuildRowsForDevices(rows, expectedDevices);
            foreach (string deviceId in expectedDevices)
            {
                try
                {
                    List<Dictionary<string, object>> deviceRows = rowsByDevice[deviceId];
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
            if (!committed.Success)
                return StageFailure("提交权限同步", committed);

            App.MeshBridge.MarkPermissionSyncConfirmed(deviceId, version, rows.Count);
            return committed;
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
            return App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.FingerprintEnrollment,
                $"启动柜机 {deviceId} 指纹录入",
                deviceId,
                _ => Task.FromResult(App.MeshBridge.SendToDevice(deviceId,
                    Message.Create(Protocol.CmdAddFingerprint, deviceId, new
                    {
                        fingerprint_id = user.FingerprintId.Value,
                        user_id = user.UserId
                    }))))
                .GetAwaiter().GetResult();
        }

        public bool DeleteFingerprint(string deviceId, int fingerprintId)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || fingerprintId <= 0) return false;
            return App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"删除柜机 {deviceId} 指纹 {fingerprintId}",
                deviceId,
                _ => Task.FromResult(App.MeshBridge.Send(
                    deviceId, Protocol.CmdDeleteFingerprint,
                    new { fingerprint_id = fingerprintId })))
                .GetAwaiter().GetResult();
        }

        public bool DeleteFingerprintFromAll(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            return App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"广播删除指纹 {fingerprintId}",
                "",
                _ => Task.FromResult(App.MeshBridge.Broadcast(Message.Create(
                    Protocol.CmdDeleteFingerprint, "",
                    new { fingerprint_id = fingerprintId }))))
                .GetAwaiter().GetResult();
        }

        /// <summary>
        /// 逐柜删除指纹并等待 ACK，供学生详情和批量删除使用。
        /// 广播删除仍保留给兼容场景；涉及业务数据清理时必须使用此方法确认下位机结果。
        /// </summary>
        public Task<BroadcastCommandResult> DeleteFingerprintFromOnlineCabinetsAsync(
            int fingerprintId, int timeoutMs = 10_000,
            CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"从在线柜机删除指纹 {fingerprintId}",
                "",
                token => DeleteFingerprintFromOnlineCabinetsCoreAsync(
                    fingerprintId, timeoutMs, token),
                cancellationToken);

        private async Task<BroadcastCommandResult> DeleteFingerprintFromOnlineCabinetsCoreAsync(
            int fingerprintId, int timeoutMs, CancellationToken cancellationToken)
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
                cancellationToken.ThrowIfCancellationRequested();
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
            string deviceId, IReadOnlyCollection<string> excludedUserIds) =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"清理柜机 {deviceId} 权限",
                deviceId,
                _ => Task.FromResult(SyncCabinetPermissionsExcludingUsersCore(
                    deviceId, excludedUserIds)))
                .GetAwaiter().GetResult();

        private BroadcastCommandResult SyncCabinetPermissionsExcludingUsersCore(
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

        public Task<CommandResult> DeleteFingerprintFromCabinetIdempotentAsync(
            string deviceId, int fingerprintId, int timeoutMs = 10_000,
            CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.CabinetSync,
                $"清理柜机 {deviceId} 指纹 {fingerprintId}",
                deviceId,
                token => DeleteFingerprintFromCabinetIdempotentCoreAsync(
                    deviceId, fingerprintId, timeoutMs, token),
                cancellationToken);

        private async Task<CommandResult> DeleteFingerprintFromCabinetIdempotentCoreAsync(
            string deviceId, int fingerprintId, int timeoutMs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        private readonly record struct FingerprintVerification(
            uint Version, int ExpectedFingerprintCount);
    }

    public readonly record struct CabinetExpectedSyncState(
        uint Version, int ExpectedFingerprintCount);

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
        public int PermissionRecordCount { get; init; }
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
                    ? $"权限已确认：{PermissionRecordCountOrExpected} 条"
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

        private int PermissionRecordCountOrExpected => PermissionRecordCount > 0
            ? PermissionRecordCount : ExpectedFingerprintCount;

        public static CabinetDataSyncResult Failed(string deviceId, string error) => new()
        {
            DeviceId = deviceId ?? "",
            PermissionResult = BroadcastCommandResult.Failed(error, new[] { deviceId ?? "" })
        };
    }
}
