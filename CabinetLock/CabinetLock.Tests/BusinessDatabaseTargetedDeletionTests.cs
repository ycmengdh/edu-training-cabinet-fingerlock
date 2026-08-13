using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

[Collection("Business database serial")]
public sealed class BusinessDatabaseTargetedDeletionTests
{
    [Fact]
    public void DeleteUserAndPermissions_RemovesOnlyRequestedUserAndBumpsVersions()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"fingerlock-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users", JArray.Parse("""
            [
              {"user_id":"S001","name":"学生一","role":"student","enabled":true,"create_time":"2026-08-12T08:00:00+08:00"},
              {"user_id":"S002","name":"学生二","role":"student","enabled":true,"create_time":"2026-08-12T08:00:00+08:00"}
            ]
            """), 7);
            BusinessDatabase.ReplaceTable("permissions", JArray.Parse("""
            [
              {"user_id":"S001","lock_id":0,"has_access":true,"update_time":"2026-08-12T08:00:00+08:00"},
              {"user_id":"S002","lock_id":1,"has_access":true,"update_time":"2026-08-12T08:00:00+08:00"}
            ]
            """), 11);

            Assert.True(BusinessDatabase.DeleteUserAndPermissions("s001"));

            Assert.Null(BusinessDatabase.ReadUser("S001"));
            Assert.NotNull(BusinessDatabase.ReadUser("S002"));
            Assert.Empty(BusinessDatabase.ReadUserPermissions("S001"));
            Assert.Single(BusinessDatabase.ReadUserPermissions("S002"));
            Assert.Equal(8u, BusinessDatabase.GetTableVersion("users"));
            Assert.Equal(12u, BusinessDatabase.GetTableVersion("permissions"));
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            DeleteDatabaseFiles(tempPath);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}
