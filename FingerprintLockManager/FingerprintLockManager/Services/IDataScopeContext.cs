using FingerprintLockManager;

namespace FingerprintLockManager
{
    /// <summary>
    /// 数据范围上下文（V2.7 教师数据隔离）。
    /// 根据当前登录用户的角色，决定其可见的数据范围：
    ///   - Admin: 全部数据可见，可操作全部
    ///   - Teacher: 仅本班学生可见；设备（柜子）全部可见但只能给本班学生分配权限
    ///   - Student: 仅自己可见
    /// </summary>
    public interface IDataScopeContext
    {
        /// <summary>当前登录用户（null 表示未登录）</summary>
        User? CurrentUser { get; }

        /// <summary>当前用户是否为管理员</summary>
        bool IsAdmin { get; }

        /// <summary>当前用户是否为教师</summary>
        bool IsTeacher { get; }

        /// <summary>当前用户是否为学生</summary>
        bool IsStudent { get; }

        /// <summary>
        /// 判断指定用户是否在当前用户的可见范围内。
        /// Admin 永远可见；Teacher 仅可见同班学生；Student 仅可见自己。
        /// </summary>
        bool CanSee(User target);

        /// <summary>
        /// 判断当前用户是否可以操作（增删改）指定用户。
        /// Admin 可操作全部（除自身最后管理员保护由 UserService 负责）；
        /// Teacher 仅可操作本班学生；Student 不可操作任何用户。
        /// </summary>
        bool CanModify(User target);

        bool CanCreate(User target);

        bool CanUpdate(User existing, User updated);

        /// <summary>
        /// 获取当前用户的可见班级 ID 集合（用于过滤）。
        /// Admin 返回 null 表示不限制；Teacher 返回 [本班ClassId]；
        /// Student 返回空列表（学生不按班级管理，仅按 UserId）。
        /// </summary>
        List<string>? GetVisibleClassIds();

        /// <summary>
        /// 断言当前用户可以操作指定用户，否则抛 UnauthorizedAccessException。
        /// 写操作调用此方法做防御性校验。
        /// </summary>
        void EnsureCanModify(User target);

        void EnsureCanCreate(User target);

        void EnsureCanUpdate(User existing, User updated);
    }
}
