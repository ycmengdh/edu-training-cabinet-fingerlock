namespace FingerprintLockManager
{
    /// <summary>
    /// 用户管理服务
    /// 提供用户的增删改查、指纹 ID 分配、密码加盐哈希等功能。
    /// 数据持久化于根节点 SD 卡 users.json，通过 DataStore 内存副本操作。
    /// </summary>
    public class UserService
    {
        /// <summary>获取所有用户</summary>
        public List<User> GetAllUsers()
        {
            try
            {
                return DataStore.Current.GetUsers()
                    .OrderBy(u => u.Role)
                    .ThenBy(u => u.UserId)
                    .ToList();
            }
            catch
            {
                return new List<User>();
            }
        }

        /// <summary>按角色筛选用户</summary>
        public List<User> GetUsersByRole(string role)
        {
            try
            {
                if (string.IsNullOrEmpty(role)) return new List<User>();
                return DataStore.Current.GetUsers()
                    .Where(u => u.Role == role)
                    .OrderBy(u => u.UserId)
                    .ToList();
            }
            catch
            {
                return new List<User>();
            }
        }

        /// <summary>获取单个用户</summary>
        public User? GetUser(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return null;
                return DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.UserId == userId);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>根据指纹 ID 获取用户</summary>
        public User? GetUserByFingerprint(int fingerprintId)
        {
            try
            {
                return DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.FingerprintId == fingerprintId);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 添加用户（自动生成盐值并对明文密码加盐哈希）
        /// </summary>
        public bool AddUser(User user, string password)
        {
            try
            {
                if (user == null || string.IsNullOrEmpty(user.UserId)) return false;
                if (string.IsNullOrEmpty(password)) return false;

                // 检查用户 ID 是否已存在
                if (DataStore.Current.GetUsers().Any(u => u.UserId == user.UserId))
                    return false;

                // 生成盐值并加盐哈希
                string salt = PasswordHelper.GenerateSalt();
                user.PasswordSalt = salt;
                user.PasswordHash = PasswordHelper.HashPassword(password, salt);

                if (user.CreateTime == default(DateTime))
                {
                    user.CreateTime = DateTime.Now;
                }
                user.UpdateTime = DateTime.Now;

                DataStore.Current.MutateUsers(list => list.Add(user));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>添加用户（盐值与哈希已设置好的场景）</summary>
        public bool AddUser(User user)
        {
            try
            {
                if (user == null || string.IsNullOrEmpty(user.UserId)) return false;

                if (DataStore.Current.GetUsers().Any(u => u.UserId == user.UserId))
                    return false;

                if (string.IsNullOrEmpty(user.PasswordSalt))
                {
                    user.PasswordSalt = PasswordHelper.GenerateSalt();
                }
                if (user.PasswordHash == null) user.PasswordHash = "";

                if (user.CreateTime == default(DateTime))
                {
                    user.CreateTime = DateTime.Now;
                }
                user.UpdateTime = DateTime.Now;

                DataStore.Current.MutateUsers(list => list.Add(user));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>更新用户</summary>
        public bool UpdateUser(User user)
        {
            try
            {
                if (user == null || string.IsNullOrEmpty(user.UserId)) return false;

                user.UpdateTime = DateTime.Now;

                bool found = false;
                DataStore.Current.MutateUsers(list =>
                {
                    int idx = list.FindIndex(u => u.UserId == user.UserId);
                    if (idx >= 0)
                    {
                        list[idx] = user;
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

        /// <summary>删除用户（同时删除其个人权限覆盖记录）</summary>
        public bool DeleteUser(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;

                bool removed = false;
                DataStore.Current.MutateUsers(list =>
                {
                    removed = list.RemoveAll(u => u.UserId == userId) > 0;
                });

                // 同步删除个人权限覆盖记录
                DataStore.Current.MutateUserPermissions(list =>
                {
                    list.RemoveAll(p => p.UserId == userId);
                });

                return removed;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>分配指纹 ID 给指定用户</summary>
        public bool AssignFingerprint(string userId, int fingerprintId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;

                bool found = false;
                DataStore.Current.MutateUsers(list =>
                {
                    int idx = list.FindIndex(u => u.UserId == userId);
                    if (idx >= 0)
                    {
                        list[idx].FingerprintId = fingerprintId;
                        list[idx].UpdateTime = DateTime.Now;
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

        /// <summary>重置用户密码</summary>
        public bool ResetPassword(string userId, string newPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(newPassword)) return false;

                string salt = PasswordHelper.GenerateSalt();
                string hash = PasswordHelper.HashPassword(newPassword, salt);

                bool found = false;
                DataStore.Current.MutateUsers(list =>
                {
                    int idx = list.FindIndex(u => u.UserId == userId);
                    if (idx >= 0)
                    {
                        list[idx].PasswordSalt = salt;
                        list[idx].PasswordHash = hash;
                        list[idx].UpdateTime = DateTime.Now;
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

        /// <summary>获取下一个可用的指纹 ID（跳过已占用）</summary>
        public int GetNextFingerprintId()
        {
            try
            {
                var maxId = DataStore.Current.GetUsers()
                    .Where(u => u.FingerprintId != null)
                    .Select(u => u.FingerprintId!.Value)
                    .DefaultIfEmpty(0)
                    .Max();

                return maxId <= 0 ? 1 : maxId + 1;
            }
            catch
            {
                return 1;
            }
        }
    }
}
