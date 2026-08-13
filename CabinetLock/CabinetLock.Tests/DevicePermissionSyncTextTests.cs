namespace CabinetLock.Tests;

public class DevicePermissionSyncTextTests
{
    [Fact]
    public void PermissionSyncText_UsesUnknownWhenEitherVersionIsMissing()
    {
        var missingReported = DeviceWithVersions(reported: 0, expected: 42);
        var missingExpected = DeviceWithVersions(reported: 42, expected: 0);

        Assert.Equal("未知", missingReported.PermissionSyncText);
        Assert.Equal("未知", missingExpected.PermissionSyncText);
    }

    [Fact]
    public void PermissionSyncText_ReportsSyncedOrLaggingWhenBothVersionsExist()
    {
        Assert.Equal("已同步", DeviceWithVersions(reported: 42, expected: 42).PermissionSyncText);
        Assert.Equal("落后", DeviceWithVersions(reported: 41, expected: 42).PermissionSyncText);
    }

    [Fact]
    public void PermissionSyncText_ReportsOfflineBeforeComparingVersions()
    {
        Device device = DeviceWithVersions(reported: 42, expected: 42);
        device.IsOnline = false;

        Assert.Equal("离线", device.PermissionSyncText);
    }

    private static Device DeviceWithVersions(uint reported, uint expected) => new()
    {
        IsOnline = true,
        RootPermissionVersion = expected,
        Status = new DeviceRuntimeStatus { PermissionVersion = reported }
    };
}
