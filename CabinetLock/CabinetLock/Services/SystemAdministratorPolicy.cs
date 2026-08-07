namespace CabinetLock
{
    /// <summary>
    /// The built-in administrator is a reserved recovery account. Its login
    /// identity and role are fixed, while its profile and credentials remain editable.
    /// </summary>
    public static class SystemAdministratorPolicy
    {
        public const string UserId = "admin";
        public const string InitialPassword = "admin123";
        public const string DisplayName = "超级管理员";

        public static bool IsReservedId(string? userId) =>
            string.Equals(userId?.Trim(), UserId, StringComparison.OrdinalIgnoreCase);

        public static bool IsReserved(User? user) =>
            user != null && (IsReservedId(user.UserId) ||
                             IsReservedId(user.UserCode));

        public static User CreateDefault(DateTime? timestamp = null)
        {
            DateTime now = timestamp ?? DateTime.Now;
            var user = new User
            {
                UserId = UserId,
                UserCode = UserId,
                Name = DisplayName,
                Role = "admin",
                Enabled = true,
                CreateTime = now,
                UpdateTime = now
            };
            Normalize(user);
            return user;
        }

        public static void Normalize(User user)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            user.UserId = UserId;
            user.UserCode = UserId;
            if (string.IsNullOrWhiteSpace(user.Name)) user.Name = DisplayName;
            user.Role = "admin";
            if (user.CreateTime == default) user.CreateTime = DateTime.Now;

            if (string.IsNullOrWhiteSpace(user.PasswordSalt) ||
                string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                user.PasswordSalt = PasswordHelper.GenerateSalt();
                user.PasswordHash = PasswordHelper.HashPassword(
                    InitialPassword, user.PasswordSalt);
            }
        }
    }
}
