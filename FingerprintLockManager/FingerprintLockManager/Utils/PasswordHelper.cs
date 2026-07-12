using System.Security.Cryptography;
using System.Text;

namespace FingerprintLockManager
{
    /// <summary>
    /// 密码工具类
    /// 使用 SHA256 + 随机盐值对密码进行加盐哈希存储与校验，抵御彩虹表攻击。
    /// 哈希算法：SHA256(password + salt)
    /// </summary>
    public static class PasswordHelper
    {
        /// <summary>盐值字节数</summary>
        private const int SaltBytes = 16;

        /// <summary>
        /// 生成随机盐值（16 字节十六进制字符串）
        /// </summary>
        /// <returns>32 位十六进制盐值字符串</returns>
        public static string GenerateSalt()
        {
            byte[] salt = new byte[SaltBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            var sb = new StringBuilder(salt.Length * 2);
            foreach (byte b in salt)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 对明文密码进行加盐 SHA256 哈希计算
        /// 算法：SHA256(password + salt)
        /// </summary>
        /// <param name="password">明文密码</param>
        /// <param name="salt">盐值（GenerateSalt 产生的十六进制字符串）</param>
        /// <returns>64 位小写十六进制哈希字符串；输入为空时返回空字符串</returns>
        public static string HashPassword(string password, string salt)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            salt = salt ?? string.Empty;

            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(password + salt);
                byte[] hash = sha256.ComputeHash(bytes);

                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
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

            string inputHash = HashPassword(input, salt);
            return string.Equals(inputHash, hash, StringComparison.OrdinalIgnoreCase);
        }
    }
}
