using System.Security.Cryptography;
using System.Text;

namespace FingerprintLockManager
{
    /// <summary>
    /// 密码工具类
    /// 使用 SHA256 对密码进行哈希存储与校验
    /// </summary>
    public static class PasswordHelper
    {
        /// <summary>
        /// 对明文密码进行 SHA256 哈希计算
        /// </summary>
        /// <param name="password">明文密码</param>
        /// <returns>64 位小写十六进制哈希字符串；输入为空时返回空字符串</returns>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;

            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(password);
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
        /// 校验明文密码与哈希值是否匹配
        /// </summary>
        /// <param name="input">用户输入的明文密码</param>
        /// <param name="hash">数据库中存储的哈希值</param>
        /// <returns>匹配返回 true；否则返回 false</returns>
        public static bool VerifyPassword(string input, string hash)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(hash)) return false;

            string inputHash = HashPassword(input);
            return string.Equals(inputHash, hash, StringComparison.OrdinalIgnoreCase);
        }
    }
}
