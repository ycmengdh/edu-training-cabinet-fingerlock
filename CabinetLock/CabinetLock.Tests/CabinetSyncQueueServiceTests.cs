namespace CabinetLock.Tests;

public sealed class CabinetSyncQueueServiceTests : IDisposable
{
    private readonly string _originalPath = BusinessDatabase.ActiveDbPath;
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"fingerlock-queue-{Guid.NewGuid():N}.db");

    public CabinetSyncQueueServiceTests()
    {
        BusinessDatabase.SetActivePath(_tempPath);
        BusinessDatabase.Initialize();
    }

    [Fact]
    public void OfflineUserOperations_CollapseToLatestIntent()
    {
        var queue = new CabinetSyncQueueService();

        queue.EnqueueUser("U001", new[] { "CABINET_001" }, "upsert");
        queue.EnqueueUserDeletion("U001", new[] { "CABINET_001" }, "delete");

        CabinetSyncJob job = Assert.Single(queue.GetOpen());
        Assert.Equal("delete_user", job.JobKind);
        Assert.Equal("delete", job.Reason);

        queue.EnqueueUser("U001", new[] { "CABINET_001" }, "restore");
        job = Assert.Single(queue.GetOpen());
        Assert.Equal("user", job.JobKind);
        Assert.Equal("restore", job.Reason);
        Assert.Equal((1, 0), queue.CountOpenAndFailed());
    }

    [Fact]
    public void MaintenanceJobs_AreDurableAndDeduplicatedPerCabinet()
    {
        var queue = new CabinetSyncQueueService();

        queue.EnqueueMaintenance(
            new[] { "CABINET_001", "CABINET_001", "CABINET_002" }, "startup");
        queue.EnqueueMaintenance(new[] { "CABINET_001" }, "pin changed");

        CabinetSyncJob[] jobs = queue.GetOpen()
            .OrderBy(job => job.DeviceId)
            .ToArray();
        Assert.Equal(2, jobs.Length);
        Assert.All(jobs, job => Assert.Equal("maintenance", job.JobKind));
        Assert.Equal("pin changed", jobs[0].Reason);
        Assert.Equal("startup", jobs[1].Reason);
    }

    [Fact]
    public void QueueEntryPoints_RejectRootTargets()
    {
        var queue = new CabinetSyncQueueService();

        queue.EnqueueUser("admin", new[] { "ROOT_A001", "", "CABINET_001" }, "upsert");
        queue.EnqueueUserDeletion("admin", new[] { "root_a002" }, "delete");
        queue.EnqueueCabinet(" ROOT_A003 ", "cabinet");
        queue.EnqueueCabinet(" ", "blank");
        queue.EnqueueMaintenance(new[] { "ROOT_A004" }, "maintenance");

        CabinetSyncJob job = Assert.Single(queue.GetAll());
        Assert.Equal("CABINET_001", job.DeviceId);
        Assert.Equal((1, 0), queue.CountOpenAndFailed());
    }

    [Fact]
    public void RemoveInvalidRootJobs_DeletesHistoryAndKeepsCabinetJobs()
    {
        var queue = new CabinetSyncQueueService();
        queue.EnqueueCabinet("CABINET_001", "valid");
        using (Microsoft.Data.Sqlite.SqliteConnection connection = BusinessDatabase.Open())
        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"INSERT INTO cabinet_sync_queue(
job_key,job_kind,user_id,device_id,reason,state,attempt_count,update_time)
VALUES('USER:ROOT_A001:ADMIN','user','admin','ROOT_A001','history','pending',0,$now)";
            command.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
            command.ExecuteNonQuery();
        }

        Assert.Single(queue.GetAll());
        Assert.Equal((1, 0), queue.CountOpenAndFailed());
        Assert.Equal(1, queue.RemoveInvalidRootJobs());
        Assert.Equal("CABINET_001", Assert.Single(queue.GetAll()).DeviceId);
        Assert.Equal(0, queue.RemoveInvalidRootJobs());
    }

    [Fact]
    public void AutomaticPass_FinishesPermissionPhaseBeforeMaintenance()
    {
        DateTime now = DateTime.Now;
        var jobs = new[]
        {
            new CabinetSyncJob
            {
                JobKey = "user:ROOT_A001:ADMIN",
                JobKind = "user",
                UserId = "admin",
                DeviceId = "ROOT_A001",
                State = "pending",
                UpdateTime = now.AddMinutes(-3)
            },
            new CabinetSyncJob
            {
                JobKey = "maintenance:CABINET_001:",
                JobKind = "maintenance",
                DeviceId = "CABINET_001",
                State = "pending",
                UpdateTime = now.AddMinutes(-2)
            },
            new CabinetSyncJob
            {
                JobKey = "user:CABINET_002:ADMIN",
                JobKind = "user",
                UserId = "admin",
                DeviceId = "CABINET_002",
                State = "pending",
                UpdateTime = now.AddMinutes(-1)
            }
        };
        var online = new HashSet<string>(
            new[] { "ROOT_A001", "CABINET_001", "CABINET_002" },
            StringComparer.OrdinalIgnoreCase);

        CabinetSyncJob selected = Assert.Single(
            CabinetSyncQueueService.SelectAutomaticPass(jobs, online, now));

        Assert.Equal("user", selected.JobKind);
        Assert.Equal("CABINET_002", selected.DeviceId);
    }

    [Fact]
    public void AutomaticPass_SkipsOfflineAndBackoffJobs()
    {
        DateTime now = DateTime.Now;
        var jobs = new[]
        {
            new CabinetSyncJob
            {
                JobKey = "user:CABINET_OFFLINE:ADMIN",
                JobKind = "user",
                UserId = "admin",
                DeviceId = "CABINET_OFFLINE",
                State = "pending",
                UpdateTime = now.AddMinutes(-3)
            },
            new CabinetSyncJob
            {
                JobKey = "user:CABINET_001:ADMIN",
                JobKind = "user",
                UserId = "admin",
                DeviceId = "CABINET_001",
                State = "failed",
                NextAttemptTime = now.AddMinutes(1),
                UpdateTime = now.AddMinutes(-2)
            },
            new CabinetSyncJob
            {
                JobKey = "maintenance:CABINET_001:",
                JobKind = "maintenance",
                DeviceId = "CABINET_001",
                State = "pending",
                UpdateTime = now.AddMinutes(-1)
            }
        };
        var online = new HashSet<string>(
            new[] { "CABINET_001" }, StringComparer.OrdinalIgnoreCase);

        CabinetSyncJob selected = Assert.Single(
            CabinetSyncQueueService.SelectAutomaticPass(jobs, online, now));

        Assert.Equal("maintenance", selected.JobKind);
    }

    public void Dispose()
    {
        BusinessDatabase.SetActivePath(_originalPath);
        foreach (string path in new[] { _tempPath, _tempPath + "-wal", _tempPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
