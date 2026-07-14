namespace FingerprintLockManager
{
    /// <summary>
    /// 登录认证服务
    /// 负责用户登录验证与密码修改（加盐哈希）。
    /// 数据来源为 DataStore 内存副本（从根节点 SD 卡加载）。
    /// </summary>
    public class AuthService
    {
        /// <summary>用户登录验证</summary>
        public User Login(string userId, string password)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password)) return null;

                var user = DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.UserId == userId);

                if (user == null) return null;

                if (!PasswordHelper.VerifyPassword(password, user.PasswordSalt, user.PasswordHash))
                {
                    return null;
                }

                return user;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>修改密码</summary>
        public bool ChangePassword(string userId, string oldPassword, string newPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) ||
                    string.IsNullOrEmpty(oldPassword) ||
                    string.IsNullOrEmpty(newPassword))
                {
                    return false;
                }

                var user = DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.UserId == userId);

                if (user == null) return false;

                if (!PasswordHelper.VerifyPassword(oldPassword, user.PasswordSalt, user.PasswordHash))
                {
                    return false;
                }

                string newSalt = PasswordHelper.GenerateSalt();
                string newHash = PasswordHelper.HashPassword(newPassword, newSalt);

                bool found = false;
                DataStore.Current.MutateUsers(list =>
                {
                    int idx = list.FindIndex(u => u.UserId == userId);
                    if (idx >= 0)
                    {
                        list[idx].PasswordSalt = newSalt;
                        list[idx].PasswordHash = newHash;
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
    }
}
