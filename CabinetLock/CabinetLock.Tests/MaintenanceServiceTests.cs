using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public sealed class MaintenanceServiceTests : IDisposable
{
    private readonly string _originalPath = BusinessDatabase.ActiveDbPath;
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"fingerlock-maintenance-{Guid.NewGuid():N}.db");

    public MaintenanceServiceTests()
    {
        BusinessDatabase.SetActivePath(_tempPath);
        BusinessDatabase.Initialize();
    }

    [Fact]
    public void SimplifiedRegister_DoesNotClearReportedMaintenanceState()
    {
        var service = new MaintenanceService();
        service.HandleReported("CABINET_001", JObject.FromObject(new
        {
            maintenance_active = true,
            maintenance_lock_mask = 5,
            maintenance_source = "remote",
            maintenance_config_version = 7
        }));

        service.HandleReported("CABINET_001", JObject.FromObject(new
        {
            device_id = "CABINET_001",
            firmware_version = "26081201-cab"
        }));

        var device = new Device { DeviceId = "CABINET_001" };
        service.ApplyState(device);
        Assert.True(device.MaintenanceActive);
        Assert.Equal(5, device.MaintenanceLockMask);
        Assert.Equal("remote", device.MaintenanceSource);
        Assert.False(service.NeedsConfigurationSync(
            device.DeviceId, "26081201-cab"));
    }

    [Fact]
    public void UnknownMaintenanceVersion_DoesNotCreateSpeculativeSyncWork()
    {
        var service = new MaintenanceService();

        Assert.False(service.NeedsConfigurationSync(
            "CABINET_001", "26081201-cab"));

        service.HandleReported("CABINET_001", JObject.FromObject(new
        {
            maintenance_config_version = 0
        }));
        Assert.True(service.NeedsConfigurationSync(
            "CABINET_001", "26081201-cab"));
    }

    public void Dispose()
    {
        BusinessDatabase.SetActivePath(_originalPath);
        try { File.Delete(_tempPath); } catch { }
        try { File.Delete(_tempPath + "-wal"); } catch { }
        try { File.Delete(_tempPath + "-shm"); } catch { }
    }
}
