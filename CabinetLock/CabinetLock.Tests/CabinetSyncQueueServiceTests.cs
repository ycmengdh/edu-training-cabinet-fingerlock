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
