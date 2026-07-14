using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 班级管理服务（需求 4）
    ///
    /// 管理实训班级的 CRUD 与归属关系：
    /// - 管理员和老师均可创建班级，创建时必须指定负责老师（TeacherUserId）
    /// - 负责老师对该班级下的学生拥有：学生录入、指纹录入、柜子分配、权限分配等操作权
    /// - 老师只能管理自己负责的班级数据
    ///
    /// 数据持久化于根节点 SD 卡 classes.json（经 DataStore）。
    /// </summary>
    public class ClassService
    {
        /// <summary>获取所有班级列表</summary>
        public List<ClassInfo> GetClasses()
        {
            return DataStore.Current.GetClasses();
        }

        /// <summary>获取某老师负责的班级列表</summary>
        public List<ClassInfo> GetClassesByTeacher(string teacherUserId)
        {
            return DataStore.Current.GetClasses()
                .Where(c => c.TeacherUserId == teacherUserId)
                .ToList();
        }

        /// <summary>获取单个班级</summary>
        public ClassInfo? GetClass(string classId)
        {
            return DataStore.Current.GetClasses().FirstOrDefault(c => c.ClassId == classId);
        }

        /// <summary>检查某老师是否有权管理某班级</summary>
        public bool CanTeacherManageClass(string teacherUserId, string classId)
        {
            var cls = GetClass(classId);
            return cls != null && cls.TeacherUserId == teacherUserId;
        }

        /// <summary>
        /// 添加班级
        /// 需求 4：录入班级信息时必须选择班级的负责老师
        /// </summary>
        /// <param name="classInfo">班级信息（ClassName 和 TeacherUserId 必填）</param>
        /// <returns>成功返回 null；失败返回错误信息</returns>
        public string? AddClass(ClassInfo classInfo)
        {
            if (string.IsNullOrWhiteSpace(classInfo.ClassName))
                return "班级名称不能为空";
            if (string.IsNullOrWhiteSpace(classInfo.TeacherUserId))
                return "必须指定负责老师";

            // 验证负责老师存在且角色为 teacher
            var teacher = DataStore.Current.GetUsers()
                .FirstOrDefault(u => u.UserId == classInfo.TeacherUserId && u.Role == "teacher");
            if (teacher == null)
                return "指定的负责老师不存在或角色不是 teacher";

            // 生成班级 ID（若未提供）
            if (string.IsNullOrEmpty(classInfo.ClassId))
            {
                classInfo.ClassId = GenerateClassId();
            }
            else
            {
                // 检查 ID 是否已存在
                if (DataStore.Current.GetClasses().Any(c => c.ClassId == classInfo.ClassId))
                    return "班级 ID 已存在";
            }

            classInfo.StudentCount = 0;
            classInfo.CreateTime = DateTime.Now;

            DataStore.Current.MutateClasses(list => list.Add(classInfo));
            return null;
        }

        /// <summary>更新班级信息</summary>
        public string? UpdateClass(ClassInfo classInfo)
        {
            if (string.IsNullOrWhiteSpace(classInfo.ClassId))
                return "班级 ID 不能为空";

            // 若更换了负责老师，验证新老师存在
            if (!string.IsNullOrEmpty(classInfo.TeacherUserId))
            {
                var teacher = DataStore.Current.GetUsers()
                    .FirstOrDefault(u => u.UserId == classInfo.TeacherUserId && u.Role == "teacher");
                if (teacher == null)
                    return "指定的负责老师不存在或角色不是 teacher";
            }

            classInfo.UpdateTime = DateTime.Now;
            DataStore.Current.MutateClasses(list =>
            {
                int idx = list.FindIndex(c => c.ClassId == classInfo.ClassId);
                if (idx >= 0) list[idx] = classInfo;
            });
            return null;
        }

        /// <summary>
        /// 删除班级
        /// 需求 10：学生毕业全班删除时，先删除学生再删班级
        /// 注意：本方法仅删除班级记录本身，不级联删除学生（学生删除由调用方先处理）
        /// </summary>
        public string? DeleteClass(string classId)
        {
            DataStore.Current.MutateClasses(list =>
                list.RemoveAll(c => c.ClassId == classId));
            return null;
        }

        /// <summary>获取班级下的学生列表</summary>
        public List<User> GetStudentsByClass(string classId)
        {
            return DataStore.Current.GetUsers()
                .Where(u => u.Role == "student" && u.ClassId == classId)
                .ToList();
        }

        /// <summary>统计班级学生人数并更新到班级记录</summary>
        public void RefreshStudentCount(string classId)
        {
            int count = DataStore.Current.GetUsers()
                .Count(u => u.Role == "student" && u.ClassId == classId);
            DataStore.Current.MutateClasses(list =>
            {
                int idx = list.FindIndex(c => c.ClassId == classId);
                if (idx >= 0)
                {
                    list[idx].StudentCount = count;
                    list[idx].UpdateTime = DateTime.Now;
                }
            });
        }

        /// <summary>生成班级 ID：CLS + 年份 + 4位序号</summary>
        private static string GenerateClassId()
        {
            string prefix = "CLS" + DateTime.Now.Year;
            var existing = DataStore.Current.GetClasses()
                .Where(c => c.ClassId.StartsWith(prefix))
                .Select(c => c.ClassId)
                .ToList();
            int maxSeq = 0;
            foreach (var id in existing)
            {
                if (id.Length > prefix.Length && int.TryParse(id.Substring(prefix.Length), out int seq))
                {
                    if (seq > maxSeq) maxSeq = seq;
                }
            }
            return prefix + (maxSeq + 1).ToString("D4");
        }
    }
}
