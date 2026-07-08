using FreeSql.DataAnnotations;

namespace FingerprintLockManager
{
    /// <summary>
    /// 权限模型（对应 permissions 表）
    /// 描述某个用户对某个锁（0-3）的访问权限
    /// </summary>
    public class Permission
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

        /// <summary>是否有访问权限</summary>
        [Column(IsNullable = false)]
        public bool HasAccess { get; set; }

        /// <summary>更新时间</summary>
        [Column(IsNullable = false)]
        public DateTime UpdateTime { get; set; }
    }
}
