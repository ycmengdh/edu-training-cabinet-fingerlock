namespace FingerprintLockManager
{
    /// <summary>
    /// 用户管理服务
    /// 提供用户的增删改查、指纹 ID 分配、密码加盐哈希等功能。
    /// 所有角色（admin/teacher/student）均需密码登录，AddUser 时自动生成盐值。
    /// </summary>
    public class UserService
    {
        /// <summary>
        /// 获取所有用户
        /// </summary>
        /// <returns>用户列表；异常时返回空列表</returns>
        public List<User> GetAllUsers()
        {
            try
            {
                return DatabaseService.Fsql.Select<User>()
                    .OrderBy(u => u.Role)
                    .OrderBy(u => u.UserId)
                    .ToList();
            }
            catch
            {
                return new List<User>();
            }
        }

        /// <summary>
        /// 按角色筛选用户
        /// </summary>
        /// <param name="role">角色：admin / teacher / student</param>
        /// <returns>符合条件的用户列表；异常时返回空列表</returns>
        public List<User> GetUsersByRole(string role)
        {
            try
            {
                if (string.IsNullOrEmpty(role)) return new List<User>();

                return DatabaseService.Fsql.Select<User>()
                    .Where(u => u.Role == role)
                    .OrderBy(u => u.UserId)
                    .ToList();
            }
            catch
            {
                return new List<User>();
            }
        }

        /// <summary>
        /// 获取单个用户
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <returns>用户对象；不存在或异常返回 null</returns>
        public User? GetUser(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return null;

                return DatabaseService.Fsql.Select<User>()
                    .Where(u => u.UserId == userId)
                    .First();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 根据指纹 ID 获取用户
        /// </summary>
        /// <param name="fingerprintId">指纹模块中的 ID</param>
        /// <returns>用户对象；不存在或异常返回 null</returns>
        public User? GetUserByFingerprint(int fingerprintId)
        {
            try
            {
                return DatabaseService.Fsql.Select<User>()
                    .Where(u => u.FingerprintId == fingerprintId)
                    .First();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 添加用户（自动生成盐值并对明文密码加盐哈希）
        /// 所有角色均需密码。
        /// </summary>
        /// <param name="user">待添加的用户对象</param>
        /// <param name="password">明文密码</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool AddUser(User user, string password)
        {
            try
            {
                if (user == null || string.IsNullOrEmpty(user.UserId)) return false;
                if (string.IsNullOrEmpty(password)) return false;

                // 生成盐值并加盐哈希
                string salt = PasswordHelper.GenerateSalt();
                user.PasswordSalt = salt;
                user.PasswordHash = PasswordHelper.HashPassword(password, salt);

                // 设置创建时间
                if (user.CreateTime == default(DateTime))
                {
                    user.CreateTime = DateTime.Now;
                }
                user.UpdateTime = DateTime.Now;

                int rows = DatabaseService.Fsql.Insert(user).ExecuteAffrows();
                return rows > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 添加用户（盐值与哈希已设置好的场景，如数据迁移）
        /// 若盐值为空则自动生成，密码哈希为空时设为空字符串。
        /// </summary>
        /// <param name="user">待添加的用户对象</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool AddUser(User user)
        {
            try
            {
                if (user == null || string.IsNullOrEmpty(user.UserId)) return false;

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

                int rows = DatabaseService.Fsql.Insert(user).ExecuteAffrows();
                return rows > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 更新用户
        /// </summary>
        /// <param name="user">待更新的用户对象（按主键 UserId 更新）</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool UpdateUser(User user)
        {
            try
            {
                if (user == null || string.IsNullOrEmpty(user.UserId)) return false;

                user.UpdateTime = DateTime.Now;

                int rows = DatabaseService.Fsql.Update<User>()
                    .SetSource(user)
                    .ExecuteAffrows();

                return rows > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 删除用户（同时删除其个人权限覆盖记录）
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool DeleteUser(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;

                // 先删除个人权限覆盖记录，再删除用户
                DatabaseService.Fsql.Delete<UserPermission>()
                    .Where(p => p.UserId == userId)
                    .ExecuteAffrows();

                int rows = DatabaseService.Fsql.Delete<User>()
                    .Where(u => u.UserId == userId)
                    .ExecuteAffrows();

                return rows > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 分配指纹 ID 给指定用户
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="fingerprintId">指纹模块中的 ID</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool AssignFingerprint(string userId, int fingerprintId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return false;

                int rows = DatabaseService.Fsql.Update<User>()
                    .Set(u => u.FingerprintId, fingerprintId)
                    .Set(u => u.UpdateTime, DateTime.Now)
                    .Where(u => u.UserId == userId)
                    .ExecuteAffrows();

                return rows > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 重置用户密码（生成新盐值并重新哈希）
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="newPassword">新明文密码</param>
        /// <returns>成功返回 true；失败或异常返回 false</returns>
        public bool ResetPassword(string userId, string newPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(newPassword)) return false;

                string salt = PasswordHelper.GenerateSalt();
                string hash = PasswordHelper.HashPassword(newPassword, salt);

                int rows = DatabaseService.Fsql.Update<User>()
                    .Set(u => u.PasswordSalt, salt)
                    .Set(u => u.PasswordHash, hash)
                    .Set(u => u.UpdateTime, DateTime.Now)
                    .Where(u => u.UserId == userId)
                    .ExecuteAffrows();

                return rows > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取下一个可用的指纹 ID
        /// 指纹 ID 从 1 开始递增，跳过已占用的 ID
        /// </summary>
        /// <returns>下一个可用指纹 ID；异常时返回 1</returns>
        public int GetNextFingerprintId()
        {
            try
            {
                // 查询当前已使用的最大指纹 ID
                var maxId = DatabaseService.Fsql.Select<User>()
                    .Where(u => u.FingerprintId != null)
                    .Max(u => u.FingerprintId);

                // 无记录时从 1 开始，否则取最大值 + 1
                if (maxId == null || maxId <= 0) return 1;

                return maxId.Value + 1;
            }
            catch
            {
                return 1;
            }
        }
    }
}
