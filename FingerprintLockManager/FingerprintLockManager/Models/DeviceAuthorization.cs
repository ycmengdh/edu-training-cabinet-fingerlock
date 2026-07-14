namespace FingerprintLockManager
{
    /// <summary>
    /// 设备授权模型（学生 × 柜子 × 锁权限）
    /// 描述某个学生被授权到某台柜子，以及在该柜子上 4 把锁的开锁权限。
    /// 这是需求 6/8 的核心：学生只有被分配到柜子并授权后，其指纹和权限才会下发到该柜子。
    /// 老师权限不通过此表记录（老师指纹录入后自动广播下发到所有柜子，权限由 RolePermission 决定）。
    /// 数据持久化于根节点 SD 卡 device_authorizations.json。
    /// </summary>
    public class DeviceAuthorization
    {
        /// <summary>授权记录 ID（内存自增）</summary>
        public long Id { get; set; }

        /// <summary>学生 UserId</summary>
        public string UserId { get; set; }

        /// <summary>柜子 DeviceId（如 CABINET_001）</summary>
        public string DeviceId { get; set; }

        /// <summary>该学生在该柜子的 Lock0（系统锁）权限</summary>
        public bool Lock0 { get; set; }

        /// <summary>该学生在该柜子的 Lock1（实训柜1）权限</summary>
        public bool Lock1 { get; set; }

        /// <summary>该学生在该柜子的 Lock2（实训柜2）权限</summary>
        public bool Lock2 { get; set; }

        /// <summary>该学生在该柜子的 Lock3（实训柜3）权限</summary>
        public bool Lock3 { get; set; }

        /// <summary>指纹是否已下发到该柜子的 AS608 传感器</summary>
        public bool FingerprintDeployed { get; set; }

        /// <summary>权限下发时间（成功下发后更新）</summary>
        public DateTime? DeployTime { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreateTime { get; set; }

        /// <summary>更新时间</summary>
        public DateTime? UpdateTime { get; set; }

        /// <summary>将 4 把锁权限转为 bool 数组（顺序 Lock0-3）</summary>
        public bool[] ToLockArray()
        {
            return new bool[] { Lock0, Lock1, Lock2, Lock3 };
        }

        /// <summary>从 bool 数组设置 4 把锁权限</summary>
        public void FromLockArray(bool[] arr)
        {
            if (arr == null) return;
            Lock0 = arr.Length > 0 && arr[0];
            Lock1 = arr.Length > 1 && arr[1];
            Lock2 = arr.Length > 2 && arr[2];
            Lock3 = arr.Length > 3 && arr[3];
        }
    }
}
