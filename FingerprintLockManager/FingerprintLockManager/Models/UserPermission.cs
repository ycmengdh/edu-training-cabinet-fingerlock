namespace FingerprintLockManager
{
    /// <summary>
    /// 用户个人权限覆盖模型
    /// 双层权限模型的第二层：个人动态覆盖项，优先级高于角色默认权限。
    /// 数据持久化于根节点 SD 卡 user_permissions.json。
    /// </summary>
    public class UserPermission
    {
        /// <summary>权限 ID（内存自增，写入 SD 卡时保持）</summary>
        public int Id { get; set; }

        /// <summary>所属用户 ID</summary>
        public string UserId { get; set; }

        /// <summary>锁编号：0=系统锁, 1=实训柜1, 2=实训柜2, 3=实训柜3</summary>
        public int LockId { get; set; }

        /// <summary>是否有访问权限（个人覆盖值）</summary>
        public bool HasAccess { get; set; }

        /// <summary>更新时间</summary>
        public DateTime UpdateTime { get; set; }
    }
}
