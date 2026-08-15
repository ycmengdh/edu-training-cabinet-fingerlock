using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

[Collection("Business database serial")]
public sealed class CabinetContentPermissionVersionTests
{
    [Fact]
    public void StudentChange_ChangesOnlyAssignedCabinetVersion()
    {
        RunWithDatabase(() =>
        {
            ReplaceUsers(StudentJson("Student One", "male"), 1);
            SaveTemplate(11, "S001", 0x11);
            var service = new CabinetSyncService();
            IReadOnlyDictionary<string, CabinetExpectedSyncState> before =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            ReplaceUsers(StudentJson("Student Renamed", "male"), 2);
            IReadOnlyDictionary<string, CabinetExpectedSyncState> after =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            Assert.Equal(before["CAB_01"].Version, after["CAB_01"].Version);
            Assert.NotEqual(before["CAB_02"].Version, after["CAB_02"].Version);
        });
    }

    [Fact]
    public void UnsentUserMetadataChange_DoesNotChangeCabinetVersions()
    {
        RunWithDatabase(() =>
        {
            ReplaceUsers(StudentJson("Student One", "male"), 1);
            SaveTemplate(11, "S001", 0x11);
            var service = new CabinetSyncService();
            IReadOnlyDictionary<string, CabinetExpectedSyncState> before =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            ReplaceUsers(StudentJson("Student One", "female"), 2);
            IReadOnlyDictionary<string, CabinetExpectedSyncState> after =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            Assert.Equal(before["CAB_01"].Version, after["CAB_01"].Version);
            Assert.Equal(before["CAB_02"].Version, after["CAB_02"].Version);
        });
    }

    [Fact]
    public void GlobalStaffChange_ChangesEveryCabinetVersion()
    {
        RunWithDatabase(() =>
        {
            ReplaceUsers(StaffJson("Teacher One"), 1);
            SaveTemplate(21, "T001", 0x21);
            var service = new CabinetSyncService();
            IReadOnlyDictionary<string, CabinetExpectedSyncState> before =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            ReplaceUsers(StaffJson("Teacher Renamed"), 2);
            IReadOnlyDictionary<string, CabinetExpectedSyncState> after =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            Assert.NotEqual(before["CAB_01"].Version, after["CAB_01"].Version);
            Assert.NotEqual(before["CAB_02"].Version, after["CAB_02"].Version);
        });
    }

    [Fact]
    public void TemplateContentChange_ChangesOnlyCabinetsUsingThatFingerprint()
    {
        RunWithDatabase(() =>
        {
            ReplaceUsers(StudentJson("Student One", "male"), 1);
            SaveTemplate(11, "S001", 0x11);
            var service = new CabinetSyncService();
            IReadOnlyDictionary<string, CabinetExpectedSyncState> before =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            SaveTemplate(11, "S001", 0x77);
            IReadOnlyDictionary<string, CabinetExpectedSyncState> after =
                service.GetExpectedCabinetSyncStates(DeviceIds);

            Assert.Equal(before["CAB_01"].Version, after["CAB_01"].Version);
            Assert.NotEqual(before["CAB_02"].Version, after["CAB_02"].Version);
        });
    }

    private static readonly string[] DeviceIds = { "CAB_01", "CAB_02" };

    private static void RunWithDatabase(Action test)
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        User? originalUser = App.CurrentUser;
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"fingerlock-version-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
            [
              {"device_id":"CAB_01","device_name":"Cabinet 1","is_root":false},
              {"device_id":"CAB_02","device_name":"Cabinet 2","is_root":false}
            ]
            """), 1);
            BusinessDatabase.ReplaceTable("classes", new JArray(), 1);
            BusinessDatabase.ReplaceTable("permissions", new JArray(), 1);
            BusinessDatabase.ReplaceTable("role_permissions", new JArray(), 1);
            App.CurrentUser = new User { UserId = "admin", Role = "admin" };
            test();
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    private static void ReplaceUsers(JArray users, uint version) =>
        BusinessDatabase.ReplaceTable("users", users, version);

    private static JArray StudentJson(string name, string gender) => JArray.Parse($$"""
    [{
      "user_id":"S001","name":"{{name}}","gender":"{{gender}}",
      "role":"student","enabled":true,"fingerprint_id":11,
      "cabinet_assignments":[
        {"device_id":"CAB_02","fingerprint_ids":[11],"lock_ids":[1]}
      ]
    }]
    """);

    private static JArray StaffJson(string name) => JArray.Parse($$"""
    [{
      "user_id":"T001","name":"{{name}}","role":"teacher",
      "enabled":true,"fingerprint_id":21
    }]
    """);

    private static void SaveTemplate(int fingerprintId, string userId, byte value) =>
        BusinessDatabase.SaveFpTemplateWithMeta(
            fingerprintId, userId, 1,
            Enumerable.Repeat(value, 512).ToArray(), "CAB_01");

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(candidate); } catch { }
        }
    }
}
