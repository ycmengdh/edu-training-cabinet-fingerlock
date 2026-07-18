namespace FingerprintLockManager
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

        public ClassInfo? Get(string classId)
        {
            if (string.IsNullOrWhiteSpace(classId)) return null;
            return GetAll().FirstOrDefault(c => c.ClassId == classId);
        }

        public bool Add(ClassInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.ClassId) ||
                string.IsNullOrWhiteSpace(info.Name)) return false;

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
            var list = _root.Read<ClassInfo>("classes");
            var existing = list.FirstOrDefault(c => c.ClassId == info.ClassId);
            if (existing == null) return false;
            int index = list.IndexOf(existing);
            list[index] = info;
            return _root.Save("classes", list);
        }

        public bool SetEnabled(string classId, bool enabled)
        {
            var list = _root.Read<ClassInfo>("classes");
            var item = list.FirstOrDefault(c => c.ClassId == classId);
            if (item == null) return false;
            item.Enabled = enabled;
            return _root.Save("classes", list);
        }

        public bool Delete(string classId)
        {
            if (string.IsNullOrWhiteSpace(classId)) return false;
            // 有绑定用户时禁止删除
            if (App.UserService.GetAllUsers().Any(u => u.ClassId == classId))
                return false;

            var list = _root.Read<ClassInfo>("classes");
            int removed = list.RemoveAll(c => c.ClassId == classId);
            return removed > 0 && _root.Save("classes", list);
        }
    }
}
