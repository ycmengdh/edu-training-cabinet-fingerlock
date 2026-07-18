namespace FingerprintLockManager
{
    /// <summary>
    /// 登录认证服务。账号和密码哈希从根节点 users.json 读取。
    /// </summary>
    public class AuthService
    {
        public User? Login(string userId, string password)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(password)) return null;
            var user = App.UserService.GetUser(userId);
            if (user == null || !user.Enabled || !PasswordHelper.VerifyPassword(
                    password, user.PasswordSalt, user.PasswordHash)) return null;

            // Upgrade legacy hashes without making a successful login depend
            // on the migration write succeeding.
            if (PasswordHelper.NeedsRehash(user.PasswordHash))
            {
                try
                {
                    App.UserService.ResetPassword(userId, password);
                }
                catch (RootDataUnavailableException)
                {
                    // The next successful login will retry the migration.
                }
            }
            return user;
        }

        public bool ChangePassword(string userId, string oldPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(oldPassword) ||
                !PasswordHelper.IsPasswordAcceptable(newPassword) ||
                string.Equals(oldPassword, newPassword, StringComparison.Ordinal)) return false;
            var user = App.UserService.GetUser(userId);
            if (user == null || !PasswordHelper.VerifyPassword(
                    oldPassword, user.PasswordSalt, user.PasswordHash)) return false;
            return App.UserService.ResetPassword(userId, newPassword);
        }
    }
}
