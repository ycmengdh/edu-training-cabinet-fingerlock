using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 角色默认权限模型（对应 role_permissions 表）
    /// 描述某个角色对 4 把锁的默认访问权限。字段保持 0-based，界面显示为 Lock1-4。
    /// 默认值：admin=[T,T,T,T]，teacher=[F,T,T,T]，student=[F,F,F,F]
    /// </summary>
    public class RolePermission
    {
        /// <summary>角色名（主键，非自增）：admin / teacher / student</summary>
        [JsonProperty("role")]
        public string Role { get; set; } = "";

        /// <summary>内部索引 0（界面 Lock1，系统锁）默认权限</summary>
        [JsonProperty("lock_0")]
        public bool Lock0 { get; set; }

        /// <summary>内部索引 1（界面 Lock2，实训柜1）默认权限</summary>
        [JsonProperty("lock_1")]
        public bool Lock1 { get; set; }

        /// <summary>内部索引 2（界面 Lock3，实训柜2）默认权限</summary>
        [JsonProperty("lock_2")]
        public bool Lock2 { get; set; }

        /// <summary>内部索引 3（界面 Lock4，实训柜3）默认权限</summary>
        [JsonProperty("lock_3")]
        public bool Lock3 { get; set; }

        /// <summary>更新时间</summary>
        [JsonProperty("update_time")]
        public DateTime UpdateTime { get; set; }

        /// <summary>
        /// 将 4 把锁的权限转为 bool 数组（内部索引 0-3，界面 Lock1-4）
        /// </summary>
        public bool[] ToArray()
        {
            return new bool[] { Lock0, Lock1, Lock2, Lock3 };
        }

        /// <summary>
        /// 从 bool 数组（内部索引 0-3，界面 Lock1-4）设置权限
        /// </summary>
        public void FromArray(bool[] arr)
        {
            if (arr == null) return;
            Lock0 = arr.Length > 0 && arr[0];
            Lock1 = arr.Length > 1 && arr[1];
            Lock2 = arr.Length > 2 && arr[2];
            Lock3 = arr.Length > 3 && arr[3];
        }
    }
}
