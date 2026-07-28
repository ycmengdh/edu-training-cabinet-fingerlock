using Newtonsoft.Json.Linq;

namespace FingerprintLockManager.Tests;

[CollectionDefinition("Business database serial", DisableParallelization = true)]
public sealed class BusinessDatabaseSerialCollection
{
}

[Collection("Business database serial")]
public class BusinessDatabaseUserRoundtripTests
{
    [Fact]
    public void Users_RoundtripGenderAndAssignedDeviceIds()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.Parse("""
            [{
              "user_id":"S001",
              "name":"测试学生",
              "gender":"female",
              "role":"student",
              "class_id":"C01",
              "assigned_device_ids":["CAB_01","CAB_02"],
              "cabinet_assignments":[
                {"device_id":"CAB_01","active_fingerprint_id":12,"update_time":"2026-07-27T08:10:00+08:00"},
                {"device_id":"CAB_02","active_fingerprint_id":18,"update_time":"2026-07-27T08:11:00+08:00"}
              ],
              "fingerprint_id":12,
              "password_salt":"",
              "password_hash":"",
              "enabled":true,
              "create_time":"2026-07-27T08:00:00+08:00"
            }]
            """), 7);

            JObject stored = Assert.IsType<JObject>(Assert.Single(BusinessDatabase.ReadArray("users")));
            Assert.Equal("female", stored.Value<string>("gender"));
            Assert.Equal(new[] { "CAB_01", "CAB_02" },
                stored["assigned_device_ids"]!.Values<string>());
            Assert.Equal(new[] { 12, 18 },
                stored["cabinet_assignments"]!.Values<int>("active_fingerprint_id"));

            User user = stored.ToObject<User>()!;
            Assert.Equal("female", user.Gender);
            Assert.Equal(new[] { "CAB_01", "CAB_02" }, user.AssignedDeviceIds);
            Assert.Equal(18, user.CabinetAssignments![1].ActiveFingerprintId);
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void BatchAssignment_PersistsAndChangesPermissionSnapshotVersion()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        User? originalUser = App.CurrentUser;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.Parse("""
            [{
              "user_id":"S001",
              "name":"测试学生",
              "gender":"male",
              "role":"student",
              "class_id":"C01",
              "assigned_device_ids":[],
              "fingerprint_id":21,
              "password_salt":"",
              "password_hash":"",
              "enabled":true,
              "create_time":"2026-07-27T08:00:00+08:00"
            }]
            """), 3);
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
            [
              {"device_id":"CAB_01","device_name":"一号柜","is_root":false},
              {"device_id":"CAB_02","device_name":"二号柜","is_root":false}
            ]
            """), 2);
            BusinessDatabase.ReplaceTable("permissions", new JArray(), 1);
            BusinessDatabase.ReplaceTable("role_permissions", new JArray(), 1);
            App.CurrentUser = new User { UserId = "admin", Role = "admin" };
            uint before = CabinetSyncService.GetExpectedPermissionVersion();

            var service = new CabinetBindingService();
            Assert.True(service.SetUsersAssignments(
                new[] { "CAB_01", "CAB_02" }, new[] { "S001" }, true));

            User stored = BusinessDatabase.ReadArray("users").Single().ToObject<User>()!;
            Assert.Equal(new[] { "CAB_01", "CAB_02" }, stored.AssignedDeviceIds);
            Assert.DoesNotContain("S001", service.GetExcludedUserIds("CAB_01"));
            Assert.NotEqual(before, CabinetSyncService.GetExpectedPermissionVersion());

            Assert.True(service.SetUsersAssignment("cab_01", new[] { "S001" }, false));
            stored = BusinessDatabase.ReadArray("users").Single().ToObject<User>()!;
            Assert.Equal(new[] { "CAB_02" }, stored.AssignedDeviceIds);
            Assert.Contains("S001", service.GetExcludedUserIds("CAB_01"));
            Assert.DoesNotContain("S001", service.GetExcludedUserIds("cab_02"));
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void Devices_RoundtripEditableDeviceNumber()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
            [{
              "device_id":"CABINET_A1",
              "device_name":"实训柜",
              "device_number":"CAB-037",
              "mesh_mac":"AA:BB:CC:DD:EE:FF",
              "is_root":false,
              "online":true
            }]
            """), 9);

            Device device = BusinessDatabase.ReadArray("devices").Single().ToObject<Device>()!;
            Assert.Equal("CABINET_A1", device.DeviceId);
            Assert.Equal("CAB-037", device.DeviceNumber);
            Assert.Equal("AA:BB:CC:DD:EE:FF", device.MeshMac);
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void Devices_DuplicateNumbersAreNormalizedCaseInsensitively()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
            [
              {"device_id":"CAB_01","device_name":"一号","device_number":"CAB-001"},
              {"device_id":"CAB_02","device_name":"二号","device_number":"cab-001"}
            ]
            """), 1);

            string[] numbers = BusinessDatabase.ReadArray("devices")
                .Values<string>("device_number")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
            Assert.Single(numbers);
            Assert.Equal("CAB-001", numbers[0]);
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void FingerprintAllocator_ReusesFirstFreeGlobalSlot()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.Parse("""
            [
              {"user_id":"S1","name":"一","role":"student","fingerprint_id":1,"enabled":true},
              {"user_id":"S3","name":"三","role":"student","fingerprint_id":3,"enabled":true}
            ]
            """), 4);
            BusinessDatabase.SaveFpTemplateWithMeta(
                4, "S4", 1, Enumerable.Repeat((byte)0xA5, 512).ToArray(), "CAB_01");

            Assert.Equal(2, new UserService().GetNextFingerprintIdLocal());
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(candidate); } catch { }
        }
    }
}
