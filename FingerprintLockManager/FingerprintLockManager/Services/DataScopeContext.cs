namespace FingerprintLockManager
{
    /// <summary>
    /// 数据范围上下文默认实现（V2.7）。
    /// 从 App.CurrentUser 读取当前登录用户，按角色提供可见范围过滤。
    /// </summary>
    public class DataScopeContext : IDataScopeContext
    {
        public static DataScopeContext Instance { get; } = new();

        public User? CurrentUser => App.CurrentUser;

        public bool IsAdmin => string.Equals(CurrentUser?.Role, "admin", StringComparison.OrdinalIgnoreCase);
        public bool IsTeacher => string.Equals(CurrentUser?.Role, "teacher", StringComparison.OrdinalIgnoreCase);
        public bool IsStudent => string.Equals(CurrentUser?.Role, "student", StringComparison.OrdinalIgnoreCase);

        public bool CanSee(User target)
        {
            if (CurrentUser == null) return false;
            if (IsAdmin) return true;
            if (IsStudent) return string.Equals(CurrentUser.UserId, target.UserId, StringComparison.OrdinalIgnoreCase);
            if (IsTeacher)
            {
                // 教师可见本班学生 + 自己 + 所有管理员（管理员信息对教师只读可见）
                if (string.Equals(CurrentUser.UserId, target.UserId, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(target.Role, "admin", StringComparison.OrdinalIgnoreCase)) return true;
                // 本班学生：class_id 匹配且角色为 student
                if (string.Equals(target.Role, "student", StringComparison.OrdinalIgnoreCase))
                {
                    string? myClass = CurrentUser.ClassId;
                    return !string.IsNullOrEmpty(myClass) &&
                           string.Equals(myClass, target.ClassId, StringComparison.OrdinalIgnoreCase);
                }
                // 其他教师：不可见（数据范围隔离）
                return false;
            }
            return false;
        }

        public bool CanModify(User target)
        {
            if (CurrentUser == null) return false;
            if (IsAdmin) return true;
            if (IsStudent) return false;  // 学生不可增删改任何用户
            if (IsTeacher)
            {
                // 教师仅可操作本班学生；不可操作管理员或其他教师
                if (string.Equals(target.Role, "student", StringComparison.OrdinalIgnoreCase))
                {
                    string? myClass = CurrentUser.ClassId;
                    return !string.IsNullOrEmpty(myClass) &&
                           string.Equals(myClass, target.ClassId, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            return false;
        }

        public bool CanCreate(User target)
        {
            if (CurrentUser == null) return false;
            if (IsAdmin) return true;
            if (!IsTeacher) return false;
            return string.Equals(target.Role, "student", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(CurrentUser.ClassId) &&
                   string.Equals(CurrentUser.ClassId, target.ClassId, StringComparison.OrdinalIgnoreCase);
        }

        public bool CanUpdate(User existing, User updated)
        {
            if (!CanModify(existing)) return false;
            if (IsAdmin) return true;
            return IsTeacher &&
                   string.Equals(existing.Role, "student", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(updated.Role, "student", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(CurrentUser?.ClassId, updated.ClassId, StringComparison.OrdinalIgnoreCase);
        }

        public List<string>? GetVisibleClassIds()
        {
            if (IsAdmin) return null;  // null = 不限制
            if (IsTeacher)
            {
                var myClass = CurrentUser?.ClassId;
                return string.IsNullOrEmpty(myClass) ? new List<string>() : new List<string> { myClass };
            }
            // Student
            return new List<string>();
        }

        public void EnsureCanModify(User target)
        {
            if (!CanModify(target))
            {
                throw new UnauthorizedAccessException(
                    $"当前用户 {CurrentUser?.UserId} ({CurrentUser?.Role}) 无权操作用户 {target.UserId} ({target.Role})");
            }
        }

        public void EnsureCanCreate(User target)
        {
            if (!CanCreate(target))
            {
                throw new UnauthorizedAccessException(
                    $"当前用户 {CurrentUser?.UserId} ({CurrentUser?.Role}) 无权创建用户 {target.UserId} ({target.Role})");
            }
        }

        public void EnsureCanUpdate(User existing, User updated)
        {
            if (!CanUpdate(existing, updated))
            {
                throw new UnauthorizedAccessException(
                    $"当前用户 {CurrentUser?.UserId} ({CurrentUser?.Role}) 无权修改用户 {existing.UserId}");
            }
        }
    }
}
