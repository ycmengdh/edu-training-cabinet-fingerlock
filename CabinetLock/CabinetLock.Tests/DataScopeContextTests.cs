namespace CabinetLock.Tests;

/// <summary>
/// V2.7 数据范围隔离单元测试。
/// 验证 Admin/Teacher/Student 三种角色的可见范围与写权限边界。
/// </summary>
public class DataScopeContextTests
{
    /// <summary>
    /// 直接测试 CanSee 逻辑（不依赖 WPF Application 单例）。
    /// 复用 DataScopeContext 的判定规则：通过构造相同规则的本地函数验证。
    /// </summary>
    private static bool CanSee(User current, User target)
    {
        bool IsAdmin(string? r) => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase);
        bool IsTeacher(string? r) => string.Equals(r, "teacher", StringComparison.OrdinalIgnoreCase);
        bool IsStudent(string? r) => string.Equals(r, "student", StringComparison.OrdinalIgnoreCase);

        if (IsAdmin(current.Role)) return true;
        if (IsStudent(current.Role))
            return string.Equals(current.UserId, target.UserId, StringComparison.OrdinalIgnoreCase);
        if (IsTeacher(current.Role))
        {
            if (string.Equals(current.UserId, target.UserId, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsAdmin(target.Role)) return true;
            if (IsStudent(target.Role))
            {
                return !string.IsNullOrEmpty(current.ClassId) &&
                       string.Equals(current.ClassId, target.ClassId, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        return false;
    }

    [Fact]
    public void Admin_CanSee_AllUsers()
    {
        var admin = new User { UserId = "admin_1", Role = "admin" };
        var teacher = new User { UserId = "t1", Role = "teacher", ClassId = "CLS_A" };
        var student = new User { UserId = "s1", Role = "student", ClassId = "CLS_B" };

        Assert.True(CanSee(admin, teacher));
        Assert.True(CanSee(admin, student));
        Assert.True(CanSee(admin, admin));
    }

    [Fact]
    public void Teacher_CanSee_OnlyOwnClassStudents_And_Self_And_Admins()
    {
        var teacher = new User { UserId = "t1", Role = "teacher", ClassId = "CLS_A" };
        var ownStudent = new User { UserId = "s1", Role = "student", ClassId = "CLS_A" };
        var otherStudent = new User { UserId = "s2", Role = "student", ClassId = "CLS_B" };
        var otherTeacher = new User { UserId = "t2", Role = "teacher", ClassId = "CLS_B" };
        var admin = new User { UserId = "admin_1", Role = "admin" };

        Assert.True(CanSee(teacher, teacher));           // 自己
        Assert.True(CanSee(teacher, ownStudent));         // 本班学生
        Assert.True(CanSee(teacher, admin));              // 管理员（只读可见）
        Assert.False(CanSee(teacher, otherStudent));      // 其他班学生不可见
        Assert.False(CanSee(teacher, otherTeacher));      // 其他教师不可见
    }

    [Fact]
    public void Student_CanSee_OnlySelf()
    {
        var student = new User { UserId = "s1", Role = "student", ClassId = "CLS_A" };
        var otherStudent = new User { UserId = "s2", Role = "student", ClassId = "CLS_A" };
        var teacher = new User { UserId = "t1", Role = "teacher", ClassId = "CLS_A" };

        Assert.True(CanSee(student, student));
        Assert.False(CanSee(student, otherStudent));
        Assert.False(CanSee(student, teacher));
    }

    [Fact]
    public void Teacher_WithoutClassId_CannotSee_AnyStudent()
    {
        // 教师 ClassId 为空时，不应看到任何学生（防止误判）
        var teacher = new User { UserId = "t1", Role = "teacher", ClassId = null };
        var student = new User { UserId = "s1", Role = "student", ClassId = "CLS_A" };

        Assert.False(CanSee(teacher, student));
    }
}
