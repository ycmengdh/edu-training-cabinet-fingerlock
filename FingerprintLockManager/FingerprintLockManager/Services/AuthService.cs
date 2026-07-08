namespace FingerprintLockManager
{
    /// <summary>
    /// 登录认证服务
    /// 负责管理员登录验证与密码修改
    /// </summary>
    public class AuthService
    {
        /// <summary>
        /// 管理员登录验证
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="password">明文密码</param>
        /// <returns>验证通过返回 User 对象；失败或异常返回 null</returns>
        public User Login(string userId, string password)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password)) return null;

                // 根据用户 ID 查询用户
                var user = DatabaseService.Fsql.Select<User>()
                    .Where(u => u.UserId == userId)
                    .First();

                if (user == null) return null;

                // 校验密码
                if (!PasswordHelper.VerifyPassword(password, user.PasswordHash))
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

        /// <summary>
        /// 修改密码
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="oldPassword">原明文密码</param>
        /// <param name="newPassword">新明文密码</param>
        /// <returns>修改成功返回 true；原密码错误或异常返回 false</returns>
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

                // 查询用户
                var user = DatabaseService.Fsql.Select<User>()
                    .Where(u => u.UserId == userId)
                    .First();

                if (user == null) return false;

                // 校验原密码
                if (!PasswordHelper.VerifyPassword(oldPassword, user.PasswordHash))
                {
                    return false;
                }

                // 更新密码哈希与更新时间
                user.PasswordHash = PasswordHelper.HashPassword(newPassword);
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
    }
}
