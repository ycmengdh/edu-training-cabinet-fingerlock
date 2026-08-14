using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

[Collection("Business database serial")]
public sealed class MultiFingerprintBindingTests
{
    [Fact]
    public void LegacyAssignment_WithNullFingerprintList_DoesNotCrashBindingRead()
    {
        var user = new User
        {
            UserId = "S001",
            Role = "student",
            CabinetAssignments = new List<CabinetAssignment>
            {
                new() { DeviceId = "CAB_01", FingerprintIds = null! }
            }
        };

        IReadOnlyList<int> selected = new CabinetBindingService()
            .GetSelectedFingerprintIds(user, "CAB_01", Array.Empty<FingerprintTemplate>());

        Assert.Empty(selected);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("teacher")]
    public void GlobalStaff_WithEmptyAssignment_UsesDefaultFingerprint(string role)
    {
        var user = new User
        {
            UserId = "STAFF_01",
            Role = role,
            CabinetAssignments = new List<CabinetAssignment>
            {
                new() { DeviceId = "CAB_01", FingerprintIds = new List<int>() }
            }
        };
        FingerprintTemplate[] templates =
        {
            new()
            {
                FingerprintId = 11,
                UserId = user.UserId,
                Enabled = true,
                FingerIndex = 1
            }
        };

        IReadOnlyList<int> selected = new CabinetBindingService()
            .GetSelectedFingerprintIds(user, "CAB_01", templates);

        Assert.Equal(new[] { 11 }, selected);
        Assert.Equal(new[] { "CAB_01", "CAB_02" },
            new CabinetBindingService().GetAssignedDeviceIds(
                user, new[] { "CAB_01", "CAB_02" }).OrderBy(id => id));
    }

    [Fact]
    public void Student_WithEmptyAssignment_DoesNotUseDefaultFingerprint()
    {
        var user = new User
        {
            UserId = "S001",
            Role = "student",
            CabinetAssignments = new List<CabinetAssignment>
            {
                new() { DeviceId = "CAB_01", FingerprintIds = new List<int>() }
            }
        };
        FingerprintTemplate[] templates =
        {
            new() { FingerprintId = 11, UserId = user.UserId, Enabled = true }
        };

        IReadOnlyList<int> selected = new CabinetBindingService()
            .GetSelectedFingerprintIds(user, "CAB_01", templates);

        Assert.Empty(selected);
    }

    [Fact]
    public void SetSelectedFingerprints_PersistsMultipleFingerprintsPerCabinet()
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
                {"device_id":"CAB_01","fingerprint_ids":[11],"update_time":"2026-07-28T08:00:00+08:00"}
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
            Assert.True(service.SetSelectedFingerprints("S001", "CAB_01", new[] { 11, 12 }));

            User stored = BusinessDatabase.ReadArray("users").Single().ToObject<User>()!;
            Assert.Single(stored.CabinetAssignments!);
            Assert.Equal(new[] { 11, 12 }, stored.CabinetAssignments![0].FingerprintIds);
            Assert.Equal(new[] { 11, 12 }, service.GetSelectedFingerprintIds(stored, "CAB_01"));
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
            Assert.True(service.SetSelectedFingerprints("S001", "CAB_02", new[] { 12 }));

            User stored = BusinessDatabase.ReadArray("users").Single().ToObject<User>()!;
            Assert.Equal(new[] { 11 }, service.GetSelectedFingerprintIds(stored, "CAB_01"));
            Assert.Equal(new[] { 12 }, service.GetSelectedFingerprintIds(stored, "CAB_02"));
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
    public void AssignmentConfiguration_KeepsLockPermissionsIndependentPerCabinet()
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
              "assigned_device_ids":[],"fingerprint_id":11,
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
            Assert.True(service.SetAssignmentConfiguration("S001", "CAB_01", new[] { 11 }, new[] { 1 }));
            Assert.True(service.SetAssignmentConfiguration("S001", "CAB_02", new[] { 12 }, new[] { 2, 3 }));

            User stored = BusinessDatabase.ReadArray("users").Single().ToObject<User>()!;
            Assert.Equal(new[] { false, true, false, false },
                service.GetLockPermissions(stored, "CAB_01", new bool[4]));
            Assert.Equal(new[] { false, false, true, true },
                service.GetLockPermissions(stored, "CAB_02", new bool[4]));
            Assert.Equal(new[] { 11 }, service.GetSelectedFingerprintIds(stored, "CAB_01"));
            Assert.Equal(new[] { 12 }, service.GetSelectedFingerprintIds(stored, "CAB_02"));
        }
        finally
        {
            App.CurrentUser = originalUser;
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void RemoveDeviceAssignments_OnlyRemovesStudentsFromTheDeletedCabinet()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        User? originalUser = App.CurrentUser;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.Parse("""
            [
              {
                "user_id":"S001","user_code":"001","name":"学生一","role":"student",
                "assigned_device_ids":["CAB_01","CAB_02"],
                "cabinet_assignments":[
                  {"device_id":"CAB_01","fingerprint_ids":[11]},
                  {"device_id":"CAB_02","fingerprint_ids":[12]}
                ]
              },
              {
                "user_id":"S002","user_code":"002","name":"学生二","role":"student",
                "assigned_device_ids":["CAB_01"],
                "cabinet_assignments":[{"device_id":"CAB_01","fingerprint_ids":[21]}]
              },
              {"user_id":"T001","name":"教师","role":"teacher"},
              {"user_id":"A001","name":"管理员","role":"admin"}
            ]
            """), 1);
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
            [
              {"device_id":"CAB_01","device_name":"一号柜","is_root":false},
              {"device_id":"CAB_02","device_name":"二号柜","is_root":false}
            ]
            """), 1);
            App.CurrentUser = new User { UserId = "admin", Role = "admin" };

            var service = new CabinetBindingService();
            Assert.Equal(new[] { "S001", "S002" }, service.GetAssignedStudents("cab_01")
                .Select(user => user.UserId).OrderBy(userId => userId));

            Assert.True(service.RemoveDeviceAssignments("CAB_01", out int affected));
            Assert.Equal(2, affected);

            Dictionary<string, User> stored = BusinessDatabase.ReadArray("users")
                .Select(item => item.ToObject<User>()!)
                .ToDictionary(user => user.UserId);
            Assert.Equal(new[] { "CAB_02" }, stored["S001"].AssignedDeviceIds);
            Assert.Empty(stored["S002"].AssignedDeviceIds!);
            Assert.Null(stored["T001"].CabinetAssignments);
            Assert.Null(stored["A001"].CabinetAssignments);
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
    public void GlobalStaffSyncQueue_CoversEveryCabinetAndSkipsUsersWithoutFingerprint()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.Parse("""
            [
              {"user_id":"A001","name":"管理员","role":"admin","enabled":true},
              {"user_id":"T001","name":"教师","role":"teacher","enabled":true},
              {"user_id":"S001","name":"学生","role":"student","enabled":true}
            ]
            """), 1);
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
            [
              {"device_id":"CAB_01","device_name":"一号柜","is_root":false},
              {"device_id":"CAB_02","device_name":"二号柜","is_root":false},
              {"device_id":"ROOT_01","device_name":"根节点","is_root":true}
            ]
            """), 1);
            SaveTemplate(11, 1, "左手拇指", userId: "A001");
            SaveTemplate(12, 1, "左手拇指", userId: "S001");

            var service = new FingerprintTemplateService(App.UserService);

            Assert.Equal(2, service.EnsureGlobalStaffSyncQueued());
            CabinetSyncJob[] jobs = new CabinetSyncQueueService().GetAll().ToArray();
            Assert.Equal(new[] { "CAB_01", "CAB_02" },
                jobs.Select(job => job.DeviceId).OrderBy(id => id));
            Assert.All(jobs, job => Assert.Equal("A001", job.UserId));
            Assert.Equal(0, service.EnsureGlobalStaffSyncQueued());
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    [Fact]
    public void CabinetSnapshot_IncludesGlobalStaffWhenStoredAssignmentIsEmpty()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.Parse("""
            [
              {
                "user_id":"A001","name":"管理员","role":"admin","enabled":true,
                "cabinet_assignments":[{"device_id":"CAB_01","fingerprint_ids":[]}]
              },
              {
                "user_id":"S001","name":"学生","role":"student","enabled":true,
                "cabinet_assignments":[{"device_id":"CAB_01","fingerprint_ids":[12]}]
              }
            ]
            """), 1);
            BusinessDatabase.ReplaceTable("devices", JArray.Parse("""
              [{"device_id":"CAB_01","device_name":"一号柜","is_root":false}]
            """), 1);
            SaveTemplate(11, 1, "左手拇指", userId: "A001");
            SaveTemplate(12, 1, "左手拇指", userId: "S001");

            IReadOnlyDictionary<string, CabinetExpectedSyncState> states =
                new CabinetSyncService().GetExpectedCabinetSyncStates(new[] { "CAB_01" });

            Assert.Equal(2, states["CAB_01"].ExpectedFingerprintCount);
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
        int quality = 0, bool enabled = true, string userId = "S001")
    {
        BusinessDatabase.SaveFpTemplateWithMeta(
            fingerprintId, userId, fingerIndex,
            Enumerable.Repeat((byte)fingerprintId, 512).ToArray(), "CAB_01");
        FingerprintTemplate meta = BusinessDatabase.ReadFpTemplateMeta(fingerprintId)!;
        meta.UserName = userId == "S001" ? "测试学生" : userId;
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
