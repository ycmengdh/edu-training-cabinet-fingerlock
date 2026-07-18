namespace FingerprintLockManager
{
    /// <summary>
    /// 角色默认权限服务，数据存放在根节点 role_permissions.json。
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
            rolePermission = PermissionPolicy.Normalize(rolePermission);
            var root = new RootDataService();
            var items = root.Read<RolePermission>("role_permissions");
            rolePermission.UpdateTime = DateTime.Now;
            var existing = items.FirstOrDefault(r => r.Role == rolePermission.Role);
            if (existing == null) items.Add(rolePermission);
            else items[items.IndexOf(existing)] = rolePermission;
            return root.Save("role_permissions", items);
        }

        public bool SetAll(IEnumerable<RolePermission> rolePermissions)
        {
            if (rolePermissions == null) return false;
            var incoming = rolePermissions
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Role))
                .Select(PermissionPolicy.Normalize)
                .ToDictionary(r => r.Role, StringComparer.OrdinalIgnoreCase);
            if (incoming.Count == 0) return false;

            var root = new RootDataService();
            var items = root.Read<RolePermission>("role_permissions");
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
            var user = App.UserService.GetUser(userId);
            if (user == null) return new bool[4];
            var result = GetRolePermission(user.Role).ToArray();
            foreach (var item in new RootDataService().Read<UserPermission>("permissions")
                         .Where(p => p.UserId == userId))
            {
                if (item.LockId >= 0 && item.LockId < result.Length)
                    result[item.LockId] = item.HasAccess;
            }
            PermissionPolicy.Enforce(user.Role, result);
            return result;
        }

        private static RolePermission BuildDefault(string role)
        {
            return role switch
            {
                "admin" => new RolePermission { Role = "admin", Lock0 = true, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = DateTime.Now },
                "teacher" => new RolePermission { Role = "teacher", Lock0 = false, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = DateTime.Now },
                _ => new RolePermission { Role = "student", Lock0 = false, Lock1 = false, Lock2 = false, Lock3 = false, UpdateTime = DateTime.Now }
            };
        }
    }
}
