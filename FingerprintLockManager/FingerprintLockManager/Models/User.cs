namespace FingerprintLockManager
{
    /// <summary>
    /// 用户模型
    /// 所有角色（admin/teacher/student）均可登录，均需密码。
    /// 数据持久化于根节点 SD 卡 users.json。
    /// </summary>
    public class User
    {
        /// <summary>用户唯一标识</summary>
        public string UserId { get; set; }

        /// <summary>用户姓名</summary>
        public string Name { get; set; }

        /// <summary>角色：admin / teacher / student</summary>
        public string Role { get; set; }

        /// <summary>指纹模块中的 ID（可为空表示尚未录入指纹）</summary>
        public int? FingerprintId { get; set; }

        /// <summary>密码盐值（随机16字节十六进制字符串）</summary>
        public string PasswordSalt { get; set; }

        /// <summary>登录密码哈希（SHA256(password+salt)）</summary>
        public string PasswordHash { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreateTime { get; set; }

        /// <summary>更新时间</summary>
        public DateTime? UpdateTime { get; set; }
    }
}
