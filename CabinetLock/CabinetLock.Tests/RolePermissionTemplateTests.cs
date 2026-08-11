using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

[Collection("Business database serial")]
public sealed class RolePermissionTemplateTests
{
    [Fact]
    public void NewStudent_DefaultsToAllCabinetDoorsWithoutSystemLock()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        User? originalUser = App.CurrentUser;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            App.CurrentUser = new User { UserId = "admin", Role = "admin" };

            Assert.True(new UserService().AddUser(new User
            {
                UserId = "S_DEFAULT",
                Name = "默认权限学生",
                Role = "student",
                CreateTime = DateTime.Now
            }));

            string userId = new UserService().GetUserByCode("S_DEFAULT")!.UserId;
            Assert.Equal([false, true, true, true],
                new PermissionService().GetFinalPermissions(userId));
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void UpdatingTemplate_PreservesExistingUsers_AndInitializesNewUsers()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        User? originalUser = App.CurrentUser;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.FromObject(new[]
            {
                new User
                {
                    UserId = "S_OLD",
                    Name = "已有学生",
                    Role = "student",
                    Enabled = true,
                    CreateTime = DateTime.Now
                }
            }), 1);
            BusinessDatabase.ReplaceTable("permissions", new JArray(), 1);
            BusinessDatabase.ReplaceTable("role_permissions", JArray.FromObject(new[]
            {
                new RolePermission
                {
                    Role = "student",
                    Lock1 = true,
                    UpdateTime = DateTime.Now
                }
            }), 1);
            App.CurrentUser = new User { UserId = "admin", Role = "admin" };

            var roleService = new RolePermissionService();
            Assert.True(roleService.SetAll(new[]
            {
                new RolePermission
                {
                    Role = "student",
                    Lock2 = true,
                    Lock3 = true
                }
            }));

            Assert.Equal([false, true, false, false],
                new PermissionService().GetFinalPermissions("S_OLD"));

            Assert.True(new UserService().AddUser(new User
            {
                UserId = "S_NEW",
                Name = "新学生",
                Role = "student",
                CreateTime = DateTime.Now
            }));

            string newUserId = new UserService().GetUserByCode("S_NEW")!.UserId;
            bool[] newUserPermissions = BusinessDatabase.ReadArray("permissions")
                .OfType<JObject>()
                .Where(item => item.Value<string>("user_id") == newUserId)
                .OrderBy(item => item.Value<int>("lock_id"))
                .Select(item => item.Value<bool>("has_access"))
                .ToArray();
            Assert.Equal([false, false, true, true], newUserPermissions);
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void UpdatingTemplate_RejectsNonAdministrator()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        User? originalUser = App.CurrentUser;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            App.CurrentUser = new User { UserId = "teacher_1", Role = "teacher" };

            Assert.Throws<UnauthorizedAccessException>(() =>
                new RolePermissionService().SetAll(new[]
                {
                    new RolePermission { Role = "student", Lock1 = true }
                }));
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
            catch
            {
            }
        }
    }
}
