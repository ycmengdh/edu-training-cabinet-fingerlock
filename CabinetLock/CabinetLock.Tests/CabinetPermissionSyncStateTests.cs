using System.Reflection;

namespace CabinetLock.Tests;

public class CabinetPermissionSyncStateTests
{
    [Fact]
    public void Row_WithMatchingPermissionVersion_StillNeedsFingerprintVerification()
    {
        var device = new Device
        {
            DeviceId = "CAB_01",
            IsOnline = true,
            ExpectedFingerprintCount = 1,
            Status = new DeviceRuntimeStatus
            {
                PermissionVersion = 42,
                PermissionCount = 1,
                FingerprintCount = 1
            }
        };

        var row = new CabinetPermissionSyncRow(device, expectedVersion: 42);

        Assert.Equal("待同步", row.Status);
        Assert.Equal(0, row.Progress);
        Assert.True(row.NeedsSync);
        Assert.True(row.CanSync);
        Assert.Contains("核验", row.Detail);
    }

    [Theory]
    [InlineData(0u, 42u)]
    [InlineData(41u, 42u)]
    public void Row_WithMissingOrDifferentVersion_NeedsSync(uint reported, uint expected)
    {
        var device = new Device
        {
            DeviceId = "CAB_01",
            IsOnline = true,
            Status = new DeviceRuntimeStatus { PermissionVersion = reported }
        };

        var row = new CabinetPermissionSyncRow(device, expected);

        Assert.Equal("待同步", row.Status);
        Assert.True(row.NeedsSync);
        Assert.True(row.CanSync);
    }

    [Fact]
    public void ConfirmedSync_UpdatesMeshRuntimeStatusImmediately()
    {
        var bridge = new MeshBridge();
        var client = new DeviceClient
        {
            DeviceId = "CAB_01",
            IsOnline = true,
            Status = new DeviceRuntimeStatus { PermissionVersion = 41, PermissionCount = 1 }
        };
        DeviceDictionary(bridge)["CAB_01"] = client;

        bridge.MarkPermissionSyncConfirmed("CAB_01", permissionVersion: 42, permissionCount: 6);

        Assert.Equal(42u, client.Status.PermissionVersion);
        Assert.Equal(6, client.Status.PermissionCount);
        Assert.NotNull(client.LastStatusAt);
    }

    [Fact]
    public void ConfirmedFingerprintSync_UpdatesMeshRuntimeStatusImmediately()
    {
        var bridge = new MeshBridge();
        var client = new DeviceClient
        {
            DeviceId = "CAB_01",
            IsOnline = true,
            Status = new DeviceRuntimeStatus { FingerprintCount = 1 }
        };
        DeviceDictionary(bridge)["CAB_01"] = client;

        bridge.MarkFingerprintSyncConfirmed("CAB_01", fingerprintCount: 6);

        Assert.Equal(6, client.Status.FingerprintCount);
        Assert.NotNull(client.LastStatusAt);
    }

    [Fact]
    public void PermissionVersion_IncludesPermissionAndTemplateContent()
    {
        var row = new CabinetPermissionDescriptor(11, "U001", "User", 2, 0x02);
        uint before = CabinetSyncService.ComputeCabinetPermissionVersion(
            new[] { row }, new Dictionary<int, uint> { [11] = 100 });
        uint permissionChanged = CabinetSyncService.ComputeCabinetPermissionVersion(
            new[] { row with { LockMask = 0x06 } },
            new Dictionary<int, uint> { [11] = 100 });
        uint templateChanged = CabinetSyncService.ComputeCabinetPermissionVersion(
            new[] { row }, new Dictionary<int, uint> { [11] = 101 });

        Assert.NotEqual(before, permissionChanged);
        Assert.NotEqual(before, templateChanged);
    }

    private static Dictionary<string, DeviceClient> DeviceDictionary(MeshBridge bridge)
    {
        FieldInfo field = typeof(MeshBridge).GetField(
            "_devices", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MeshBridge._devices not found");
        return (Dictionary<string, DeviceClient>)field.GetValue(bridge)!;
    }
}
