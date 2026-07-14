namespace FingerprintLockManager
{
    /// <summary>
    /// 权限管理服务（双层权限模型）
    /// 第一层：角色默认权限（由 RolePermissionService 管理）
    /// 第二层：个人权限覆盖项（UserPermission 表，优先级高于角色默认）
    /// 本服务负责个人覆盖项的增删查，以及指纹验证时合并两层得到最终权限。
    /// </summary>
    public class PermissionService
    {
        /// <summary>锁总数（Lock0-3，共 4 把）</summary>
        private const int LockCount = 4;

        /// <summary>角色权限服务（用于合并两层权限）</summary>
        private readonly RolePermissionService _rolePermService = new RolePermissionService();

        /// <summary>
        /// 获取用户的个人权限覆盖项
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <returns>个人覆盖项列表；异常时返回空列表</returns>
        public List<UserPermission> GetUserPermissions(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return new List<UserPermission>();

                return DatabaseService.Fsql.Select<UserPermission>()
                    .Where(p => p.UserId == userId)
                    .OrderBy(p => p.LockId)
                    .ToList();
            }
            catch
            {
                return new List<UserPermission>();
            }
        }

        /// <summary>
        /// 获取用户对某把锁的个人覆盖权限
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="lockId">锁编号 0-3</param>
        /// <returns>存在覆盖返回 (true, HasAccess)；无覆盖返回 (false, false)</returns>
        public (bool hasOverride, bool hasAccess) GetUserPermission(string userId, int lockId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return (false, false);

                var p = DatabaseService.Fsql.Select<UserPermission>()
                    .Where(x => x.UserId == userId && x.LockId == lockId)
                    .First();
                if (p == null) return (false, false);
                return (true, p.HasAccess);
            }
            catch
            {
                return (false, false);
            }
        }

        /// <summary>
        /// 获取用户最终权限（合并角色默认 + 个人覆盖）
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <returns>4 把锁的最终权限 bool[4]</returns>
        public bool[] GetFinalPermissions(string userId)
        {
            return _rolePermService.GetFinalPermissions(userId);
        }

        /// <summary>
        /// 设置用户个人权限覆盖项（upsert）
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="lockId">锁编号 0-3</param>
        /// <param name="hasAccess">是否有访问权限</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool SetUserPermission(string userId, int lockId, bool hasAccess)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;
                if (lockId < 0 || lockId >= LockCount) return false;

                var existing = DatabaseService.Fsql.Select<UserPermission>()
                    .Where(p => p.UserId == userId && p.LockId == lockId)
                    .First();

                if (existing != null)
                {
                    existing.HasAccess = hasAccess;
                    existing.UpdateTime = DateTime.Now;
                    int rows = DatabaseService.Fsql.Update<UserPermission>()
                        .SetSource(existing)
                        .ExecuteAffrows();
                    return rows > 0;
                }
                else
                {
                    var perm = new UserPermission
                    {
                        UserId = userId,
                        LockId = lockId,
                        HasAccess = hasAccess,
                        UpdateTime = DateTime.Now
                    };
                    int rows = DatabaseService.Fsql.Insert(perm).ExecuteAffrows();
                    return rows > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 批量设置用户个人权限覆盖项
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="permissions">锁编号与权限的字典</param>
        /// <returns>全部成功返回 true；任意一项失败或异常返回 false</returns>
        public bool SetUserPermissions(string userId, Dictionary<int, bool> permissions)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || permissions == null) return false;

                foreach (var kv in permissions)
                {
                    if (!SetUserPermission(userId, kv.Key, kv.Value))
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
        /// 删除用户对某把锁的个人覆盖项（回退到角色默认权限）
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="lockId">锁编号 0-3</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool DeleteUserPermission(string userId, int lockId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;
                int rows = DatabaseService.Fsql.Delete<UserPermission>()
                    .Where(p => p.UserId == userId && p.LockId == lockId)
                    .ExecuteAffrows();
                return rows >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 删除用户所有个人覆盖项（完全回退到角色默认权限）
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool DeleteAllUserPermissions(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;
                DatabaseService.Fsql.Delete<UserPermission>()
                    .Where(p => p.UserId == userId)
                    .ExecuteAffrows();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 通过指纹验证用户并返回最终权限（合并双层权限）
        /// </summary>
        /// <param name="fingerprintId">指纹 ID</param>
        /// <returns>用户对象与 4 把锁的最终权限数组；指纹未注册返回 (null, 全 false)</returns>
        public (User user, bool[] permissions) VerifyByFingerprint(int fingerprintId)
        {
            bool[] empty = new bool[LockCount];
            try
            {
                var user = DatabaseService.Fsql.Select<User>()
                    .Where(u => u.FingerprintId == fingerprintId)
                    .First();

                if (user == null) return (null, empty);

                bool[] permissions = _rolePermService.GetFinalPermissions(user.UserId);
                return (user, permissions);
            }
            catch
            {
                return (null, empty);
            }
        }
    }
}
