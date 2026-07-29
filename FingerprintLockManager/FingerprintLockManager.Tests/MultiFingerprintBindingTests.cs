using Newtonsoft.Json.Linq;

namespace FingerprintLockManager.Tests;

[Collection("Business database serial")]
public sealed class MultiFingerprintBindingTests
{
    [Fact]
    public void SetActiveFingerprint_KeepsOnlyOneFingerprintPerCabinet()
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
              "user_id":"S001","name":"测试学生","role":"student","class_id":"C01",
              "assigned_device_ids":["CAB_01"],
              "cabinet_assignments":[
                {"device_id":"CAB_01","active_fingerprint_id":11,"update_time":"2026-07-28T08:00:00+08:00"}
              ],
              "fingerprint_id":11,"enabled":true,"create_time":"2026-07-28T08:00:00+08:00"
            }]
            """), 1);
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
              [{"device_id":"CAB_01","device_name":"一号柜","is_root":false}]
            """), 1);
            SaveTemplate(11, 2, "左手食指");
            SaveTemplate(12, 7, "右手食指");
            App.CurrentUser = new User { UserId = "admin", Role = "admin" };

            var service = new CabinetBindingService();
            Assert.True(service.SetActiveFingerprint("S001", "CAB_01", 12));

            User stored = BusinessDatabase.ReadArray("users").Single().ToObject<User>()!;
            Assert.Single(stored.CabinetAssignments!);
            Assert.Equal(12, stored.CabinetAssignments![0].ActiveFingerprintId);
            Assert.Equal(12, service.GetActiveFingerprintId(stored, "CAB_01"));
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void Student_CanUseDifferentFingerprintOnEachCabinet()
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
              "user_id":"S001","name":"测试学生","role":"student","class_id":"C01",
              "assigned_device_ids":["CAB_01","CAB_02"],"fingerprint_id":11,
              "enabled":true,"create_time":"2026-07-28T08:00:00+08:00"
            }]
            """), 1);
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
            [
              {"device_id":"CAB_01","device_name":"一号柜","is_root":false},
              {"device_id":"CAB_02","device_name":"二号柜","is_root":false}
            ]
            """), 1);
            SaveTemplate(11, 2, "左手食指");
            SaveTemplate(12, 7, "右手食指");
            App.CurrentUser = new User { UserId = "admin", Role = "admin" };

            var service = new CabinetBindingService();
            Assert.True(service.MigrateLegacyBindings());
            Assert.True(service.SetActiveFingerprint("S001", "CAB_02", 12));

            User stored = BusinessDatabase.ReadArray("users").Single().ToObject<User>()!;
            Assert.Equal(11, service.GetActiveFingerprintId(stored, "CAB_01"));
            Assert.Equal(12, service.GetActiveFingerprintId(stored, "CAB_02"));
            Assert.Equal(new[] { "CAB_01", "CAB_02" }, stored.AssignedDeviceIds);
            Assert.Equal(2, stored.CabinetAssignments!.Count);
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void Snapshot_DefaultFingerprint_DoesNotUseAnotherUsersMatchingLegacyId()
    {
        var user = new User
        {
            UserId = "S001",
            Role = "student",
            FingerprintId = 11
        };
        FingerprintTemplate[] templates =
        {
            new() { FingerprintId = 11, UserId = "S002", Enabled = true, FingerIndex = 1 },
            new() { FingerprintId = 12, UserId = "S001", Enabled = true, FingerIndex = 2 }
        };

        int? fingerprintId = new CabinetBindingService()
            .ResolveDefaultFingerprintId(user, templates);

        Assert.Equal(12, fingerprintId);
    }

    [Fact]
    public void FingerprintMetadata_RoundtripsFingerNameQualityAndEnabled()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            SaveTemplate(21, 6, "右手拇指", 86, false);

            FingerprintTemplate stored = BusinessDatabase.ReadFpTemplateMeta(21)!;
            Assert.Equal(6, stored.FingerIndex);
            Assert.Equal("右手拇指", stored.FingerName);
            Assert.Equal(86, stored.Quality);
            Assert.False(stored.Enabled);
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void SyncQueue_PersistsAndCoalescesLatestRequest()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            var service = new CabinetSyncQueueService();

            service.EnqueueUser("S001", new[] { "CAB_01", "cab_01" }, "首次同步");
            service.EnqueueUser("S001", new[] { "CAB_01" }, "权限已修改");

            CabinetSyncJob job = Assert.Single(service.GetAll());
            Assert.Equal("pending", job.State);
            Assert.Equal("权限已修改", job.Reason);
            Assert.Equal("CAB_01", job.DeviceId);
            Assert.Equal("S001", job.UserId);
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void FingerprintMetadata_BusinessTableRoundtripPreservesLocalTemplateBytes()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            SaveTemplate(31, 2, "左手食指", 90);
            byte[] before = BusinessDatabase.ReadFpTemplateBytes(31)!;

            JArray metadata = BusinessDatabase.ReadArray("fingerprints");
            Assert.Single(metadata);
            ((JObject)metadata[0]!)["finger_name"] = "左手食指（重录）";
            BusinessDatabase.ReplaceTable("fingerprints", metadata, 12);

            Assert.Equal("左手食指（重录）",
                BusinessDatabase.ReadFpTemplateMeta(31)!.FingerName);
            Assert.Equal(before, BusinessDatabase.ReadFpTemplateBytes(31));
            Assert.Equal(12u, BusinessDatabase.GetTableVersion("fingerprints"));
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    private static void SaveTemplate(
        int fingerprintId, int fingerIndex, string fingerName,
        int quality = 0, bool enabled = true)
    {
        BusinessDatabase.SaveFpTemplateWithMeta(
            fingerprintId, "S001", fingerIndex,
            Enumerable.Repeat((byte)fingerprintId, 512).ToArray(), "CAB_01");
        FingerprintTemplate meta = BusinessDatabase.ReadFpTemplateMeta(fingerprintId)!;
        meta.UserName = "测试学生";
        meta.FingerName = fingerName;
        meta.Quality = quality;
        meta.Enabled = enabled;
        BusinessDatabase.WriteFpTemplateMeta(meta);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(candidate); } catch { }
        }
    }
}
