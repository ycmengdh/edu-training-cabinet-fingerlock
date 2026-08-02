namespace CabinetLock.Tests;

public class AuthServiceTests
{
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
        Assert.Equal("admin", user.UserId);
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
        Assert.Equal("admin", user.UserId);
    }
}
