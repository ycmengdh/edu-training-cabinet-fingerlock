namespace CabinetLock.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly string _originalPath = BusinessDatabase.ActiveDbPath;
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"fingerlock-auth-{Guid.NewGuid():N}.db");

    public AuthServiceTests()
    {
        BusinessDatabase.SetActivePath(_tempPath);
        BusinessDatabase.Initialize();
    }

    public void Dispose()
    {
        BusinessDatabase.SetActivePath(_originalPath);
        foreach (string path in new[] { _tempPath, _tempPath + "-wal", _tempPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    [Fact]
    public void BuiltInAdministratorCredentials_AreExact()
    {
        var service = new AuthService();

        Assert.True(service.IsBuiltInAdministratorCredentials("admin", "admin123"));
        Assert.False(service.IsBuiltInAdministratorCredentials("Admin", "admin123"));
        Assert.False(service.IsBuiltInAdministratorCredentials("admin", "Admin123"));
    }

    [Fact]
    public void Login_AcceptsAdminWhenRootUnavailable()
    {
        // 根节点不可用时：
        // - 本地无 users：走内置 admin/admin123
        // - 本地已有 users：严格按缓存账户验证
        Assert.False(App.SdStorageService.IsAvailable);

        User? user = new AuthService().Login("admin", "admin123");

        Assert.NotNull(user);
        Assert.Equal("admin", user.DisplayId);
        Assert.Equal("admin", user.Role);
        Assert.True(user.Enabled);
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("teacher", "admin123")]
    public void Login_RejectsOtherCredentialsWhenRootDataIsUnavailable(
        string userId, string password)
    {
        Assert.False(App.SdStorageService.IsAvailable);

        Assert.Null(new AuthService().Login(userId, password));
    }

    [Fact]
    public void Login_TrimsUserId()
    {
        Assert.False(App.SdStorageService.IsAvailable);

        User? user = new AuthService().Login("  admin  ", "admin123");

        Assert.NotNull(user);
        Assert.Equal("admin", user.DisplayId);
    }

    [Fact]
    public void Login_RepairsMissingSystemAdministratorInNonEmptyDatabase()
    {
        BusinessDatabase.ReplaceTable("users", Newtonsoft.Json.Linq.JArray.Parse(
            "[{\"user_id\":\"teacher\",\"user_code\":\"teacher\",\"name\":\"Teacher\",\"role\":\"teacher\",\"enabled\":true}]"), 1);

        User? user = new AuthService().Login("admin", "admin123");

        Assert.NotNull(user);
        Assert.True(user.IsSystemAdministrator);
        Assert.Contains(BusinessDatabase.ReadArray("users")
            .OfType<Newtonsoft.Json.Linq.JObject>()
            .Select(row => row.Value<string>("user_id")), id => id == "admin");
    }

    [Fact]
    public void SystemAdministrator_ProfilePasswordAndFingerprintAreEditable_ButAccountCannotBeDeleted()
    {
        User? originalUser = App.CurrentUser;
        try
        {
            User administrator = SystemAdministratorPolicy.CreateDefault();
            BusinessDatabase.ReplaceTable("users",
                Newtonsoft.Json.Linq.JArray.FromObject(new[] { administrator }), 1);
            App.CurrentUser = administrator;
            var service = new UserService();

            Assert.False(service.DeleteUser(SystemAdministratorPolicy.UserId));
            administrator.Name = "Changed";
            administrator.Gender = "男";
            administrator.UserCode = "renamed-admin";
            administrator.Role = "teacher";
            Assert.True(service.UpdateUser(administrator));
            Assert.True(service.AssignFingerprint(
                SystemAdministratorPolicy.UserId, 23));

            User fingerprinted = Assert.Single(service.GetAllUsers());
            Assert.Equal("Changed", fingerprinted.Name);
            Assert.Equal("男", fingerprinted.Gender);
            Assert.Equal(SystemAdministratorPolicy.UserId, fingerprinted.UserCode);
            Assert.Equal("admin", fingerprinted.Role);
            Assert.Equal(23, fingerprinted.FingerprintId);

            Assert.True(service.SetEnabled(
                SystemAdministratorPolicy.UserId, false));
            Assert.False(Assert.Single(service.GetAllUsers()).Enabled);
            Assert.True(service.SetEnabled(
                SystemAdministratorPolicy.UserId, true));
            Assert.True(service.ClearFingerprint(
                SystemAdministratorPolicy.UserId, 23));
            Assert.True(service.ResetPassword(
                SystemAdministratorPolicy.UserId, "new-admin-password"));

            User stored = Assert.Single(service.GetAllUsers());
            Assert.Equal("Changed", stored.Name);
            Assert.Null(stored.FingerprintId);
            Assert.True(PasswordHelper.VerifyPassword("new-admin-password",
                stored.PasswordSalt, stored.PasswordHash));
        }
        finally
        {
            App.CurrentUser = originalUser;
        }
    }

    [Fact]
    public void SnapshotImport_PreservesSystemAdministratorProfileAndCredentials()
    {
        User administrator = SystemAdministratorPolicy.CreateDefault();
        administrator.Name = "值班管理员";
        administrator.Gender = "女";
        administrator.FingerprintId = 31;
        administrator.PasswordSalt = PasswordHelper.GenerateSalt();
        administrator.PasswordHash = PasswordHelper.HashPassword(
            "snapshot-admin-password", administrator.PasswordSalt);

        var tables = BusinessDatabase.DailySyncTables.ToDictionary(
            table => table,
            _ => new Newtonsoft.Json.Linq.JArray(),
            StringComparer.OrdinalIgnoreCase);
        tables["users"].Add(Newtonsoft.Json.Linq.JObject.FromObject(administrator));
        var versions = BusinessDatabase.DailySyncTables.ToDictionary(
            table => table, _ => 7U, StringComparer.OrdinalIgnoreCase);

        BusinessDatabase.ReplaceBusinessSnapshot(tables, versions);

        User stored = Assert.Single(new UserService().GetAllUsers());
        Assert.Equal("值班管理员", stored.Name);
        Assert.Equal("女", stored.Gender);
        Assert.Equal(31, stored.FingerprintId);
        Assert.True(PasswordHelper.VerifyPassword("snapshot-admin-password",
            stored.PasswordSalt, stored.PasswordHash));
    }
}
