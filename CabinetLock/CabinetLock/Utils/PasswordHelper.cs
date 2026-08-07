using System.Security.Cryptography;

namespace CabinetLock
{
    /// <summary>
    /// 密码工具类
    /// 新密码使用 PBKDF2-SHA256；仍可验证旧版 SHA256(password + salt)，
    /// 登录成功后由 AuthService 自动迁移旧哈希。
    /// </summary>
    public static class PasswordHelper
    {
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const int Pbkdf2Iterations = 210_000;
        public const int MinimumPasswordLength = 6;
        public const int MaximumPasswordLength = 128;
        private const string Pbkdf2Prefix = "pbkdf2-sha256";

        /// <summary>
        /// 生成随机盐值（16 字节十六进制字符串）
        /// </summary>
        /// <returns>32 位十六进制盐值字符串</returns>
        public static string GenerateSalt()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(SaltBytes)).ToLowerInvariant();
        }

        /// <summary>
        /// 生成带算法与迭代次数标识的 PBKDF2-SHA256 哈希。
        /// </summary>
        /// <param name="password">明文密码</param>
        /// <param name="salt">盐值（GenerateSalt 产生的十六进制字符串）</param>
        /// <returns>64 位小写十六进制哈希字符串；输入为空时返回空字符串</returns>
        public static string HashPassword(string password, string salt)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            if (!TryDecodeSalt(salt, out byte[] saltBytes)) return string.Empty;

            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                password, saltBytes, Pbkdf2Iterations,
                HashAlgorithmName.SHA256, HashBytes);
            return $"{Pbkdf2Prefix}${Pbkdf2Iterations}${Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// 校验明文密码与盐值、哈希值是否匹配
        /// </summary>
        /// <param name="input">用户输入的明文密码</param>
        /// <param name="salt">盐值</param>
        /// <param name="hash">数据库中存储的哈希值</param>
        /// <returns>匹配返回 true；否则返回 false</returns>
        public static bool VerifyPassword(string input, string salt, string hash)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(hash)) return false;
            if (!TryDecodeSalt(salt, out byte[] saltBytes)) return false;

            if (hash.StartsWith(Pbkdf2Prefix + "$", StringComparison.Ordinal))
            {
                string[] parts = hash.Split('$');
                if (parts.Length != 3 || !int.TryParse(parts[1], out int iterations) ||
                    iterations < 100_000 || iterations > 2_000_000)
                {
                    return false;
                }

                byte[] expected;
                try
                {
                    expected = Convert.FromBase64String(parts[2]);
                }
                catch (FormatException)
                {
                    return false;
                }
                if (expected.Length != HashBytes) return false;

                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                    input, saltBytes, iterations,
                    HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }

            // Legacy v1 format: lowercase SHA256(password + hex salt).
            byte[] legacyExpected;
            try
            {
                legacyExpected = Convert.FromHexString(hash);
            }
            catch (FormatException)
            {
                return false;
            }
            byte[] legacyActual = SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(input + salt));
            return legacyExpected.Length == legacyActual.Length &&
                   CryptographicOperations.FixedTimeEquals(legacyActual, legacyExpected);
        }

        public static bool NeedsRehash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return true;
            string[] parts = hash.Split('$');
            return parts.Length != 3 || parts[0] != Pbkdf2Prefix ||
                   !int.TryParse(parts[1], out int iterations) ||
                   iterations < Pbkdf2Iterations;
        }

        public static bool IsPasswordAcceptable(string? password) =>
            !string.IsNullOrWhiteSpace(password) &&
            password.Length >= MinimumPasswordLength &&
            password.Length <= MaximumPasswordLength;

        public static string PasswordRequirement =>
            $"密码长度需要为 {MinimumPasswordLength}-{MaximumPasswordLength} 个字符";

        private static bool TryDecodeSalt(string? salt, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(salt)) return false;
            try
            {
                bytes = Convert.FromHexString(salt);
                return bytes.Length >= SaltBytes;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
