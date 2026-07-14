namespace FingerprintLockManager
{
    /// <summary>
    /// 权限管理服务（双层权限模型）
    /// 第一层：角色默认权限（由 RolePermissionService 管理）
    /// 第二层：个人权限覆盖项（user_permissions 表，优先级高于角色默认）
    /// 本服务负责个人覆盖项的增删查，以及指纹验证时合并两层得到最终权限。
    /// 数据持久化于根节点 SD 卡 user_permissions.json。
    /// </summary>
    public class PermissionService
    {
        /// <summary>锁总数（Lock0-3，共 4 把）</summary>
        private const int LockCount = 4;

        /// <summary>角色权限服务（用于合并两层权限）</summary>
        private readonly RolePermissionService _rolePermService = new RolePermissionService();

        /// <summary>获取用户的个人权限覆盖项</summary>
        public List<UserPermission> GetUserPermissions(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return new List<UserPermission>();
                return DataStore.Current.GetUserPermissions()
                    .Where(p => p.UserId == userId)
                    .OrderBy(p => p.LockId)
                    .ToList();
            }
            catch
            {
                return new List<UserPermission>();
            }
        }

        /// <summary>获取用户对某把锁的个人覆盖权限</summary>
        public (bool hasOverride, bool hasAccess) GetUserPermission(string userId, int lockId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return (false, false);
                var p = DataStore.Current.GetUserPermissions()
                    .FirstOrDefault(x => x.UserId == userId && x.LockId == lockId);
                if (p == null) return (false, false);
                return (true, p.HasAccess);
            }
            catch
            {
                return (false, false);
            }
        }

        /// <summary>获取用户最终权限（合并角色默认 + 个人覆盖）</summary>
        public bool[] GetFinalPermissions(string userId)
        {
            return _rolePermService.GetFinalPermissions(userId);
        }

        /// <summary>设置用户个人权限覆盖项（upsert）</summary>
        public bool SetUserPermission(string userId, int lockId, bool hasAccess)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;
                if (lockId < 0 || lockId >= LockCount) return false;

                bool ok = false;
                DataStore.Current.MutateUserPermissions(list =>
                {
                    int idx = list.FindIndex(p => p.UserId == userId && p.LockId == lockId);
                    if (idx >= 0)
                    {
                        list[idx].HasAccess = hasAccess;
                        list[idx].UpdateTime = DateTime.Now;
                    }
                    else
                    {
                        list.Add(new UserPermission
                        {
                            Id = DataStore.Current.NextUserPermissionId(),
                            UserId = userId,
                            LockId = lockId,
                            HasAccess = hasAccess,
                            UpdateTime = DateTime.Now
                        });
                    }
                    ok = true;
                });
                return ok;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>批量设置用户个人权限覆盖项</summary>
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

        /// <summary>删除用户对某把锁的个人覆盖项（回退到角色默认权限）</summary>
        public bool DeleteUserPermission(string userId, int lockId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;
                DataStore.Current.MutateUserPermissions(list =>
                {
                    list.RemoveAll(p => p.UserId == userId && p.LockId == lockId);
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>删除用户所有个人覆盖项（完全回退到角色默认权限）</summary>
        public bool DeleteAllUserPermissions(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;
                DataStore.Current.MutateUserPermissions(list =>
                {
                    list.RemoveAll(p => p.UserId == userId);
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>通过指纹验证用户并返回最终权限（合并双层权限）</summary>
        public (User user, bool[] permissions) VerifyByFingerprint(int fingerprintId)
        {
            bool[] empty = new bool[LockCount];
            try
            {
                var user = DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.FingerprintId == fingerprintId);

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
