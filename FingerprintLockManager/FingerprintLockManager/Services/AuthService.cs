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
            if (user == null || !PasswordHelper.VerifyPassword(
                    password, user.PasswordSalt, user.PasswordHash)) return null;
            return user;
        }

        public bool ChangePassword(string userId, string oldPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(oldPassword) ||
                string.IsNullOrEmpty(newPassword)) return false;
            var user = App.UserService.GetUser(userId);
            if (user == null || !PasswordHelper.VerifyPassword(
                    oldPassword, user.PasswordSalt, user.PasswordHash)) return false;
            return App.UserService.ResetPassword(userId, newPassword);
        }
    }
}
