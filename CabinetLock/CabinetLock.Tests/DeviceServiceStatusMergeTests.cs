using System.Reflection;

namespace CabinetLock.Tests;

public class DeviceServiceStatusMergeTests
{
    [Fact]
    public void ApplyLiveRuntimeStatus_WithFreshResponse_ReplacesPersistedSnapshot()
    {
        var persisted = new Device
        {
            Status = new DeviceRuntimeStatus { PermissionCount = 0, PermissionVersion = 1 }
        };
        var live = new DeviceClient
        {
            LastStatusAt = DateTime.Now,
            Status = new DeviceRuntimeStatus
            {
                FingerprintCount = 4,
                PermissionCount = 2,
                PermissionVersion = 1490992728
            }
        };

        ApplyLiveRuntimeStatus(persisted, live);

        Assert.Same(live.Status, persisted.Status);
        Assert.Equal(4, persisted.Status.FingerprintCount);
        Assert.Equal(2, persisted.Status.PermissionCount);
        Assert.Equal(1490992728U, persisted.Status.PermissionVersion);
    }

    [Fact]
    public void ApplyLiveRuntimeStatus_WithoutResponse_KeepsPersistedSnapshot()
    {
        var original = new DeviceRuntimeStatus
        {
            PermissionCount = 2,
            PermissionVersion = 1490992728
        };
        var persisted = new Device { Status = original };
        var live = new DeviceClient { Status = new DeviceRuntimeStatus() };

        ApplyLiveRuntimeStatus(persisted, live);

        Assert.Same(original, persisted.Status);
    }

    [Theory]
    [InlineData("", "", "CAB_AABBCCDDEE01", "CAB_AABBCCDDEE01")]
    [InlineData("Cabinet Node", "", "CAB_AABBCCDDEE01", "CAB_AABBCCDDEE01")]
    [InlineData("实训柜", "", "CAB_AABBCCDDEE01", "CAB_AABBCCDDEE01")]
    [InlineData("一号柜", "现场-01", "一号柜", "现场-01")]
    public void ApplyDefaultIdentity_UsesCabMacWithoutOverwritingUserValues(
        string name, string number, string expectedName, string expectedNumber)
    {
        var device = new Device
        {
            DeviceId = "CAB_OLD",
            MeshMac = "AA:BB:CC:DD:EE:01",
            DeviceName = name,
            DeviceNumber = number
        };

        MethodInfo method = typeof(DeviceService).GetMethod(
            "ApplyDefaultIdentity", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DeviceService.ApplyDefaultIdentity not found");
        method.Invoke(null, new object[] { device });

        Assert.Equal(expectedName, device.DeviceName);
        Assert.Equal(expectedNumber, device.DeviceNumber);
        Assert.Equal("CAB_OLD", device.DeviceId);
    }

    private static void ApplyLiveRuntimeStatus(Device target, DeviceClient source)
    {
        MethodInfo method = typeof(DeviceService).GetMethod(
            "ApplyLiveRuntimeStatus", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DeviceService.ApplyLiveRuntimeStatus not found");
        method.Invoke(null, new object[] { target, source });
    }
}
