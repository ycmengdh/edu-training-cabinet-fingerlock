namespace FingerprintLockManager
{
    /// <summary>
    /// 角色默认权限服务
    /// 管理角色（admin/teacher/student）的默认权限模板，作为双层权限模型的第一层。
    /// 核心方法 GetFinalPermissions：COALESCE 合并角色默认 + 个人覆盖，返回最终权限 bool[4]。
    /// 数据持久化于根节点 SD 卡 role_permissions.json。
    /// </summary>
    public class RolePermissionService
    {
        /// <summary>锁总数</summary>
        private const int LockCount = 4;

        /// <summary>获取所有角色默认权限</summary>
        public List<RolePermission> GetAll()
        {
            try
            {
                return DataStore.Current.GetRolePermissions()
                    .OrderBy(r => r.Role)
                    .ToList();
            }
            catch
            {
                return new List<RolePermission>();
            }
        }

        /// <summary>获取指定角色的默认权限；不存在返回内置默认值</summary>
        public RolePermission GetRolePermission(string role)
        {
            try
            {
                if (string.IsNullOrEmpty(role)) return BuildDefault(role);
                var rp = DataStore.Current.GetRolePermissions()
                    .FirstOrDefault(r => r.Role == role);
                return rp ?? BuildDefault(role);
            }
            catch
            {
                return BuildDefault(role);
            }
        }

        /// <summary>更新或创建角色默认权限</summary>
        public bool SetRolePermission(RolePermission rolePermission)
        {
            try
            {
                if (rolePermission == null || string.IsNullOrEmpty(rolePermission.Role)) return false;
                rolePermission.UpdateTime = DateTime.Now;

                bool found = false;
                DataStore.Current.MutateRolePermissions(list =>
                {
                    int idx = list.FindIndex(r => r.Role == rolePermission.Role);
                    if (idx >= 0)
                    {
                        list[idx] = rolePermission;
                        found = true;
                    }
                    else
                    {
                        list.Add(rolePermission);
                        found = true;
                    }
                });
                return found;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 初始化 3 条默认角色权限记录（admin/teacher/student）。
        /// 仅在表为空时插入，避免覆盖已有配置。
        /// DataStore 加载时已自动调用，此方法保留供手动初始化。
        /// </summary>
        public void InitDefaultRolePermissions()
        {
            try
            {
                if (DataStore.Current.GetRolePermissions().Count > 0) return;

                var now = DateTime.Now;
                var defaults = new List<RolePermission>
                {
                    new() { Role = "admin", Lock0 = true, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                    new() { Role = "teacher", Lock0 = false, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                    new() { Role = "student", Lock0 = false, Lock1 = false, Lock2 = false, Lock3 = false, UpdateTime = now }
                };

                DataStore.Current.MutateRolePermissions(list =>
                {
                    if (list.Count == 0)
                    {
                        list.AddRange(defaults);
                    }
                });
            }
            catch
            {
                // 初始化失败忽略
            }
        }

        /// <summary>
        /// 核心方法：获取用户最终权限（双层 COALESCE 合并）
        /// 以角色默认权限为基础，若存在个人覆盖项则用覆盖值替换对应锁。
        /// </summary>
        public bool[] GetFinalPermissions(string userId)
        {
            bool[] result = new bool[LockCount];
            try
            {
                if (string.IsNullOrEmpty(userId)) return result;

                // 查询用户以获取角色
                var user = DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.UserId == userId);
                if (user == null) return result;

                // 第一层：角色默认权限
                var rolePerm = GetRolePermission(user.Role);
                result[0] = rolePerm.Lock0;
                result[1] = rolePerm.Lock1;
                result[2] = rolePerm.Lock2;
                result[3] = rolePerm.Lock3;

                // 第二层：个人覆盖项（COALESCE，存在覆盖则替换）
                var overrides = DataStore.Current.GetUserPermissions()
                    .Where(p => p.UserId == userId)
                    .ToList();
                foreach (var p in overrides)
                {
                    if (p.LockId >= 0 && p.LockId < LockCount)
                    {
                        result[p.LockId] = p.HasAccess;
                    }
                }

                return result;
            }
            catch
            {
                return result;
            }
        }

        /// <summary>构建内置默认角色权限（未入库时的兜底）</summary>
        private static RolePermission BuildDefault(string role)
        {
            var now = DateTime.Now;
            return role switch
            {
                "admin" => new RolePermission { Role = "admin", Lock0 = true, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                "teacher" => new RolePermission { Role = "teacher", Lock0 = false, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                _ => new RolePermission { Role = "student", Lock0 = false, Lock1 = false, Lock2 = false, Lock3 = false, UpdateTime = now },
            };
        }
    }
}
