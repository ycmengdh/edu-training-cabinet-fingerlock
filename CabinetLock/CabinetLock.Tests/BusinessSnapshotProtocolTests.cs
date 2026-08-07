using System.Diagnostics;
using Xunit.Abstractions;

namespace CabinetLock.Tests;

[Collection("Business database serial")]
public class BusinessSnapshotProtocolTests
{
    private readonly ITestOutputHelper _output;

    public BusinessSnapshotProtocolTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SnapshotCommands_PreserveRawBinaryPayload()
    {
        byte[] payload = { 0x01, 0x00, 0xFE, 0xFF, 0x7B, 0x00 };
        Message message = Message.Create(Protocol.CmdSdSnapshotChunk,
            "ROOT_001", payload);

        AppMessage app = AppMessageMapper.ToApp(message);
        Message decoded = AppMessageMapper.ToMessage(app);

        Assert.Equal((ushort)0x004B, app.CmdId);
        Assert.Equal(payload, app.Payload);
        Assert.Equal(payload, Assert.IsType<byte[]>(decoded.Data));
    }

    [Fact]
    public void SnapshotCodec_RoundTripsAndDetectsCorruption()
    {
        var tables = EmptyTables();
        tables["users"].Add(new Newtonsoft.Json.Linq.JObject
        {
            ["user_id"] = "teacher_1",
            ["name"] = "Teacher",
            ["role"] = "teacher",
            ["enabled"] = true
        });
        var versions = BusinessDatabase.DailySyncTables.ToDictionary(
            table => table, _ => 7U);

        BusinessSnapshot created = BusinessSnapshotCodec.Create(tables,
            versions, generation: 42);
        BusinessSnapshot decoded = BusinessSnapshotCodec.Decode(
            created.ToContainerBytes());
        BusinessSnapshotPackage package = BusinessSnapshotCodec.ReadPackage(decoded);

        Assert.Equal(BusinessSnapshotCodec.HeaderSize, created.Header.Length);
        Assert.Equal((uint)42, decoded.Generation);
        Assert.Single(package.Tables["users"]);
        Assert.Equal((uint)7, package.Versions["permissions"]);

        byte[] corrupt = created.ToContainerBytes();
        corrupt[^1] ^= 0x80;
        Assert.Throws<InvalidDataException>(() =>
            BusinessSnapshotCodec.Decode(corrupt));
    }

    [Fact]
    public void CapacityFixture_ProducesSubMegabyteDailySnapshot()
    {
        var tables = EmptyTables();
        tables["users"].Add(Newtonsoft.Json.Linq.JObject.FromObject(
            SystemAdministratorPolicy.CreateDefault(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local))));
        for (int classIndex = 0; classIndex < 180; classIndex++)
        {
            string classId = $"CLASS_{classIndex:D3}";
            tables["classes"].Add(new Newtonsoft.Json.Linq.JObject
            {
                ["class_id"] = classId,
                ["name"] = $"Class {classIndex:D3}",
                ["enabled"] = true,
                ["create_time"] = "2026-01-01T00:00:00+08:00"
            });
            for (int student = 0; student < 40; student++)
            {
                string userId = $"STU_{classIndex:D3}_{student:D2}";
                tables["users"].Add(User(userId, classId, "student"));
                for (int lockId = 0; lockId < 4; lockId++)
                {
                    tables["permissions"].Add(new Newtonsoft.Json.Linq.JObject
                    {
                        ["user_id"] = userId,
                        ["lock_id"] = lockId,
                        ["has_access"] = lockId == student % 4,
                        ["update_time"] = "2026-01-01T00:00:00+08:00"
                    });
                }
            }
        }
        for (int teacher = 0; teacher < 20; teacher++)
        {
            string userId = $"TEACHER_{teacher:D2}";
            tables["users"].Add(User(userId, null, "teacher"));
            for (int lockId = 0; lockId < 4; lockId++)
            {
                tables["permissions"].Add(new Newtonsoft.Json.Linq.JObject
                {
                    ["user_id"] = userId,
                    ["lock_id"] = lockId,
                    ["has_access"] = true,
                    ["update_time"] = "2026-01-01T00:00:00+08:00"
                });
            }
        }
        for (int device = 0; device < 40; device++)
        {
            tables["devices"].Add(new Newtonsoft.Json.Linq.JObject
            {
                ["device_id"] = $"CAB_{device:D3}",
                ["device_name"] = $"Cabinet {device:D3}",
                ["online"] = false,
                ["is_root"] = false
            });
        }

        var watch = Stopwatch.StartNew();
        BusinessSnapshot snapshot = BusinessSnapshotCodec.Create(tables);
        watch.Stop();

        var decodeWatch = Stopwatch.StartNew();
        BusinessSnapshotPackage package = BusinessSnapshotCodec.ReadPackage(
            BusinessSnapshotCodec.Decode(snapshot.ToContainerBytes()));
        decodeWatch.Stop();

        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(),
            $"snapshot-capacity-{Guid.NewGuid():N}.db");
        var importWatch = new Stopwatch();
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            importWatch.Start();
            BusinessDatabase.ReplaceBusinessSnapshot(package.Tables,
                package.Versions);
            importWatch.Stop();
            Assert.Equal(7221, BusinessDatabase.ReadArray("users").Count);
            Assert.Equal(28880, BusinessDatabase.ReadArray("permissions").Count);
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            foreach (string path in new[]
                { tempPath, tempPath + "-wal", tempPath + "-shm" })
            {
                try { File.Delete(path); } catch { }
            }
        }

        _output.WriteLine(
            $"raw={snapshot.RawPayload.Length} compressed={snapshot.CompressedPayload.Length} " +
            $"encode_ms={watch.ElapsedMilliseconds} decode_ms={decodeWatch.ElapsedMilliseconds} " +
            $"import_ms={importWatch.ElapsedMilliseconds}");

        Assert.Equal(7221, tables["users"].Count);
        Assert.Equal(28880, tables["permissions"].Count);
        Assert.True(snapshot.CompressedPayload.Length < 1024 * 1024,
            $"Compressed size was {snapshot.CompressedPayload.Length} bytes");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"Host serialization took {watch.Elapsed}");
    }

    [Fact]
    public void CurrentRootFirmware_HandlesSnapshotBeforeJsonParsing()
    {
        string controller = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "esp32", "root_node", "components", "controller", "root_controller.c")));
        string protocol = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "esp32", "common_components", "cabinet_protocol", "include",
            "cabinet_protocol.h")));
        string storage = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "esp32", "root_node", "components", "storage", "root_storage.c")));

        Assert.Contains("CAB_CMD_SD_SNAPSHOT_MANIFEST = 0x0048", protocol);
        Assert.Contains("CAB_CMD_SD_SNAPSHOT_DOWNLOAD_PART = 0x004F", protocol);
        int dispatch = controller.IndexOf("case CAB_CMD_SD_SNAPSHOT_MANIFEST:",
            StringComparison.Ordinal);
        int parse = controller.IndexOf("cJSON *json = parse_json(request);",
            StringComparison.Ordinal);
        Assert.True(dispatch >= 0 && parse > dispatch);
        Assert.Contains("business.snapshot.gz.upload", storage);
        Assert.Contains("PSA_ALG_SHA_256", storage);
    }

    [Fact]
    public void SnapshotImport_IsAtomicAndKeepsFingerprintLibrary()
    {
        string originalPath = BusinessDatabase.ActiveDbPath;
        string tempPath = Path.Combine(Path.GetTempPath(),
            $"snapshot-{Guid.NewGuid():N}.db");
        try
        {
            BusinessDatabase.SetActivePath(tempPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.ReplaceTable("users",
                Newtonsoft.Json.Linq.JArray.Parse(
                    "[{\"user_id\":\"old\",\"name\":\"Old\",\"role\":\"student\",\"enabled\":true}]"), 1);
            byte[] fingerprint = Enumerable.Repeat((byte)0x5A, 512).ToArray();
            BusinessDatabase.SaveFpTemplateWithMeta(9, "old", 1,
                fingerprint, "CAB_01");

            var tables = EmptyTables();
            tables["users"].Add(new Newtonsoft.Json.Linq.JObject
            {
                ["user_id"] = "new",
                ["name"] = "New",
                ["role"] = "student",
                ["enabled"] = true
            });
            var duplicatePermission = new Newtonsoft.Json.Linq.JObject
            {
                ["user_id"] = "new",
                ["lock_id"] = 1,
                ["has_access"] = true
            };
            tables["permissions"].Add(duplicatePermission);
            tables["permissions"].Add(duplicatePermission.DeepClone());
            var versions = BusinessDatabase.DailySyncTables.ToDictionary(
                table => table, _ => 11U);

            Assert.ThrowsAny<Exception>(() =>
                BusinessDatabase.ReplaceBusinessSnapshot(tables, versions));
            Assert.Equal("old", BusinessDatabase.ReadArray("users")
                .Single().Value<string>("user_id"));
            Assert.Equal(fingerprint, BusinessDatabase.ReadFpTemplateBytes(9));

            tables["permissions"].RemoveAt(1);
            BusinessDatabase.ReplaceBusinessSnapshot(tables, versions);
            Newtonsoft.Json.Linq.JArray importedUsers =
                BusinessDatabase.ReadArray("users");
            Assert.Contains(importedUsers.OfType<Newtonsoft.Json.Linq.JObject>(),
                user => user.Value<string>("user_id") == "new");
            Assert.Contains(importedUsers.OfType<Newtonsoft.Json.Linq.JObject>(),
                user => user.Value<string>("user_id") ==
                    SystemAdministratorPolicy.UserId);
            Assert.Equal((uint)11, BusinessDatabase.GetTableVersion("devices"));
            Assert.Equal(fingerprint, BusinessDatabase.ReadFpTemplateBytes(9));
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalPath);
            foreach (string path in new[]
                { tempPath, tempPath + "-wal", tempPath + "-shm" })
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    private static Dictionary<string, Newtonsoft.Json.Linq.JArray> EmptyTables() =>
        BusinessDatabase.DailySyncTables.ToDictionary(
            table => table,
            _ => new Newtonsoft.Json.Linq.JArray(),
            StringComparer.OrdinalIgnoreCase);

    private static Newtonsoft.Json.Linq.JObject User(
        string userId, string? classId, string role) => new()
    {
        ["user_id"] = userId,
        ["user_code"] = userId,
        ["name"] = userId,
        ["gender"] = "",
        ["role"] = role,
        ["class_id"] = classId,
        ["fingerprint_id"] = null,
        ["password_salt"] = "",
        ["password_hash"] = "",
        ["enabled"] = true,
        ["create_time"] = "2026-01-01T00:00:00+08:00"
    };

    private static string FindRepositoryFile(string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        string sourceCandidate = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? "", "..", "..", relativePath));
        if (File.Exists(sourceCandidate)) return sourceCandidate;

        string currentCandidate = Path.Combine(
            Directory.GetCurrentDirectory(), relativePath);
        if (File.Exists(currentCandidate)) return currentCandidate;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }
}
