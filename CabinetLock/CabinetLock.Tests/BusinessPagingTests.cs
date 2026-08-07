using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public sealed class BusinessPagingTests : IDisposable
{
    private readonly string _originalPath = BusinessDatabase.ActiveDbPath;
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"fingerlock-paging-{Guid.NewGuid():N}.db");
    private readonly User? _originalUser = App.CurrentUser;

    public BusinessPagingTests()
    {
        BusinessDatabase.SetActivePath(_tempPath);
        BusinessDatabase.Initialize();
        Seed();
        App.CurrentUser = SystemAdministratorPolicy.CreateDefault();
    }

    public void Dispose()
    {
        App.CurrentUser = _originalUser;
        BusinessDatabase.SetActivePath(_originalPath);
        foreach (string path in new[] { _tempPath, _tempPath + "-wal", _tempPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    [Fact]
    public void UserPaging_ReturnsOnlyRequestedPageAndPreservesTotalCount()
    {
        PagedResult<User> page = App.UserService.QueryVisibleUsersPage(
            pageIndex: 1, pageSize: 20, role: "student");

        Assert.Equal(120, page.TotalCount);
        Assert.Equal(20, page.Items.Count);
        Assert.Equal(1, page.PageIndex);
        Assert.All(page.Items, user => Assert.Equal("student", user.Role));
        Assert.Equal("STU_001_21", page.Items[0].DisplayId);

        PagedResult<User> roleSearch = App.UserService.QueryVisibleUsersPage(
            0, 20, keyword: "教师");
        Assert.Equal(2, roleSearch.TotalCount);
        Assert.All(roleSearch.Items, user => Assert.Equal("teacher", user.Role));
    }

    [Fact]
    public void UserPaging_EnforcesTeacherScopeInsideSqlQuery()
    {
        App.CurrentUser = BusinessDatabase.ReadUser("TEACHER_01");

        PagedResult<User> page = App.UserService.QueryVisibleUsersPage(0, 200);

        Assert.Equal(82, page.TotalCount);
        Assert.Contains(page.Items, user => user.UserId == "admin");
        Assert.Contains(page.Items, user => user.UserId == "TEACHER_01");
        Assert.DoesNotContain(page.Items, user => user.UserId == "TEACHER_02");
        Assert.DoesNotContain(page.Items, user => user.ClassId == "CLASS_003");
    }

    [Fact]
    public void ClassPaging_ComputesCountsAndTeacherSearchWithoutScanningInPageCode()
    {
        PagedResult<ClassInfo> firstPage = App.ClassService.QueryVisiblePage(0, 2);

        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(40, firstPage.Items[0].StudentCount);
        Assert.Equal(1, firstPage.Items[0].TeacherCount);
        Assert.Equal("Teacher One", firstPage.Items[0].TeacherText);

        PagedResult<ClassInfo> teacherMatches = App.ClassService.QueryVisiblePage(
            0, 20, "Teacher One");
        Assert.Equal(2, teacherMatches.TotalCount);
        Assert.Equal(new[] { "CLASS_001", "CLASS_002" },
            teacherMatches.Items.Select(item => item.ClassId));
    }

    [Fact]
    public void PermissionQueries_ReadOnlyRequestedUsers()
    {
        string[] userIds = ["STU_001_01", "STU_003_40"];

        Dictionary<string, DateTime> updates =
            App.PermissionService.GetLatestUpdateTimes(userIds);
        List<UserPermission> permissions =
            App.PermissionService.GetUserPermissions("STU_001_01");

        Assert.Equal(2, updates.Count);
        Assert.Equal(4, permissions.Count);
        Assert.All(permissions, item => Assert.Equal("STU_001_01", item.UserId));
    }

    [Fact]
    public void DashboardStatistics_AreAggregatedInSqlAndRespectScope()
    {
        StudentBindingStatistics administrator =
            App.UserService.GetVisibleStudentBindingStatistics();
        Dictionary<string, string> codes = BusinessDatabase.ReadUserCodes(
            ["admin", "STU_001_01", "missing"]);

        Assert.Equal(120, administrator.TotalStudents);
        Assert.Equal(120, administrator.BoundStudents);
        Assert.Equal("admin", codes["admin"]);
        Assert.Equal("STU_001_01", codes["STU_001_01"]);
        Assert.DoesNotContain("missing", codes.Keys);

        App.CurrentUser = BusinessDatabase.ReadUser("TEACHER_01");
        StudentBindingStatistics teacher =
            App.UserService.GetVisibleStudentBindingStatistics();
        Assert.Equal(80, teacher.TotalStudents);
        Assert.Equal(80, teacher.BoundStudents);
    }

    [Fact]
    public void ClassWorkspaceQueries_ReturnOnlyRequestedClassData()
    {
        IReadOnlyList<string> teachers =
            BusinessDatabase.ReadTeacherNamesForClass("CLASS_002");
        List<User> students = App.UserService.QueryVisibleUsersPage(
            0, 500, role: "student", classId: "CLASS_002").Items.ToList();

        BusinessDatabase.SaveFpTemplateWithMeta(
            11, "STU_002_01", 6, new byte[] { 1, 2, 3 }, "CABINET_001");
        BusinessDatabase.SaveFpTemplateWithMeta(
            12, "STU_003_01", 6, new byte[] { 4, 5, 6 }, "CABINET_002");
        List<FingerprintTemplate> templates =
            BusinessDatabase.ReadFpTemplateMetasForUsers(
                students.Select(student => student.UserId));

        Assert.Equal(new[] { "Teacher One" }, teachers);
        Assert.Equal(40, students.Count);
        Assert.All(students, student => Assert.Equal("CLASS_002", student.ClassId));
        Assert.Single(templates);
        Assert.Equal("STU_002_01", templates[0].UserId);
    }

    [Fact]
    public void ClassWorkspaceQueries_BatchPermissionsAndLimitQueueJobs()
    {
        List<User> students = App.UserService.QueryVisibleUsersPage(
            0, 500, role: "student", classId: "CLASS_001").Items.ToList();
        Dictionary<string, bool[]> permissions =
            App.PermissionService.GetFinalPermissions(students);
        var queue = new CabinetSyncQueueService();
        queue.EnqueueUser("STU_001_01", new[] { "CABINET_001" }, "related user");
        queue.EnqueueUser("STU_003_01", new[] { "CABINET_002" }, "other user");
        queue.EnqueueCabinet("CABINET_001", "related cabinet");
        queue.EnqueueCabinet("CABINET_003", "other cabinet");

        IReadOnlyList<CabinetSyncJob> jobs = queue.GetRelevant(
            new[] { "STU_001_01" }, new[] { "CABINET_001" });

        Assert.Equal(40, permissions.Count);
        Assert.All(permissions.Values, values => Assert.Equal(4, values.Length));
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, job => job.JobKind == "user" && job.UserId == "STU_001_01");
        Assert.Contains(jobs, job => job.JobKind == "cabinet" && job.DeviceId == "CABINET_001");
        Assert.DoesNotContain(jobs, job => job.UserId == "STU_003_01" || job.DeviceId == "CABINET_003");
    }

    private static void Seed()
    {
        DateTime timestamp = new(2026, 8, 7, 9, 0, 0, DateTimeKind.Local);
        var classes = new List<ClassInfo>();
        var users = new List<User> { SystemAdministratorPolicy.CreateDefault(timestamp) };
        var permissions = new List<UserPermission>();
        int permissionId = 1;

        for (int classIndex = 1; classIndex <= 3; classIndex++)
        {
            string classId = $"CLASS_{classIndex:D3}";
            classes.Add(new ClassInfo
            {
                ClassId = classId,
                Name = $"Class {classIndex}",
                Enabled = true,
                CreateTime = timestamp
            });
            for (int studentIndex = 1; studentIndex <= 40; studentIndex++)
            {
                string userId = $"STU_{classIndex:D3}_{studentIndex:D2}";
                users.Add(new User
                {
                    UserId = userId,
                    UserCode = userId,
                    Name = $"Student {classIndex:D3}-{studentIndex:D2}",
                    Role = "student",
                    ClassId = classId,
                    Enabled = true,
                    CreateTime = timestamp
                });
                for (int lockId = 0; lockId < 4; lockId++)
                {
                    permissions.Add(new UserPermission
                    {
                        Id = permissionId++,
                        UserId = userId,
                        LockId = lockId,
                        HasAccess = lockId == studentIndex % 4,
                        UpdateTime = timestamp.AddMinutes(studentIndex)
                    });
                }
            }
        }

        var teacherOne = new User
        {
            UserId = "TEACHER_01",
            UserCode = "TEACHER_01",
            Name = "Teacher One",
            Role = "teacher",
            Enabled = true,
            CreateTime = timestamp
        };
        teacherOne.SetResponsibleClassIds(["CLASS_001", "CLASS_002"]);
        users.Add(teacherOne);

        var teacherTwo = new User
        {
            UserId = "TEACHER_02",
            UserCode = "TEACHER_02",
            Name = "Teacher Two",
            Role = "teacher",
            Enabled = true,
            CreateTime = timestamp
        };
        teacherTwo.SetResponsibleClassIds(["CLASS_003"]);
        users.Add(teacherTwo);

        BusinessDatabase.ReplaceTable("classes", JArray.FromObject(classes), 1);
        BusinessDatabase.ReplaceTable("users", JArray.FromObject(users), 1);
        BusinessDatabase.ReplaceTable("permissions", JArray.FromObject(permissions), 1);
    }
}
