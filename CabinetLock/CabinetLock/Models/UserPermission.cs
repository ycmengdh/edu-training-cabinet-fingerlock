using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 用户个人权限覆盖模型（对应 user_permissions 表）
    /// 双层权限模型的第二层：个人动态覆盖项，优先级高于角色默认权限。
    /// 若某用户对某锁存在覆盖记录，则以覆盖值为准；否则回退到角色默认权限。
    /// </summary>
    public class UserPermission
    {
        /// <summary>权限 ID（自增主键）</summary>
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>所属用户 ID</summary>
        [JsonProperty("user_id")]
        public string UserId { get; set; } = "";

        /// <summary>锁编号：0=系统锁, 1=实训柜1, 2=实训柜2, 3=实训柜3</summary>
        [JsonProperty("lock_id")]
        public int LockId { get; set; }

        /// <summary>是否有访问权限（个人覆盖值）</summary>
        [JsonProperty("has_access")]
        public bool HasAccess { get; set; }

        /// <summary>更新时间</summary>
        [JsonProperty("update_time")]
        public DateTime UpdateTime { get; set; }
    }
}
