namespace CabinetLock
{
    /// <summary>
    /// 角色默认权限服务。模板只用于初始化新用户，不直接改变已有用户权限。
    /// </summary>
    public class RolePermissionService
    {
        public List<RolePermission> GetAll()
        {
            return new RootDataService().Read<RolePermission>("role_permissions")
                .Select(PermissionPolicy.Normalize)
                .OrderBy(r => r.Role).ToList();
        }

        public RolePermission GetRolePermission(string role)
        {
            var item = GetAll().FirstOrDefault(r => r.Role == role);
            return item ?? BuildDefault(role);
        }

        public bool SetRolePermission(RolePermission rolePermission)
        {
            if (rolePermission == null || string.IsNullOrWhiteSpace(rolePermission.Role)) return false;
            return SetAll(new[] { rolePermission });
        }

        public bool SetAll(IEnumerable<RolePermission> rolePermissions)
        {
            if (rolePermissions == null) return false;
            EnsureAdministrator();

            var incoming = rolePermissions
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Role))
                .Select(PermissionPolicy.Normalize)
                .ToDictionary(r => r.Role, StringComparer.OrdinalIgnoreCase);
            if (incoming.Count == 0) return false;

            var root = new RootDataService();
            var items = root.Read<RolePermission>("role_permissions");
            if (!SnapshotExistingUserPermissions(root, items)) return false;

            foreach (var pair in incoming)
            {
                pair.Value.UpdateTime = DateTime.Now;
                var existing = items.FirstOrDefault(r =>
                    string.Equals(r.Role, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (existing == null) items.Add(pair.Value);
                else items[items.IndexOf(existing)] = pair.Value;
            }
            return root.Save("role_permissions", items);
        }

        public List<UserPermission> CreateDefaultUserPermissions(string userId, string role)
        {
            if (string.IsNullOrWhiteSpace(userId)) return new List<UserPermission>();

            bool[] defaults = GetRolePermission(role).ToArray();
            PermissionPolicy.Enforce(role, defaults);
            DateTime now = DateTime.Now;
            return Enumerable.Range(0, defaults.Length)
                .Select(lockId => new UserPermission
                {
                    UserId = userId,
                    LockId = lockId,
                    HasAccess = defaults[lockId],
                    UpdateTime = now
                })
                .ToList();
        }

        public void InitDefaultRolePermissions()
        {
            var root = new RootDataService();
            if (root.Read<RolePermission>("role_permissions").Count > 0) return;
            root.Save("role_permissions", new[]
            {
                BuildDefault("admin"), BuildDefault("teacher"), BuildDefault("student")
            });
        }

        public bool[] GetFinalPermissions(string userId)
        {
            return new PermissionService().GetFinalPermissions(userId);
        }

        private static void EnsureAdministrator()
        {
            if (!DataScopeContext.Instance.IsAdmin)
                throw new UnauthorizedAccessException("只有系统管理员可以修改角色默认权限");
        }

        private static bool SnapshotExistingUserPermissions(
            RootDataService root,
            IReadOnlyCollection<RolePermission> currentRoles)
        {
            List<User> users = root.Read<User>("users");
            if (users.Count == 0) return true;

            List<UserPermission> permissions = root.Read<UserPermission>("permissions");
            var existingLocks = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (UserPermission permission in permissions)
            {
                if (!existingLocks.TryGetValue(permission.UserId, out HashSet<int>? lockIds))
                {
                    lockIds = new HashSet<int>();
                    existingLocks[permission.UserId] = lockIds;
                }
                lockIds.Add(permission.LockId);
            }

            var roleMap = currentRoles.ToDictionary(
                role => role.Role,
                PermissionPolicy.Normalize,
                StringComparer.OrdinalIgnoreCase);
            DateTime now = DateTime.Now;
            bool changed = false;

            foreach (User user in users)
            {
                if (!existingLocks.TryGetValue(user.UserId, out HashSet<int>? lockIds))
                {
                    lockIds = new HashSet<int>();
                    existingLocks[user.UserId] = lockIds;
                }

                bool[] defaults = (roleMap.TryGetValue(user.Role, out RolePermission? rolePermission)
                        ? rolePermission
                        : BuildDefault(user.Role))
                    .ToArray();
                PermissionPolicy.Enforce(user.Role, defaults);

                for (int lockId = 0; lockId < defaults.Length; lockId++)
                {
                    if (!lockIds.Add(lockId)) continue;
                    permissions.Add(new UserPermission
                    {
                        UserId = user.UserId,
                        LockId = lockId,
                        HasAccess = defaults[lockId],
                        UpdateTime = now
                    });
                    changed = true;
                }
            }

            return !changed || root.Save("permissions", permissions);
        }

        private static RolePermission BuildDefault(string role)
        {
            return role switch
            {
                "admin" => new RolePermission { Role = "admin", Lock0 = true, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = DateTime.Now },
                "teacher" => new RolePermission { Role = "teacher", Lock0 = false, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = DateTime.Now },
                _ => new RolePermission { Role = "student", Lock0 = false, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = DateTime.Now }
            };
        }
    }
}
