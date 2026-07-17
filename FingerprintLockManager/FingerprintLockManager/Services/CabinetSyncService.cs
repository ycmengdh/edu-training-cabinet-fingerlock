namespace FingerprintLockManager
{
    /// <summary>
    /// 将根节点上的权限配置下发到柜子。柜子收到后写入本地 Flash，
    /// 后续指纹鉴权完全以本地缓存为准。
    /// </summary>
    public class CabinetSyncService
    {
        public bool SyncAllPermissions()
        {
            // Read the three authority tables from one stable users/permissions
            // version pair. Device heartbeat and log versions are irrelevant.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var before = App.SdStorageService.QueryVersion();
                if (before == null) return false;

                var root = new RootDataService();
                var users = App.UserService.GetAllUsers();
                var roles = root.Read<RolePermission>("role_permissions");
                var overrides = root.Read<UserPermission>("permissions");
                var after = App.SdStorageService.QueryVersion();
                if (after == null) return false;
                if (before.UsersVersion != after.UsersVersion ||
                    before.PermissionsVersion != after.PermissionsVersion)
                {
                    continue;
                }

                return BroadcastTransaction(BuildRows(users, roles, overrides), after.GlobalVersion);
            }
            return false;
        }

        private static List<Dictionary<string, object>> BuildRows(
            List<User> users, List<RolePermission> roles, List<UserPermission> overrides)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var user in users.Where(u => u.FingerprintId.HasValue))
            {
                var role = roles.FirstOrDefault(r => r.Role == user.Role)
                    ?? new RolePermission { Role = user.Role };
                bool[] permissions = role.ToArray();
                foreach (var item in overrides.Where(p => p.UserId == user.UserId))
                {
                    if (item.LockId >= 0 && item.LockId < permissions.Length)
                        permissions[item.LockId] = item.HasAccess;
                }
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

        private static bool BroadcastTransaction(
            List<Dictionary<string, object>> rows, uint version)
        {
            bool sent = App.MeshBridge.Broadcast(Message.Create(
                Protocol.CmdBeginPermissionSync, "", new { version, total = rows.Count }));
            for (int sequence = 0; sequence < rows.Count; sequence++)
            {
                var row = rows[sequence];
                row["version"] = version;
                row["total"] = rows.Count;
                row["sequence"] = sequence;
                sent = App.MeshBridge.Broadcast(Message.Create(
                    Protocol.CmdSyncPermission, "", row)) && sent;
            }
            sent = App.MeshBridge.Broadcast(Message.Create(
                Protocol.CmdCommitPermissionSync, "", new { version, total = rows.Count })) && sent;
            return sent;
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

    }
}
