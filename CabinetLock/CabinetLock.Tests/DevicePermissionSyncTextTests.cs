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
    public void PermissionSyncText_RequiresExpectedPermissionCount()
    {
        Device device = DeviceWithVersions(reported: 42, expected: 42);
        device.ExpectedFingerprintCount = 3;
        device.Status.PermissionCount = 2;

        Assert.Equal("不完整", device.PermissionSyncText);
        Assert.Equal("权限不完整", device.DataSyncText);
    }

    [Fact]
    public void DataSyncText_DoesNotTreatMatchingPermissionVersionAsFullSync()
    {
        Device device = DeviceWithVersions(reported: 42, expected: 42);
        device.ExpectedFingerprintCount = 2;
        device.Status.PermissionCount = 2;
        device.Status.FingerprintCount = 2;

        Assert.Equal("待核验", device.DataSyncText);

        device.FingerprintVerificationVersion = 42;

        Assert.Equal("已同步", device.DataSyncText);
    }

    [Fact]
    public void DataSyncText_ReportsMissingFingerprintBeforeVerification()
    {
        Device device = DeviceWithVersions(reported: 42, expected: 42);
        device.ExpectedFingerprintCount = 2;
        device.Status.PermissionCount = 2;
        device.Status.FingerprintCount = 1;

        Assert.Equal("指纹缺失", device.DataSyncText);
    }

    [Fact]
    public void PermissionSyncText_ReportsOfflineBeforeComparingVersions()
    {
        Device device = DeviceWithVersions(reported: 42, expected: 42);
        device.IsOnline = false;

        Assert.Equal("离线", device.PermissionSyncText);
    }

    [Fact]
    public void RuntimeNotification_FiresOnlyWhenDisplayedDataChanges()
    {
        Device device = DeviceWithVersions(reported: 42, expected: 42);
        int notifications = 0;
        device.PropertyChanged += (_, _) => notifications++;
        device.CaptureRuntimeDataSnapshot();

        device.NotifyRuntimeDataChangedIfNeeded();
        Assert.Equal(0, notifications);

        device.Status.FingerprintCount = 1;
        device.NotifyRuntimeDataChangedIfNeeded();
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void RuntimeNotification_UsesDisplayedMinuteForHeartbeatUpdates()
    {
        Device device = DeviceWithVersions(reported: 42, expected: 42);
        device.LastSeenUnix = 1_700_000_040;
        int notifications = 0;
        device.PropertyChanged += (_, _) => notifications++;
        device.CaptureRuntimeDataSnapshot();

        device.LastSeenUnix += 30;
        device.NotifyRuntimeDataChangedIfNeeded();
        Assert.Equal(0, notifications);

        device.LastSeenUnix += 60;
        device.NotifyRuntimeDataChangedIfNeeded();
        Assert.Equal(1, notifications);

        device.IsOnline = false;
        device.NotifyRuntimeDataChangedIfNeeded();
        Assert.Equal(2, notifications);
    }

    private static Device DeviceWithVersions(uint reported, uint expected) => new()
    {
        IsOnline = true,
        RootPermissionVersion = expected,
        Status = new DeviceRuntimeStatus { PermissionVersion = reported }
    };
}
