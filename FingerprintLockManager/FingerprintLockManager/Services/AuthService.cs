namespace FingerprintLockManager
{
    /// <summary>
    /// 登录认证服务
    /// 负责用户登录验证与密码修改（加盐哈希）。
    /// 数据来源为 DataStore 内存副本（从根节点 SD 卡加载）。
    /// 需求 3：学生不能登录上位机后台，仅 admin/teacher 可登录。
    /// </summary>
    public class AuthService
    {
        /// <summary>最近一次登录失败的错误原因（供 UI 显示具体提示）</summary>
        public string? LastLoginError { get; private set; }

        /// <summary>用户登录验证（需求 3：拒绝学生登录）</summary>
        public User? Login(string userId, string password)
        {
            try
            {
                LastLoginError = null;
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password))
                {
                    LastLoginError = "用户ID和密码不能为空";
                    return null;
                }

                var user = DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.UserId == userId);

                if (user == null)
                {
                    LastLoginError = "用户不存在";
                    return null;
                }

                // 需求 3：学生不能登录上位机
                if (user.Role == "student")
                {
                    LastLoginError = "学生账号无权登录上位机后台";
                    return null;
                }

                if (!PasswordHelper.VerifyPassword(password, user.PasswordSalt, user.PasswordHash))
                {
                    LastLoginError = "密码错误";
                    return null;
                }

                return user;
            }
            catch
            {
                LastLoginError = "登录异常";
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
