namespace CabinetLock
{
    /// <summary>
    /// 登录认证服务。账号和密码哈希从本机 business.db（users 表）读取，
    /// 启动时已从 SD 同步或以本地数据为准。
    /// 规则：
    /// 1) 用户表为空或系统管理员缺失时，允许 admin/admin123 初始化保留账户；
    /// 2) 系统管理员一旦存在，一律按保存的密码哈希严格校验。
    /// </summary>
    public class AuthService
    {
        public User? Login(string userId, string password)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(password)) return null;
            userId = userId.Trim();

            List<User> users;
            try
            {
                users = App.UserService.GetAllUsers();
            }
            catch (RootDataUnavailableException)
            {
                // 无 SD、也无本地缓存：仅允许内置管理员进入系统。
                var builtInAdministrator = CreateBuiltInAdministrator(userId, password);
                if (builtInAdministrator != null) return builtInAdministrator;
                throw;
            }

            // 空库或异常快照缺少保留管理员时，补建恢复账户。
            if (users.Count == 0 ||
                (!users.Any(SystemAdministratorPolicy.IsReserved) &&
                 IsBuiltInAdministratorCredentials(userId, password)))
                return BootstrapBuiltInAdministrator(userId, password);

            // 已有账户：严格按表验证。
            var user = users.FirstOrDefault(u =>
                string.Equals(u.DisplayId, userId, StringComparison.OrdinalIgnoreCase));
            // 学生是柜子业务用户，不允许登录上位机，也不需要维护密码。
            if (user == null || !user.Enabled ||
                string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase) ||
                !PasswordHelper.VerifyPassword(password, user.PasswordSalt, user.PasswordHash)) return null;

            // Upgrade legacy hashes without making a successful login depend
            // on the migration write succeeding.
            if (PasswordHelper.NeedsRehash(user.PasswordHash))
            {
                try
                {
                    // 登录阶段允许对本人做哈希升级（UserService 对本人/空会话放行）。
                    App.UserService.ResetPassword(user.UserId, password);
                }
                catch (RootDataUnavailableException)
                {
                    // The next successful login will retry the migration.
                }
            }
            return user;
        }

        public bool IsBuiltInAdministratorCredentials(string userId, string password) =>
            string.Equals(userId?.Trim(), SystemAdministratorPolicy.UserId,
                StringComparison.Ordinal) &&
            string.Equals(password, SystemAdministratorPolicy.InitialPassword,
                StringComparison.Ordinal);

        /// <summary>
        /// 用户表为空时：校验内置口令，写入 admin 账户后返回；
        /// 写入失败时仍返回内存中的内置管理员，保证空系统可首次进入。
        /// </summary>
        private User? BootstrapBuiltInAdministrator(string userId, string password)
        {
            if (!IsBuiltInAdministratorCredentials(userId, password)) return null;

            User admin = SystemAdministratorPolicy.CreateDefault();

            try
            {
                if (App.UserService.AddUser(admin,
                        SystemAdministratorPolicy.InitialPassword))
                {
                    return App.UserService.GetUserByCode(SystemAdministratorPolicy.UserId) ??
                        BuildEphemeralAdministrator();
                }

                // 可能是并发创建成功：再读一次并严格校验。
                var existing = App.UserService.GetUserByCode(
                    SystemAdministratorPolicy.UserId);
                if (existing != null && existing.Enabled &&
                    PasswordHelper.VerifyPassword(
                        password, existing.PasswordSalt, existing.PasswordHash))
                {
                    return existing;
                }
            }
            catch (RootDataUnavailableException)
            {
                // 写入不可用时退回内存账户。
            }
            catch (UnauthorizedAccessException)
            {
                // 登录阶段数据范围上下文可能尚未建立。
            }

            return BuildEphemeralAdministrator();
        }

        private User? CreateBuiltInAdministrator(string userId, string password)
        {
            if (!IsBuiltInAdministratorCredentials(userId, password)) return null;
            return BuildEphemeralAdministrator();
        }

        private static User BuildEphemeralAdministrator() =>
            SystemAdministratorPolicy.CreateDefault();

        public bool ChangePassword(string userId, string oldPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(oldPassword) ||
                !PasswordHelper.IsPasswordAcceptable(newPassword) ||
                string.Equals(oldPassword, newPassword, StringComparison.Ordinal)) return false;
            var user = App.UserService.GetUserByCode(userId);
            if (user == null || !PasswordHelper.VerifyPassword(
                    oldPassword, user.PasswordSalt, user.PasswordHash)) return false;
            return App.UserService.ResetPassword(user.UserId, newPassword);
        }
    }
}
