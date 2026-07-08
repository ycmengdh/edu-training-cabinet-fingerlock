namespace FingerprintLockManager
{
    /// <summary>
    /// 权限管理服务
    /// 负责用户对锁（Lock0-3）的访问权限管理，支持按角色默认权限与按指纹查询权限
    /// </summary>
    public class PermissionService
    {
        /// <summary>锁总数（Lock0-3，共 4 把）</summary>
        private const int LockCount = 4;

        /// <summary>
        /// 获取用户所有权限
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <returns>权限列表；异常时返回空列表</returns>
        public List<Permission> GetUserPermissions(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return new List<Permission>();

                return DatabaseService.Fsql.Select<Permission>()
                    .Where(p => p.UserId == userId)
                    .OrderBy(p => p.LockId)
                    .ToList();
            }
            catch
            {
                return new List<Permission>();
            }
        }

        /// <summary>
        /// 获取用户对某把锁的权限
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="lockId">锁编号 0-3</param>
        /// <returns>有权限返回 true；无权限或异常返回 false</returns>
        public bool HasPermission(string userId, int lockId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;

                return DatabaseService.Fsql.Select<Permission>()
                    .Where(p => p.UserId == userId && p.LockId == lockId && p.HasAccess)
                    .Any();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取指纹对应的用户权限（返回 4 个锁的 bool 数组，用于下发给 ESP32）
        /// 优先查数据库权限表，若数据库无记录则按角色默认权限返回：
        ///   admin:   [true, true, true, true]
        ///   teacher: [false, true, true, true]
        ///   student: [false, false, false, false]
        /// </summary>
        /// <param name="fingerprintId">指纹 ID</param>
        /// <returns>4 个锁的权限数组；指纹未注册时返回全 false</returns>
        public bool[] GetPermissionsByFingerprint(int fingerprintId)
        {
            // 默认全 false
            bool[] result = new bool[LockCount];

            try
            {
                // 根据指纹 ID 查找用户
                var user = DatabaseService.Fsql.Select<User>()
                    .Where(u => u.FingerprintId == fingerprintId)
                    .First();

                // 指纹未注册
                if (user == null) return result;

                // 查询数据库中的权限记录
                var permissions = DatabaseService.Fsql.Select<Permission>()
                    .Where(p => p.UserId == user.UserId)
                    .ToList();

                if (permissions != null && permissions.Count > 0)
                {
                    // 数据库有记录：按记录填充，未记录的锁默认 false
                    foreach (var p in permissions)
                    {
                        if (p.LockId >= 0 && p.LockId < LockCount)
                        {
                            result[p.LockId] = p.HasAccess;
                        }
                    }
                }
                else
                {
                    // 数据库无记录：按角色默认权限返回
                    result = GetDefaultPermissionsByRole(user.Role);
                }

                return result;
            }
            catch
            {
                return result;
            }
        }

        /// <summary>
        /// 获取指纹对应的用户信息和权限（用于验证时回复 AUTH_OK）
        /// </summary>
        /// <param name="fingerprintId">指纹 ID</param>
        /// <returns>用户对象与 4 个锁的权限数组；指纹未注册返回 (null, 全 false)</returns>
        public (User user, bool[] permissions) VerifyByFingerprint(int fingerprintId)
        {
            bool[] empty = new bool[LockCount];

            try
            {
                var user = DatabaseService.Fsql.Select<User>()
                    .Where(u => u.FingerprintId == fingerprintId)
                    .First();

                if (user == null) return (null, empty);

                bool[] permissions = GetPermissionsByFingerprint(fingerprintId);
                return (user, permissions);
            }
            catch
            {
                return (null, empty);
            }
        }

        /// <summary>
        /// 设置用户权限（老师分配学生权限）
        /// 若权限记录已存在则更新，否则新增
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="lockId">锁编号 0-3</param>
        /// <param name="hasAccess">是否有访问权限</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool SetPermission(string userId, int lockId, bool hasAccess)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;
                if (lockId < 0 || lockId >= LockCount) return false;

                // 查询是否已存在权限记录
                var existing = DatabaseService.Fsql.Select<Permission>()
                    .Where(p => p.UserId == userId && p.LockId == lockId)
                    .First();

                if (existing != null)
                {
                    // 更新已有记录
                    existing.HasAccess = hasAccess;
                    existing.UpdateTime = DateTime.Now;

                    int rows = DatabaseService.Fsql.Update<Permission>()
                        .SetSource(existing)
                        .ExecuteAffrows();
                    return rows > 0;
                }
                else
                {
                    // 新增权限记录
                    var permission = new Permission
                    {
                        UserId = userId,
                        LockId = lockId,
                        HasAccess = hasAccess,
                        UpdateTime = DateTime.Now
                    };

                    int rows = DatabaseService.Fsql.Insert(permission).ExecuteAffrows();
                    return rows > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 批量设置权限
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="permissions">锁编号与权限的字典</param>
        /// <returns>全部成功返回 true；任意一项失败或异常返回 false</returns>
        public bool SetPermissions(string userId, Dictionary<int, bool> permissions)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || permissions == null) return false;

                foreach (var kv in permissions)
                {
                    if (!SetPermission(userId, kv.Key, kv.Value))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 初始化用户的默认权限（创建用户时调用）
        ///   admin:   全 true
        ///   teacher: Lock0=false，其余 true
        ///   student: 全 false
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="role">角色</param>
        public void InitDefaultPermissions(string userId, string role)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(role)) return;

                bool[] defaults = GetDefaultPermissionsByRole(role);

                for (int i = 0; i < LockCount; i++)
                {
                    var permission = new Permission
                    {
                        UserId = userId,
                        LockId = i,
                        HasAccess = defaults[i],
                        UpdateTime = DateTime.Now
                    };
                    DatabaseService.Fsql.Insert(permission).ExecuteAffrows();
                }
            }
            catch
            {
                // 初始化默认权限失败时忽略
            }
        }

        /// <summary>
        /// 根据角色获取默认权限数组
        ///   admin:   [true, true, true, true]
        ///   teacher: [false, true, true, true]
        ///   student: [false, false, false, false]
        /// </summary>
        /// <param name="role">角色</param>
        /// <returns>4 个锁的默认权限数组</returns>
        private static bool[] GetDefaultPermissionsByRole(string role)
        {
            bool[] result = new bool[LockCount];

            switch (role)
            {
                case "admin":
                    result[0] = true;
                    result[1] = true;
                    result[2] = true;
                    result[3] = true;
                    break;
                case "teacher":
                    // Lock0（系统锁）不可开，其余可开
                    result[0] = false;
                    result[1] = true;
                    result[2] = true;
                    result[3] = true;
                    break;
                case "student":
                default:
                    // 学生需老师单独分配
                    result[0] = false;
                    result[1] = false;
                    result[2] = false;
                    result[3] = false;
                    break;
            }

            return result;
        }
    }
}
