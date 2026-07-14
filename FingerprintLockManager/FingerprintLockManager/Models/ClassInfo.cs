namespace FingerprintLockManager
{
    /// <summary>
    /// 班级模型
    /// 描述实训班级与负责老师的归属关系。
    /// 管理员和老师均可创建班级，但创建时必须指定负责老师（TeacherUserId）。
    /// 负责老师对该班级下的学生拥有：学生录入、指纹录入、柜子分配、权限分配等操作权。
    /// 数据持久化于根节点 SD 卡 classes.json。
    /// </summary>
    public class ClassInfo
    {
        /// <summary>班级唯一标识，如 "CLS2026001"</summary>
        public string ClassId { get; set; }

        /// <summary>班级名称，如 "电子21-1"</summary>
        public string ClassName { get; set; }

        /// <summary>负责老师 UserId（必须指向 Role=teacher 的用户）</summary>
        public string TeacherUserId { get; set; }

        /// <summary>备注描述（可选）</summary>
        public string Description { get; set; }

        /// <summary>学生人数（冗余字段，由 ClassService 维护，便于列表展示）</summary>
        public int StudentCount { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreateTime { get; set; }

        /// <summary>更新时间</summary>
        public DateTime? UpdateTime { get; set; }
    }
}
