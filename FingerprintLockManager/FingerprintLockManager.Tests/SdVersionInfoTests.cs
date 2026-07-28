namespace FingerprintLockManager.Tests;

public class SdVersionInfoTests
{
    [Fact]
    public void AdvanceAfterSuccessfulSave_TracksSharedPermissionVersion()
    {
        var versions = new SdVersionInfo
        {
            GlobalVersion = 10,
            UsersVersion = 3,
            PermissionsVersion = 7
        };

        versions.AdvanceAfterSuccessfulSave("users");
        versions.AdvanceAfterSuccessfulSave("permissions");
        versions.AdvanceAfterSuccessfulSave("role_permissions");

        Assert.Equal((uint)13, versions.GlobalVersion);
        Assert.Equal((uint)4, versions.UsersVersion);
        Assert.Equal((uint)10, versions.PermissionsVersion);
    }

    [Fact]
    public void DirectMaintenanceSnapshot_AllowsUnchangedRemoteVersions()
    {
        var snapshot = CreateDirectSnapshot();
        var remote = new SdVersionInfo
        {
            UsersVersion = 30,
            ClassesVersion = 39,
            PermissionsVersion = 83,
            DevicesVersion = 295,
            FpVersion = 18
        };

        Assert.True(snapshot.MatchesRemote(remote, out string conflict));
        Assert.Equal("", conflict);
    }

    [Fact]
    public void DirectMaintenanceSnapshot_BlocksConcurrentRemoteChange()
    {
        var snapshot = CreateDirectSnapshot();
        var remote = new SdVersionInfo
        {
            UsersVersion = 31,
            ClassesVersion = 39,
            PermissionsVersion = 83,
            DevicesVersion = 295,
            FpVersion = 18
        };

        Assert.False(snapshot.MatchesRemote(remote, out string conflict));
        Assert.Contains("users", conflict);
        Assert.Contains("30", conflict);
        Assert.Contains("31", conflict);
    }

    private static DirectMaintenanceStateService.SessionSnapshot CreateDirectSnapshot() => new()
    {
        Versions = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["users"] = 30,
            ["classes"] = 39,
            ["permissions"] = 83,
            ["devices"] = 295,
            ["fingerprints"] = 18
        }
    };
}
