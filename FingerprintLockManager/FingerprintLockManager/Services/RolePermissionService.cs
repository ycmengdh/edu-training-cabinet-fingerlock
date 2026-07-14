namespace FingerprintLockManager
{
    /// <summary>
    /// 角色默认权限服务
    /// 管理角色（admin/teacher/student）的默认权限模板，作为双层权限模型的第一层。
    /// 核心方法 GetFinalPermissions：COALESCE 合并角色默认 + 个人覆盖，返回最终权限 bool[4]。
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
                var list = DatabaseService.Fsql.Select<RolePermission>()
                    .OrderBy(r => r.Role)
                    .ToList();
                return list;
            }
            catch
            {
                return new List<RolePermission>();
            }
        }

        /// <summary>
        /// 获取指定角色的默认权限
        /// </summary>
        /// <param name="role">角色名：admin/teacher/student</param>
        /// <returns>角色权限对象；不存在或异常返回该角色的内置默认值</returns>
        public RolePermission GetRolePermission(string role)
        {
            try
            {
                if (string.IsNullOrEmpty(role)) return BuildDefault(role);
                var rp = DatabaseService.Fsql.Select<RolePermission>()
                    .Where(r => r.Role == role)
                    .First();
                return rp ?? BuildDefault(role);
            }
            catch
            {
                return BuildDefault(role);
            }
        }

        /// <summary>
        /// 更新或创建角色默认权限
        /// </summary>
        /// <param name="rolePermission">角色权限对象</param>
        /// <returns>成功返回 true；失败返回 false</returns>
        public bool SetRolePermission(RolePermission rolePermission)
        {
            try
            {
                if (rolePermission == null || string.IsNullOrEmpty(rolePermission.Role)) return false;
                rolePermission.UpdateTime = DateTime.Now;

                var existing = DatabaseService.Fsql.Select<RolePermission>()
                    .Where(r => r.Role == rolePermission.Role)
                    .First();

                if (existing != null)
                {
                    int rows = DatabaseService.Fsql.Update<RolePermission>()
                        .SetSource(rolePermission)
                        .ExecuteAffrows();
                    return rows > 0;
                }
                else
                {
                    int rows = DatabaseService.Fsql.Insert(rolePermission).ExecuteAffrows();
                    return rows > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 初始化 3 条默认角色权限记录（admin/teacher/student）
        /// 仅在表为空时插入，避免覆盖已有配置。
        /// </summary>
        public void InitDefaultRolePermissions()
        {
            try
            {
                bool hasAny = DatabaseService.Fsql.Select<RolePermission>().Any();
                if (hasAny) return;

                var now = DateTime.Now;
                var defaults = new List<RolePermission>
                {
                    new RolePermission { Role = "admin", Lock0 = true, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                    new RolePermission { Role = "teacher", Lock0 = false, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                    new RolePermission { Role = "student", Lock0 = false, Lock1 = false, Lock2 = false, Lock3 = false, UpdateTime = now }
                };

                DatabaseService.Fsql.Insert<RolePermission>()
                    .AppendData(defaults)
                    .ExecuteAffrows();
            }
            catch
            {
                // 初始化失败忽略，避免影响启动
            }
        }

        /// <summary>
        /// 核心方法：获取用户最终权限（双层 COALESCE 合并）
        /// 算法：以角色默认权限为基础，若存在个人覆盖项则用覆盖值替换对应锁。
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <returns>4 把锁的最终权限 bool[4]；用户不存在返回全 false</returns>
        public bool[] GetFinalPermissions(string userId)
        {
            bool[] result = new bool[LockCount];
            try
            {
                if (string.IsNullOrEmpty(userId)) return result;

                // 查询用户以获取角色
                var user = DatabaseService.Fsql.Select<User>()
                    .Where(u => u.UserId == userId)
                    .First();
                if (user == null) return result;

                // 第一层：角色默认权限
                var rolePerm = GetRolePermission(user.Role);
                result[0] = rolePerm.Lock0;
                result[1] = rolePerm.Lock1;
                result[2] = rolePerm.Lock2;
                result[3] = rolePerm.Lock3;

                // 第二层：个人覆盖项（COALESCE，存在覆盖则替换）
                var overrides = DatabaseService.Fsql.Select<UserPermission>()
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
