using System.Reflection;

namespace CabinetLock.Tests;

public class CabinetPermissionSyncStateTests
{
    [Fact]
    public void Row_WithMatchingReportedVersion_IsAlreadySynced()
    {
        var device = new Device
        {
            DeviceId = "CAB_01",
            IsOnline = true,
            Status = new DeviceRuntimeStatus { PermissionVersion = 42 }
        };

        var row = new CabinetPermissionSyncRow(device, expectedVersion: 42);

        Assert.Equal("已同步", row.Status);
        Assert.Equal(100, row.Progress);
        Assert.False(row.NeedsSync);
        Assert.False(row.CanSync);
        Assert.Equal("当前权限版本已一致", row.Detail);
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

    private static Dictionary<string, DeviceClient> DeviceDictionary(MeshBridge bridge)
    {
        FieldInfo field = typeof(MeshBridge).GetField(
            "_devices", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MeshBridge._devices not found");
        return (Dictionary<string, DeviceClient>)field.GetValue(bridge)!;
    }
}
