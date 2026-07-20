namespace FingerprintLockManager
{
    /// <summary>
    /// 个人权限覆盖服务，数据存放在根节点 permissions.json。
    /// V2.7：写操作加入教师数据范围校验（仅可修改本班学生权限）。
    /// </summary>
    public class PermissionService
    {
        private const int LockCount = 4;
        private readonly RootDataService _root = new RootDataService();
        private readonly RolePermissionService _rolePermissions = new RolePermissionService();
        private static IDataScopeContext Scope => DataScopeContext.Instance;

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
            string role = App.UserService.GetUser(userId)?.Role ?? "student";
            var result = _rolePermissions.GetRolePermission(role).ToArray();
            foreach (var permission in GetUserPermissions(userId))
            {
                if (permission.LockId >= 0 && permission.LockId < LockCount)
                    result[permission.LockId] = permission.HasAccess;
            }
            PermissionPolicy.Enforce(role, result);
            return result;
        }

        public bool SetUserPermission(string userId, int lockId, bool hasAccess)
        {
            if (string.IsNullOrWhiteSpace(userId) || lockId < 0 || lockId >= LockCount)
                return false;

            var user = App.UserService.GetUser(userId);
            if (user == null || (hasAccess && !PermissionPolicy.CanGrant(user.Role, lockId)))
                return false;

            // V2.7：教师只能修改本班学生权限
            Scope.EnsureCanModify(user);

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
            var user = App.UserService.GetUser(userId);
            if (user == null) return false;

            // V2.7：教师只能修改本班学生权限
            Scope.EnsureCanModify(user);

            var items = _root.Read<UserPermission>("permissions");
            foreach (var pair in permissions)
            {
                if (pair.Key < 0 || pair.Key >= LockCount) return false;
                if (pair.Value && !PermissionPolicy.CanGrant(user.Role, pair.Key)) return false;
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
            var user = App.UserService.GetUser(userId);
            if (user != null) Scope.EnsureCanModify(user);
            var items = _root.Read<UserPermission>("permissions");
            items.RemoveAll(p => p.UserId == userId && p.LockId == lockId);
            return _root.Save("permissions", items);
        }

        public bool DeleteAllUserPermissions(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var user = App.UserService.GetUser(userId);
            if (user != null) Scope.EnsureCanModify(user);
            var items = _root.Read<UserPermission>("permissions");
            items.RemoveAll(p => p.UserId == userId);
            return _root.Save("permissions", items);
        }

        /// <summary>
        /// V2.7：获取所有已分配权限覆盖记录的用户 ID 集合（用于统计学生绑定设备数）。
        /// </summary>
        public HashSet<string> GetAllBoundUserIds()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in _root.Read<UserPermission>("permissions"))
                {
                    if (!string.IsNullOrWhiteSpace(p.UserId)) result.Add(p.UserId);
                }
            }
            catch { /* SD 不可用时返回空集 */ }
            return result;
        }

    }
}
