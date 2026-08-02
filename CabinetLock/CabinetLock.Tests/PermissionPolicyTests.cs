namespace CabinetLock.Tests;

public class PermissionPolicyTests
{
    [Theory]
    [InlineData("teacher")]
    [InlineData("student")]
    [InlineData("unknown")]
    public void Normalize_RemovesSystemLockFromNonAdmins(string role)
    {
        var normalized = PermissionPolicy.Normalize(new RolePermission
        {
            Role = role,
            Lock0 = true,
            Lock1 = true
        });

        Assert.False(normalized.Lock0);
        Assert.True(normalized.Lock1);
    }

    [Fact]
    public void Normalize_PreservesAdminSystemLockSetting()
    {
        var normalized = PermissionPolicy.Normalize(new RolePermission
        {
            Role = "ADMIN",
            Lock0 = true
        });

        Assert.Equal("admin", normalized.Role);
        Assert.True(normalized.Lock0);
    }

    [Fact]
    public void Enforce_MasksNonAdminFinalPermissions()
    {
        bool[] permissions = [true, true, false, true];

        PermissionPolicy.Enforce("teacher", permissions);

        Assert.Equal([false, true, false, true], permissions);
    }
}
