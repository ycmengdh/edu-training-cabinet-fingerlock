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
