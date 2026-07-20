namespace FingerprintLockManager
{
    /// <summary>
    /// 将根节点上的权限配置下发到柜子。柜子收到后写入本地 Flash，
    /// 后续指纹鉴权完全以本地缓存为准。
    /// 40 柜场景：按柜 unicast + 行间 pacing，避免 Mesh 广播风暴。
    /// </summary>
    public class CabinetSyncService
    {
        /// <summary>单柜 SYNC 行间隔（毫秒），与固件 PERM_SYNC_INTER_ROW_MS 对齐量级。</summary>
        private const int InterRowDelayMs = 40;

        /// <summary>柜与柜之间的间隔（毫秒）。</summary>
        private const int InterNodeDelayMs = 100;

        public BroadcastCommandResult SyncAllPermissions()
        {
            // Read the three authority tables from one stable users/permissions
            // version pair. Device heartbeat and log versions are irrelevant.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var before = App.SdStorageService.QueryVersion();
                if (before == null)
                    return BroadcastCommandResult.Failed("读取根节点版本失败");

                var root = new RootDataService();
                var users = App.UserService.GetAllUsers();
                var roles = root.Read<RolePermission>("role_permissions");
                var overrides = root.Read<UserPermission>("permissions");
                var after = App.SdStorageService.QueryVersion();
                if (after == null)
                    return BroadcastCommandResult.Failed("读取根节点版本失败");
                if (before.UsersVersion != after.UsersVersion ||
                    before.PermissionsVersion != after.PermissionsVersion)
                {
                    continue;
                }

                return SyncTransactionPaced(BuildRows(users, roles, overrides), after.GlobalVersion);
            }
            return BroadcastCommandResult.Failed("权限数据在读取过程中被并发修改，请重试");
        }

        private static List<Dictionary<string, object>> BuildRows(
            List<User> users, List<RolePermission> roles, List<UserPermission> overrides)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var user in users.Where(u => u.Enabled && u.FingerprintId.HasValue))
            {
                var role = roles.FirstOrDefault(r => r.Role == user.Role)
                    ?? new RolePermission { Role = user.Role };
                bool[] permissions = role.ToArray();
                foreach (var item in overrides.Where(p => p.UserId == user.UserId))
                {
                    if (item.LockId >= 0 && item.LockId < permissions.Length)
                        permissions[item.LockId] = item.HasAccess;
                }
                PermissionPolicy.Enforce(user.Role, permissions);
                rows.Add(new Dictionary<string, object>
                {
                    ["fingerprint_id"] = user.FingerprintId!.Value,
                    ["user_id"] = user.UserId,
                    ["name"] = user.Name,
                    ["role"] = RoleToNumber(user.Role),
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
                    bool ok = SyncOneCabinet(deviceId, rows, version);
                    if (ok) confirmed.Add(deviceId);
                    else
                    {
                        failed.Add(deviceId);
                        lastError = $"柜子 {deviceId} 权限同步失败";
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

        private static bool SyncOneCabinet(
            string deviceId, List<Dictionary<string, object>> rows, uint version)
        {
            // BEGIN
            if (!App.MeshBridge.SendToDevice(deviceId, Message.Create(
                    Protocol.CmdBeginPermissionSync, deviceId,
                    new { version, total = rows.Count })))
                return false;

            Thread.Sleep(InterRowDelayMs);

            // SYNC rows (unicast, paced)
            for (int sequence = 0; sequence < rows.Count; sequence++)
            {
                var row = new Dictionary<string, object>(rows[sequence])
                {
                    ["version"] = version,
                    ["total"] = rows.Count,
                    ["sequence"] = sequence
                };
                if (!App.MeshBridge.SendToDevice(deviceId, Message.Create(
                        Protocol.CmdSyncPermission, deviceId, row)))
                    return false;
                Thread.Sleep(InterRowDelayMs);
            }

            // COMMIT unicast — wait for SYNC_ACK (CommandService supports unicast when DeviceId set)
            var commit = Message.Create(Protocol.CmdCommitPermissionSync, deviceId,
                new { version, total = rows.Count });
            BroadcastCommandResult result = App.CommandService.SendBroadcastAsync(
                commit, new[] { deviceId }, 15_000).GetAwaiter().GetResult();
            return result.Success;
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

        private static int RoleToNumber(string role)
        {
            return role switch
            {
                "admin" => 0,
                "teacher" => 1,
                _ => 2
            };
        }

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
    }
}
