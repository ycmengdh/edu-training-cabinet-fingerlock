namespace CabinetLock
{
    /// <summary>班级服务。读写根节点 classes.json。</summary>
    public class ClassService
    {
        private readonly RootDataService _root = new RootDataService();

        public List<ClassInfo> GetAll()
        {
            return _root.Read<ClassInfo>("classes")
                .OrderBy(c => c.ClassId)
                .ToList();
        }

        public List<ClassInfo> GetVisible()
        {
            var all = GetAll();
            var visibleIds = DataScopeContext.Instance.GetVisibleClassIds();
            return visibleIds == null
                ? all
                : all.Where(c => visibleIds.Contains(c.ClassId, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        public ClassInfo? Get(string classId)
        {
            if (string.IsNullOrWhiteSpace(classId)) return null;
            return GetAll().FirstOrDefault(c => c.ClassId == classId);
        }

        public bool Add(ClassInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.ClassId) ||
                string.IsNullOrWhiteSpace(info.Name)) return false;
            if (!DataScopeContext.Instance.IsAdmin)
                throw new UnauthorizedAccessException("只有系统管理员可以创建班级");

            var list = _root.Read<ClassInfo>("classes");
            if (list.Any(c => c.ClassId == info.ClassId)) return false;

            info.Enabled = true;
            info.CreateTime = info.CreateTime == default ? DateTime.Now : info.CreateTime;
            list.Add(info);
            return _root.Save("classes", list);
        }

        public bool Update(ClassInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.ClassId)) return false;
            EnsureCanManage(info.ClassId);
            var list = _root.Read<ClassInfo>("classes");
            var existing = list.FirstOrDefault(c => c.ClassId == info.ClassId);
            if (existing == null) return false;
            int index = list.IndexOf(existing);
            list[index] = info;
            return _root.Save("classes", list);
        }

        public bool SetEnabled(string classId, bool enabled)
        {
            EnsureCanManage(classId);
            var list = _root.Read<ClassInfo>("classes");
            var item = list.FirstOrDefault(c => c.ClassId == classId);
            if (item == null) return false;
            item.Enabled = enabled;
            return _root.Save("classes", list);
        }

        public bool Delete(string classId)
        {
            if (string.IsNullOrWhiteSpace(classId)) return false;
            EnsureCanManage(classId);
            // 有绑定用户时禁止删除
            if (App.UserService.GetAllUsers().Any(u =>
                    string.Equals(u.Role, "student", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(u.ClassId, classId, StringComparison.OrdinalIgnoreCase)))
                return false;

            var list = _root.Read<ClassInfo>("classes");
            int removed = list.RemoveAll(c => c.ClassId == classId);
            return removed > 0 && _root.Save("classes", list);
        }

        private static void EnsureCanManage(string classId)
        {
            var scope = DataScopeContext.Instance;
            if (scope.IsAdmin) return;
            if (scope.IsTeacher && scope.CurrentUser?.IsResponsibleForClass(classId) == true)
            {
                throw new UnauthorizedAccessException("教师只能维护自己负责的班级");
            }
            throw new UnauthorizedAccessException("当前角色无权维护班级");
        }
    }
}
