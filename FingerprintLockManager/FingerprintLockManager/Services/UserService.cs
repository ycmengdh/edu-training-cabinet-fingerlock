namespace FingerprintLockManager
{
    /// <summary>
    /// 用户服务。所有读写都通过根节点 SD 的 users.json 完成。
    /// </summary>
    public class UserService
    {
        private readonly RootDataService _root = new RootDataService();

        public List<User> GetAllUsers()
        {
            return _root.Read<User>("users")
                .OrderBy(u => u.Role).ThenBy(u => u.UserId).ToList();
        }

        public List<User> GetUsersByRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return new List<User>();
            return GetAllUsers().Where(u => u.Role == role).ToList();
        }

        public User? GetUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            return _root.Read<User>("users")
                .FirstOrDefault(u => u.UserId == userId);
        }

        public User? GetUserByFingerprint(int fingerprintId)
        {
            return _root.Read<User>("users")
                .FirstOrDefault(u => u.FingerprintId == fingerprintId);
        }

        public bool AddUser(User user, string password)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId) ||
                string.IsNullOrEmpty(password)) return false;

            var users = _root.Read<User>("users");
            if (users.Any(u => u.UserId == user.UserId)) return false;

            string salt = PasswordHelper.GenerateSalt();
            user.PasswordSalt = salt;
            user.PasswordHash = PasswordHelper.HashPassword(password, salt);
            user.CreateTime = user.CreateTime == default ? DateTime.Now : user.CreateTime;
            user.UpdateTime = DateTime.Now;
            users.Add(user);
            return _root.Save("users", users);
        }

        public bool AddUser(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId)) return false;
            var users = _root.Read<User>("users");
            if (users.Any(u => u.UserId == user.UserId)) return false;
            if (string.IsNullOrEmpty(user.PasswordSalt)) user.PasswordSalt = PasswordHelper.GenerateSalt();
            user.PasswordHash ??= "";
            user.CreateTime = user.CreateTime == default ? DateTime.Now : user.CreateTime;
            user.UpdateTime = DateTime.Now;
            users.Add(user);
            return _root.Save("users", users);
        }

        public bool UpdateUser(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId)) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.UserId == user.UserId);
            if (existing == null) return false;

            user.UpdateTime = DateTime.Now;
            int index = users.IndexOf(existing);
            users[index] = user;
            return _root.Save("users", users);
        }

        public bool DeleteUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var users = _root.Read<User>("users");
            int removed = users.RemoveAll(u => u.UserId == userId);
            if (removed == 0) return false;
            if (!_root.Save("users", users)) return false;

            var permissions = _root.Read<UserPermission>("permissions");
            permissions.RemoveAll(p => p.UserId == userId);
            _root.Save("permissions", permissions);
            return true;
        }

        public bool AssignFingerprint(string userId, int fingerprintId)
        {
            if (string.IsNullOrWhiteSpace(userId) || fingerprintId <= 0) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.FingerprintId == fingerprintId && u.UserId != userId);
            if (existing != null) return false;

            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return false;
            user.FingerprintId = fingerprintId;
            user.UpdateTime = DateTime.Now;
            return _root.Save("users", users);
        }

        public bool ResetPassword(string userId, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(newPassword)) return false;
            var users = _root.Read<User>("users");
            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return false;

            user.PasswordSalt = PasswordHelper.GenerateSalt();
            user.PasswordHash = PasswordHelper.HashPassword(newPassword, user.PasswordSalt);
            user.UpdateTime = DateTime.Now;
            return _root.Save("users", users);
        }

        public int GetNextFingerprintId()
        {
            return _root.Read<User>("users")
                .Where(u => u.FingerprintId.HasValue)
                .Select(u => u.FingerprintId!.Value)
                .DefaultIfEmpty(0)
                .Max() + 1;
        }
    }
}
