using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 用户模型（对应 users 表）
    /// 所有角色（admin/teacher/student）均可登录，均需密码
    /// </summary>
    public class User
    {
        /// <summary>用户唯一标识（主键，非自增）</summary>
        [JsonProperty("user_id")]
        public string UserId { get; set; } = "";

        /// <summary>用户姓名</summary>
        [JsonProperty("name")]
        public string Name { get; set; } = "";

        /// <summary>角色：admin / teacher / student</summary>
        [JsonProperty("role")]
        public string Role { get; set; } = "";

        /// <summary>所属班级 ID（可空）</summary>
        [JsonProperty("class_id")]
        public string? ClassId { get; set; }

        /// <summary>指纹模块中的 ID（唯一，可为空表示尚未录入指纹）</summary>
        [JsonProperty("fingerprint_id")]
        public int? FingerprintId { get; set; }

        /// <summary>密码盐值（随机16字节十六进制字符串，所有角色均需）</summary>
        [JsonProperty("password_salt")]
        public string PasswordSalt { get; set; } = "";

        /// <summary>登录密码哈希（带算法版本，所有角色均需）</summary>
        [JsonProperty("password_hash")]
        public string PasswordHash { get; set; } = "";

        /// <summary>停用后不能登录，也不会下发本地开锁权限。</summary>
        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public string StatusText => Enabled ? "启用" : "停用";

        /// <summary>创建时间</summary>
        [JsonProperty("create_time")]
        public DateTime CreateTime { get; set; }

        /// <summary>更新时间</summary>
        [JsonProperty("update_time")]
        public DateTime? UpdateTime { get; set; }
    }
}
