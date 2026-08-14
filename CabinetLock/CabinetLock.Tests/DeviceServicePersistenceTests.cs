using System.Reflection;
using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

[Collection("Business database serial")]
public class DeviceServicePersistenceTests
{
    [Fact]
    public void PersistNewlySeenDevices_FirstSeenPersistsWithoutVersionChurn()
    {
        WithTemporaryDatabase(() =>
        {
            var device = new Device
            {
                DeviceId = "CABINET_A1",
                DeviceName = "一号柜",
                MeshMac = "AA:BB:CC:DD:EE:01",
                IsOnline = true
            };

            Persist(device);

            Device stored = BusinessDatabase.ReadArray("devices").Single().ToObject<Device>()!;
            Assert.Equal(device.DeviceId, stored.DeviceId);
            Assert.Equal(device.MeshMac, stored.MeshMac);
            Assert.Equal(1U, BusinessDatabase.GetTableVersion("devices"));

            Persist(device);

            Assert.Single(BusinessDatabase.ReadArray("devices"));
            Assert.Equal(1U, BusinessDatabase.GetTableVersion("devices"));
        });
    }

    [Fact]
    public async Task PersistNewlySeenDevices_ConcurrentRegistrationsDoNotLoseDevices()
    {
        await WithTemporaryDatabaseAsync(async () =>
        {
            var first = new Device
            {
                DeviceId = "CABINET_A1",
                MeshMac = "AA:BB:CC:DD:EE:01"
            };
            var second = new Device
            {
                DeviceId = "CABINET_A2",
                MeshMac = "AA:BB:CC:DD:EE:02"
            };

            await Task.WhenAll(Task.Run(() => Persist(first)), Task.Run(() => Persist(second)));

            string[] ids = BusinessDatabase.ReadArray("devices")
                .Values<string>("device_id").OrderBy(id => id).ToArray()!;
            Assert.Equal(new[] { "CABINET_A1", "CABINET_A2" }, ids);
            Assert.Equal(2U, BusinessDatabase.GetTableVersion("devices"));
        });
    }

    [Fact]
    public void PersistNewlySeenDevices_DeduplicatesSamePhysicalDeviceWithinBatch()
    {
        WithTemporaryDatabase(() =>
        {
            Persist(
                new Device { DeviceId = "CABINET_OLD", MeshMac = "AA:BB:CC:DD:EE:01" },
                new Device { DeviceId = "CABINET_NEW", MeshMac = "aa:bb:cc:dd:ee:01" });

            Assert.Single(BusinessDatabase.ReadArray("devices"));
            Assert.Equal(1U, BusinessDatabase.GetTableVersion("devices"));
        });
    }

    [Fact]
    public void PersistNewlySeenDevices_KeepsLastNonEmptyFirmwareVersion()
    {
        WithTemporaryDatabase(() =>
        {
            var device = new Device
            {
                DeviceId = "CABINET_A1",
                MeshMac = "AA:BB:CC:DD:EE:01"
            };
            Persist(device);

            device.FirmwareVersion = "3.4.0-idf";
            device.HardwareVersion = "cabinet-v1";
            Persist(device);

            Device stored = BusinessDatabase.ReadArray("devices").Single()
                .ToObject<Device>()!;
            Assert.Equal("3.4.0-idf", stored.FirmwareVersion);
            Assert.Equal("cabinet-v1", stored.HardwareVersion);
            Assert.Equal(2U, BusinessDatabase.GetTableVersion("devices"));

            device.FirmwareVersion = "";
            device.HardwareVersion = "";
            Persist(device);

            stored = BusinessDatabase.ReadArray("devices").Single()
                .ToObject<Device>()!;
            Assert.Equal("3.4.0-idf", stored.FirmwareVersion);
            Assert.Equal("cabinet-v1", stored.HardwareVersion);
            Assert.Equal(2U, BusinessDatabase.GetTableVersion("devices"));
        });
    }

    [Fact]
    public void PersistNewlySeenDevices_UpdatesOnlyWhenFirmwareChanges()
    {
        WithTemporaryDatabase(() =>
        {
            var device = new Device
            {
                DeviceId = "CABINET_A1",
                MeshMac = "AA:BB:CC:DD:EE:01",
                FirmwareVersion = "3.4.0-idf"
            };
            Persist(device);
            Persist(device);
            Assert.Equal(1U, BusinessDatabase.GetTableVersion("devices"));

            device.FirmwareVersion = "3.5.0-idf";
            Persist(device);

            Device stored = BusinessDatabase.ReadArray("devices").Single()
                .ToObject<Device>()!;
            Assert.Equal("3.5.0-idf", stored.FirmwareVersion);
            Assert.Equal(2U, BusinessDatabase.GetTableVersion("devices"));
        });
    }

    [Fact]
    public void PersistNewlySeenDevices_MigratesFirmwareNameAndKeepsCustomName()
    {
        WithTemporaryDatabase(() =>
        {
            BusinessDatabase.ReplaceTable("devices", JArray.FromObject(new[]
            {
                new Device
                {
                    DeviceId = "CAB_AABBCCDDEE01",
                    DeviceName = "ESP-IDF Cabinet",
                    DeviceNumber = "CAB_AABBCCDDEE01",
                    MeshMac = "AA:BB:CC:DD:EE:01"
                },
                new Device
                {
                    DeviceId = "CAB_AABBCCDDEE02",
                    DeviceName = "二号实验柜",
                    DeviceNumber = "CAB_AABBCCDDEE02",
                    MeshMac = "AA:BB:CC:DD:EE:02"
                }
            }), 1);

            Persist(
                new Device
                {
                    DeviceId = "CAB_AABBCCDDEE01",
                    DeviceName = "CAB_AABBCCDDEE01",
                    DeviceNumber = "CAB_AABBCCDDEE01",
                    MeshMac = "AA:BB:CC:DD:EE:01"
                },
                new Device
                {
                    DeviceId = "CAB_AABBCCDDEE02",
                    DeviceName = "CAB_AABBCCDDEE02",
                    DeviceNumber = "CAB_AABBCCDDEE02",
                    MeshMac = "AA:BB:CC:DD:EE:02"
                });

            Device[] stored = BusinessDatabase.ReadArray("devices")
                .ToObject<Device[]>()!;
            Assert.Equal("CAB_AABBCCDDEE01", stored[0].DeviceName);
            Assert.Equal("二号实验柜", stored[1].DeviceName);
            Assert.Equal(2U, BusinessDatabase.GetTableVersion("devices"));
        });
    }

    [Fact]
    public void NormalizeManagedDeviceNames_MigratesDefaultsAndClearsRootName()
    {
        WithTemporaryDatabase(() =>
        {
            BusinessDatabase.ReplaceTable("devices", JArray.FromObject(new[]
            {
                new Device
                {
                    DeviceId = "CAB_AABBCCDDEE01",
                    DeviceName = "ESP-IDF Cabinet",
                    DeviceNumber = "CAB_AABBCCDDEE01",
                    MeshMac = "AA:BB:CC:DD:EE:01"
                },
                new Device
                {
                    DeviceId = "CAB_AABBCCDDEE02",
                    DeviceName = "二号实验柜",
                    MeshMac = "AA:BB:CC:DD:EE:02"
                },
                new Device
                {
                    DeviceId = "ROOT_AABBCCDDEE03",
                    DeviceName = "ESP-IDF Root",
                    DeviceNumber = "ROOT-01",
                    MeshMac = "AA:BB:CC:DD:EE:03"
                }
            }), 1);

            int changed = new DeviceService().NormalizeManagedDeviceNames();

            Device[] stored = BusinessDatabase.ReadArray("devices")
                .ToObject<Device[]>()!;
            Assert.Equal(3, changed);
            Assert.Equal("CAB_AABBCCDDEE01", stored[0].DeviceName);
            Assert.Equal("二号实验柜", stored[1].DeviceName);
            Assert.Equal("CAB_AABBCCDDEE02", stored[1].DeviceNumber);
            Assert.Equal("", stored[2].DeviceName);
            Assert.Equal("ROOT-01", stored[2].DeviceNumber);
        });
    }

    private static void Persist(params Device[] devices)
    {
        MethodInfo method = typeof(DeviceService).GetMethod(
            "PersistNewlySeenDevices", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DeviceService.PersistNewlySeenDevices not found");
        method.Invoke(null, new object[] { devices });
    }

    private static void WithTemporaryDatabase(Action action) =>
        WithTemporaryDatabaseAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    private static async Task WithTemporaryDatabaseAsync(Func<Task> action)
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("devices", new JArray(), 0);
            await action();
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            foreach (string candidate in new[] { tempPath, tempPath + "-wal", tempPath + "-shm" })
            {
                try { File.Delete(candidate); } catch { }
            }
        }
    }
}
