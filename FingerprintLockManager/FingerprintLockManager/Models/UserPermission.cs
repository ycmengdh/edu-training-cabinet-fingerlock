using FreeSql.DataAnnotations;

namespace FingerprintLockManager
{
    /// <summary>
    /// 用户个人权限覆盖模型（对应 user_permissions 表）
    /// 双层权限模型的第二层：个人动态覆盖项，优先级高于角色默认权限。
    /// 若某用户对某锁存在覆盖记录，则以覆盖值为准；否则回退到角色默认权限。
    /// </summary>
    [Table(Name = "user_permissions")]
    [Index("idx_user_lock", "UserId,LockId", true)]
    public class UserPermission
    {
        /// <summary>权限 ID（自增主键）</summary>
        [Column(IsPrimary = true, IsIdentity = true)]
        public int Id { get; set; }

        /// <summary>所属用户 ID</summary>
        [Column(IsNullable = false)]
        public string UserId { get; set; }

        /// <summary>锁编号：0=系统锁, 1=实训柜1, 2=实训柜2, 3=实训柜3</summary>
        [Column(IsNullable = false)]
        public int LockId { get; set; }

        /// <summary>是否有访问权限（个人覆盖值）</summary>
        [Column(IsNullable = false)]
        public bool HasAccess { get; set; }

        /// <summary>更新时间</summary>
        [Column(IsNullable = false)]
        public DateTime UpdateTime { get; set; }
    }
}
