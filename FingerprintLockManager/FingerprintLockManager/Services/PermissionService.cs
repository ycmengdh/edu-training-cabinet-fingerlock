namespace FingerprintLockManager
{
    /// <summary>
    /// 个人权限覆盖服务，数据存放在根节点 permissions.json。
    /// </summary>
    public class PermissionService
    {
        private const int LockCount = 4;
        private readonly RootDataService _root = new RootDataService();
        private readonly RolePermissionService _rolePermissions = new RolePermissionService();

        public List<UserPermission> GetUserPermissions(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return new List<UserPermission>();
            return _root.Read<UserPermission>("permissions")
                .Where(p => p.UserId == userId).OrderBy(p => p.LockId).ToList();
        }

        public (bool hasOverride, bool hasAccess) GetUserPermission(string userId, int lockId)
        {
            var permission = GetUserPermissions(userId).FirstOrDefault(p => p.LockId == lockId);
            return permission == null ? (false, false) : (true, permission.HasAccess);
        }

        public bool[] GetFinalPermissions(string userId)
        {
            var result = _rolePermissions.GetRolePermission(
                App.UserService.GetUser(userId)?.Role ?? "student").ToArray();
            foreach (var permission in GetUserPermissions(userId))
            {
                if (permission.LockId >= 0 && permission.LockId < LockCount)
                    result[permission.LockId] = permission.HasAccess;
            }
            return result;
        }

        public bool SetUserPermission(string userId, int lockId, bool hasAccess)
        {
            if (string.IsNullOrWhiteSpace(userId) || lockId < 0 || lockId >= LockCount)
                return false;

            var permissions = _root.Read<UserPermission>("permissions");
            var existing = permissions.FirstOrDefault(p => p.UserId == userId && p.LockId == lockId);
            if (existing == null)
            {
                permissions.Add(new UserPermission
                {
                    UserId = userId,
                    LockId = lockId,
                    HasAccess = hasAccess,
                    UpdateTime = DateTime.Now
                });
            }
            else
            {
                existing.HasAccess = hasAccess;
                existing.UpdateTime = DateTime.Now;
            }
            return _root.Save("permissions", permissions);
        }

        public bool SetUserPermissions(string userId, Dictionary<int, bool> permissions)
        {
            if (string.IsNullOrWhiteSpace(userId) || permissions == null) return false;
            var items = _root.Read<UserPermission>("permissions");
            foreach (var pair in permissions)
            {
                if (pair.Key < 0 || pair.Key >= LockCount) return false;
                var existing = items.FirstOrDefault(p => p.UserId == userId && p.LockId == pair.Key);
                if (existing == null)
                {
                    items.Add(new UserPermission
                    {
                        UserId = userId,
                        LockId = pair.Key,
                        HasAccess = pair.Value,
                        UpdateTime = DateTime.Now
                    });
                }
                else
                {
                    existing.HasAccess = pair.Value;
                    existing.UpdateTime = DateTime.Now;
                }
            }
            return _root.Save("permissions", items);
        }

        public bool DeleteUserPermission(string userId, int lockId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var items = _root.Read<UserPermission>("permissions");
            items.RemoveAll(p => p.UserId == userId && p.LockId == lockId);
            return _root.Save("permissions", items);
        }

        public bool DeleteAllUserPermissions(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var items = _root.Read<UserPermission>("permissions");
            items.RemoveAll(p => p.UserId == userId);
            return _root.Save("permissions", items);
        }

    }
}
