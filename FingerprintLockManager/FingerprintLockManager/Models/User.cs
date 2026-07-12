using FreeSql.DataAnnotations;

namespace FingerprintLockManager
{
    /// <summary>
    /// 用户模型（对应 users 表）
    /// 所有角色（admin/teacher/student）均可登录，均需密码
    /// </summary>
    public class User
    {
        /// <summary>用户唯一标识（主键，非自增）</summary>
        [Column(IsPrimary = true, IsIdentity = false)]
        public string UserId { get; set; }

        /// <summary>用户姓名</summary>
        [Column(IsNullable = false)]
        public string Name { get; set; }

        /// <summary>角色：admin / teacher / student</summary>
        [Column(IsNullable = false)]
        public string Role { get; set; }

        /// <summary>指纹模块中的 ID（唯一，可为空表示尚未录入指纹）</summary>
        [Column(IsNullable = true)]
        public int? FingerprintId { get; set; }

        /// <summary>密码盐值（随机16字节十六进制字符串，所有角色均需）</summary>
        [Column(IsNullable = false)]
        public string PasswordSalt { get; set; }

        /// <summary>登录密码哈希（SHA256(password+salt)，所有角色均需）</summary>
        [Column(IsNullable = false)]
        public string PasswordHash { get; set; }

        /// <summary>创建时间</summary>
        [Column(IsNullable = false)]
        public DateTime CreateTime { get; set; }

        /// <summary>更新时间</summary>
        [Column(IsNullable = true)]
        public DateTime? UpdateTime { get; set; }
    }
}
