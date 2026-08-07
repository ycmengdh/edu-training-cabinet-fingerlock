using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text.Json;
using CabinetLock;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private const int ClassCount = 180;
    private const int StudentsPerClass = 40;
    private const int TeacherCount = 20;
    private const int CabinetCount = 40;
    private const int LockCount = 4;

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Options.PrintUsage();
            return 2;
        }

        string repositoryRoot = FindRepositoryRoot();
        string runDirectory = Path.Combine(repositoryRoot, ".artifacts",
            "business-snapshot-hardware", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDirectory);

        var report = new BenchmarkReport
        {
            StartedAt = DateTimeOffset.Now,
            RunDirectory = runDirectory,
            Port = options.Port,
            RootId = options.RootId,
            BaudRate = options.BaudRate,
            SnapshotChunkSize = options.ChunkSize,
            AckWindow = options.AckWindow,
            HostWriteSize = options.WriteSize,
            HostWriteDelayMs = options.WriteDelayMs
        };

        string originalDbPath = BusinessDatabase.ActiveDbPath;
        try
        {
            Console.WriteLine($"Artifacts: {runDirectory}");
            BusinessSnapshot benchmarkSnapshot = BuildLocalFixture(runDirectory, report);

            if (options.LocalOnly)
            {
                report.CompletedAt = DateTimeOffset.Now;
                report.Success = true;
                WriteReport(runDirectory, report);
                Console.WriteLine("Local benchmark complete; hardware was not modified.");
                return 0;
            }

            await RunHardwareBenchmark(options, runDirectory, benchmarkSnapshot, report);
            report.CompletedAt = DateTimeOffset.Now;
            report.Success = true;
            WriteReport(runDirectory, report);
            PrintSummary(report);
            return 0;
        }
        catch (Exception ex)
        {
            report.CompletedAt = DateTimeOffset.Now;
            report.Success = false;
            report.Error = ex.ToString();
            WriteReport(runDirectory, report);
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            Console.Error.WriteLine($"Report: {Path.Combine(runDirectory, "report.json")}");
            return 1;
        }
        finally
        {
            BusinessDatabase.SetActivePath(originalDbPath);
        }
    }

    private static BusinessSnapshot BuildLocalFixture(
        string runDirectory, BenchmarkReport report)
    {
        Console.WriteLine("Generating full-capacity business fixture...");
        var watch = Stopwatch.StartNew();
        Dictionary<string, JArray> tables = CreateFixture();
        watch.Stop();
        report.FixtureGenerationMs = watch.Elapsed.TotalMilliseconds;
        report.Classes = tables["classes"].Count;
        report.Users = tables["users"].Count;
        report.Students = ClassCount * StudentsPerClass;
        report.Teachers = TeacherCount;
        report.Devices = tables["devices"].Count;
        report.Permissions = tables["permissions"].Count;

        ValidateCounts(tables);
        var versions = BusinessDatabase.DailySyncTables.ToDictionary(
            table => table, _ => 1U, StringComparer.OrdinalIgnoreCase);

        string databasePath = Path.Combine(runDirectory, "business.db");
        BusinessDatabase.SetActivePath(databasePath);
        BusinessDatabase.Initialize();
        watch.Restart();
        BusinessDatabase.ReplaceBusinessSnapshot(tables, versions);
        BusinessDatabase.Checkpoint();
        watch.Stop();
        report.DatabaseImportMs = watch.Elapsed.TotalMilliseconds;
        report.DatabaseBytes = new FileInfo(databasePath).Length;

        Dictionary<string, int> importedCounts = BusinessDatabase.DailySyncTables
            .ToDictionary(table => table, table => BusinessDatabase.ReadArray(table).Count,
                StringComparer.OrdinalIgnoreCase);
        if (importedCounts["classes"] != ClassCount ||
            importedCounts["users"] != ClassCount * StudentsPerClass + TeacherCount + 1 ||
            importedCounts["permissions"] !=
                (ClassCount * StudentsPerClass + TeacherCount) * LockCount ||
            importedCounts["devices"] != CabinetCount)
        {
            throw new InvalidDataException("SQLite fixture row counts changed during import");
        }

        watch.Restart();
        BusinessSnapshot snapshot = BusinessSnapshotCodec.CreateFromDatabase(generation: 1);
        watch.Stop();
        report.SnapshotEncodeMs = watch.Elapsed.TotalMilliseconds;
        report.RawSnapshotBytes = snapshot.RawPayload.Length;
        report.CompressedSnapshotBytes = snapshot.CompressedPayload.Length;
        report.ContainerBytes = snapshot.ContainerSize;
        report.ContentSha256 = Convert.ToHexString(snapshot.ContentSha256).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(runDirectory, "business.snapshot.gz"),
            snapshot.ToContainerBytes());

        watch.Restart();
        BusinessSnapshotPackage package = BusinessSnapshotCodec.ReadPackage(
            BusinessSnapshotCodec.Decode(snapshot.ToContainerBytes()));
        watch.Stop();
        report.LocalDecodeMs = watch.Elapsed.TotalMilliseconds;

        string restoredPath = Path.Combine(runDirectory, "restored.db");
        BusinessDatabase.SetActivePath(restoredPath);
        BusinessDatabase.Initialize();
        watch.Restart();
        BusinessDatabase.ReplaceBusinessSnapshot(package.Tables, package.Versions);
        BusinessDatabase.Checkpoint();
        watch.Stop();
        report.LocalRestoreMs = watch.Elapsed.TotalMilliseconds;
        report.RestoredDatabaseBytes = new FileInfo(restoredPath).Length;
        if (BusinessDatabase.ReadArray("users").Count != report.Users ||
            BusinessDatabase.ReadArray("permissions").Count != report.Permissions)
        {
            throw new InvalidDataException("Restored SQLite fixture row counts do not match");
        }

        Console.WriteLine(
            $"Fixture: {report.Classes} classes, {report.Students} students, " +
            $"{report.Teachers} teachers, {report.Devices} cabinets, " +
            $"{report.Permissions} permissions");
        Console.WriteLine(
            $"Local: DB={FormatBytes(report.DatabaseBytes)}, " +
            $"raw={FormatBytes(report.RawSnapshotBytes)}, " +
            $"gzip={FormatBytes(report.CompressedSnapshotBytes)}, " +
            $"import={report.DatabaseImportMs:F0} ms, encode={report.SnapshotEncodeMs:F0} ms");
        return snapshot;
    }

    private static async Task RunHardwareBenchmark(
        Options options, string runDirectory, BusinessSnapshot benchmarkSnapshot,
        BenchmarkReport report)
    {
        using var client = new SnapshotSerialClient(options);
        Console.WriteLine($"Opening {options.Port} (configured {options.BaudRate} baud)...");
        client.Open();

        BusinessSnapshot? originalSnapshot = null;
        bool benchmarkPromotionAttempted = false;
        bool restoreRequired = false;
        Exception? benchmarkFailure = null;
        try
        {
            var watch = Stopwatch.StartNew();
            SnapshotManifest originalManifest = client.QueryManifest();
            watch.Stop();
            report.InitialManifestMs = watch.Elapsed.TotalMilliseconds;
            report.RootHadOriginalSnapshot = originalManifest.Exists;

            if (originalManifest.Exists)
            {
                Console.WriteLine("Preserving the root node's current snapshot...");
                watch.Restart();
                byte[] originalContainer = client.DownloadSnapshot(originalManifest);
                watch.Stop();
                report.OriginalDownloadMs = watch.Elapsed.TotalMilliseconds;
                originalSnapshot = BusinessSnapshotCodec.Decode(originalContainer);
                if (!CryptographicOperations.FixedTimeEquals(
                        originalSnapshot.ContentSha256, originalManifest.ContentSha256))
                {
                    throw new InvalidDataException(
                        "Root snapshot download hash does not match its manifest; hardware test aborted");
                }
                File.WriteAllBytes(Path.Combine(runDirectory, "root-before.snapshot.gz"),
                    originalContainer);
                Console.WriteLine(
                    $"Root snapshot preserved: {FormatBytes(originalContainer.Length)} " +
                    $"in {report.OriginalDownloadMs:F0} ms");
            }
            else
            {
                Console.WriteLine(
                    "WARNING: root has no existing snapshot; the benchmark snapshot cannot be deleted " +
                    "with the current protocol and will remain after this run.");
            }

            benchmarkPromotionAttempted = true;
            restoreRequired = originalSnapshot != null && !options.KeepBenchmark;
            if (originalSnapshot != null && options.KeepBenchmark)
            {
                Console.WriteLine(
                    "The existing root snapshot was archived in this run; " +
                    "--keep-benchmark will leave the generated fixture active.");
            }
            long sentBefore = client.WireBytesSent;
            long receivedBefore = client.WireBytesReceived;
            Console.WriteLine("Uploading benchmark snapshot with current production pacing...");
            SnapshotUploadMetrics upload = client.UploadSnapshot(benchmarkSnapshot,
                (done, total) => PrintProgress("Upload", done, total));
            report.UploadMs = upload.Elapsed.TotalMilliseconds;
            report.UploadAckWaitMs = upload.AckWait.TotalMilliseconds;
            report.UploadAckCount = upload.AckCount;
            report.UploadResumeCount = upload.ResumeCount;
            report.UploadCommitMs = upload.CommitElapsed.TotalMilliseconds;
            report.UploadWireBytes = client.WireBytesSent - sentBefore;
            Console.WriteLine();

            SnapshotManifest committed = client.QueryManifest();
            if (!committed.Exists || !CryptographicOperations.FixedTimeEquals(
                    committed.ContentSha256, benchmarkSnapshot.ContentSha256))
            {
                throw new InvalidDataException("Committed root manifest does not match benchmark snapshot");
            }

            Console.WriteLine("Downloading the committed snapshot for wire and hash verification...");
            sentBefore = client.WireBytesSent;
            receivedBefore = client.WireBytesReceived;
            watch.Restart();
            byte[] downloaded = client.DownloadSnapshot(committed,
                (done, total) => PrintProgress("Download", done, total));
            watch.Stop();
            Console.WriteLine();
            report.DownloadMs = watch.Elapsed.TotalMilliseconds;
            report.DownloadWireBytesSent = client.WireBytesSent - sentBefore;
            report.DownloadWireBytesReceived = client.WireBytesReceived - receivedBefore;
            report.DownloadedContainerBytes = downloaded.Length;
            File.WriteAllBytes(Path.Combine(runDirectory, "root-readback.snapshot.gz"), downloaded);

            byte[] expectedContainer = benchmarkSnapshot.ToContainerBytes();
            if (!downloaded.AsSpan().SequenceEqual(expectedContainer))
                throw new InvalidDataException("Root read-back differs byte-for-byte from uploaded snapshot");

            watch.Restart();
            BusinessSnapshotPackage downloadedPackage = BusinessSnapshotCodec.ReadPackage(
                BusinessSnapshotCodec.Decode(downloaded));
            watch.Stop();
            report.HardwareReadbackDecodeMs = watch.Elapsed.TotalMilliseconds;

            string hardwareRestoredPath = Path.Combine(runDirectory, "hardware-restored.db");
            BusinessDatabase.SetActivePath(hardwareRestoredPath);
            BusinessDatabase.Initialize();
            watch.Restart();
            BusinessDatabase.ReplaceBusinessSnapshot(downloadedPackage.Tables,
                downloadedPackage.Versions);
            BusinessDatabase.Checkpoint();
            watch.Stop();
            report.HardwareReadbackRestoreMs = watch.Elapsed.TotalMilliseconds;
            if (BusinessDatabase.ReadArray("users").Count != report.Users ||
                BusinessDatabase.ReadArray("permissions").Count != report.Permissions)
            {
                throw new InvalidDataException("Hardware read-back database row counts do not match");
            }
        }
        catch (Exception ex)
        {
            benchmarkFailure = ex;
            throw;
        }
        finally
        {
            report.UnframedReceiveBytes = client.UnframedReceiveBytes;
            report.InvalidApplicationMessages = client.InvalidApplicationMessages;
            if (benchmarkPromotionAttempted && restoreRequired && originalSnapshot != null)
            {
                Console.WriteLine("Restoring the root node's original snapshot...");
                try
                {
                    Stopwatch restoreWatch = Stopwatch.StartNew();
                    client.UploadSnapshot(originalSnapshot,
                        (done, total) => PrintProgress("Restore", done, total));
                    restoreWatch.Stop();
                    Console.WriteLine();
                    SnapshotManifest restored = client.QueryManifest();
                    if (!restored.Exists || !CryptographicOperations.FixedTimeEquals(
                            restored.ContentSha256, originalSnapshot.ContentSha256))
                    {
                        throw new InvalidDataException(
                            "Root original snapshot restore manifest verification failed");
                    }
                    report.OriginalRestoreMs = restoreWatch.Elapsed.TotalMilliseconds;
                    report.OriginalSnapshotRestored = true;
                    Console.WriteLine(
                        $"Original root snapshot restored and verified in " +
                        $"{report.OriginalRestoreMs:F0} ms.");
                }
                catch (Exception restoreError)
                {
                    report.OriginalSnapshotRestored = false;
                    report.RestoreError = restoreError.ToString();
                    if (benchmarkFailure == null)
                    {
                        throw new InvalidOperationException(
                            "Benchmark completed, but restoring the original root snapshot failed",
                            restoreError);
                    }
                    Console.Error.WriteLine(
                        $"CRITICAL: original root snapshot restore failed: {restoreError.Message}");
                }
            }
        }

        await Task.CompletedTask;
    }

    private static Dictionary<string, JArray> CreateFixture()
    {
        Dictionary<string, JArray> tables = BusinessDatabase.DailySyncTables.ToDictionary(
            table => table, _ => new JArray(), StringComparer.OrdinalIgnoreCase);
        const string timestamp = "2026-01-01T00:00:00+08:00";
        string[] cabinetIds = Enumerable.Range(0, CabinetCount)
            .Select(index => $"CAB_{index:D3}").ToArray();

        tables["users"].Add(JObject.FromObject(
            SystemAdministratorPolicy.CreateDefault(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local))));

        for (int device = 0; device < CabinetCount; device++)
        {
            tables["devices"].Add(new JObject
            {
                ["device_id"] = cabinetIds[device],
                ["device_name"] = $"Training Cabinet {device + 1:D2}",
                ["device_number"] = (device + 1).ToString("D3"),
                ["ip_address"] = "",
                ["online"] = false,
                ["register_time"] = timestamp,
                ["last_online_time"] = null,
                ["last_seen"] = 0,
                ["offline_time"] = 0,
                ["mesh_mac"] = $"02:00:00:00:00:{device + 1:X2}",
                ["is_root"] = false,
                ["firmware_version"] = "benchmark",
                ["hardware_version"] = "ESP32-S3",
                ["status"] = new JObject()
            });
        }

        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            string classId = $"CLASS_{classIndex + 1:D3}";
            string cabinetId = cabinetIds[classIndex % CabinetCount];
            tables["classes"].Add(new JObject
            {
                ["class_id"] = classId,
                ["name"] = $"Training Class {classIndex + 1:D3}",
                ["enabled"] = true,
                ["create_time"] = timestamp
            });

            for (int student = 0; student < StudentsPerClass; student++)
            {
                string userId = $"STU_{classIndex + 1:D3}_{student + 1:D2}";
                int assignedLock = student % LockCount;
                JObject user = CreateUser(userId,
                    $"Student {classIndex + 1:D3}-{student + 1:D2}", "student", timestamp);
                user["class_id"] = classId;
                user["class_ids"] = null;
                user["assigned_device_ids"] = new JArray(cabinetId);
                user["cabinet_assignments"] = new JArray(new JObject
                {
                    ["device_id"] = cabinetId,
                    ["fingerprint_ids"] = new JArray(),
                    ["lock_ids"] = new JArray(assignedLock),
                    ["update_time"] = timestamp
                });
                tables["users"].Add(user);
                AddPermissions(tables["permissions"], userId,
                    lockId => lockId == assignedLock, timestamp);
            }
        }

        for (int teacher = 0; teacher < TeacherCount; teacher++)
        {
            string userId = $"TEACHER_{teacher + 1:D2}";
            JObject user = CreateUser(userId, $"Teacher {teacher + 1:D2}",
                "teacher", timestamp);
            user["class_id"] = null;
            user["class_ids"] = new JArray(Enumerable.Range(0, ClassCount)
                .Where(index => index % TeacherCount == teacher)
                .Select(index => $"CLASS_{index + 1:D3}"));
            user["assigned_device_ids"] = new JArray(cabinetIds);
            user["cabinet_assignments"] = new JArray(cabinetIds.Select(cabinetId =>
                new JObject
                {
                    ["device_id"] = cabinetId,
                    ["fingerprint_ids"] = new JArray(),
                    ["lock_ids"] = new JArray(0, 1, 2, 3),
                    ["update_time"] = timestamp
                }));
            tables["users"].Add(user);
            AddPermissions(tables["permissions"], userId, _ => true, timestamp);
        }

        tables["role_permissions"].Add(new JObject
        {
            ["role"] = "student", ["lock_0"] = false, ["lock_1"] = false,
            ["lock_2"] = false, ["lock_3"] = false, ["update_time"] = timestamp
        });
        tables["role_permissions"].Add(new JObject
        {
            ["role"] = "teacher", ["lock_0"] = true, ["lock_1"] = true,
            ["lock_2"] = true, ["lock_3"] = true, ["update_time"] = timestamp
        });
        tables["role_permissions"].Add(new JObject
        {
            ["role"] = "admin", ["lock_0"] = true, ["lock_1"] = true,
            ["lock_2"] = true, ["lock_3"] = true, ["update_time"] = timestamp
        });
        return tables;
    }

    private static JObject CreateUser(
        string userId, string name, string role, string timestamp) => new()
    {
        ["user_id"] = userId,
        ["user_code"] = userId,
        ["name"] = name,
        ["gender"] = "",
        ["role"] = role,
        ["fingerprint_id"] = null,
        ["password_salt"] = "",
        ["password_hash"] = "",
        ["enabled"] = true,
        ["create_time"] = timestamp,
        ["update_time"] = timestamp
    };

    private static void AddPermissions(
        JArray permissions, string userId, Func<int, bool> hasAccess, string timestamp)
    {
        for (int lockId = 0; lockId < LockCount; lockId++)
        {
            permissions.Add(new JObject
            {
                ["user_id"] = userId,
                ["lock_id"] = lockId,
                ["has_access"] = hasAccess(lockId),
                ["update_time"] = timestamp
            });
        }
    }

    private static void ValidateCounts(IReadOnlyDictionary<string, JArray> tables)
    {
        int expectedUsers = ClassCount * StudentsPerClass + TeacherCount + 1;
        int expectedPermissions = (expectedUsers - 1) * LockCount;
        if (tables["classes"].Count != ClassCount ||
            tables["users"].Count != expectedUsers ||
            tables["permissions"].Count != expectedPermissions ||
            tables["devices"].Count != CabinetCount)
        {
            throw new InvalidDataException("Generated fixture has unexpected row counts");
        }

        int teachersWithAllCabinets = tables["users"].OfType<JObject>()
            .Count(user => user.Value<string>("role") == "teacher" &&
                user["assigned_device_ids"] is JArray ids && ids.Count == CabinetCount &&
                user["cabinet_assignments"] is JArray assignments &&
                assignments.Count == CabinetCount);
        if (teachersWithAllCabinets != TeacherCount)
            throw new InvalidDataException("Not every teacher is assigned to all cabinets");
    }

    private static void PrintProgress(string operation, long current, long total)
    {
        double percent = total == 0 ? 100 : current * 100.0 / total;
        Console.Write($"\r{operation}: {percent,6:F1}%  {FormatBytes(current)} / {FormatBytes(total)}");
    }

    private static void PrintSummary(BenchmarkReport report)
    {
        Console.WriteLine("Hardware benchmark complete.");
        Console.WriteLine(
            $"Upload: {report.UploadMs:F0} ms (ACK wait {report.UploadAckWaitMs:F0} ms, " +
            $"{report.UploadAckCount} ACKs, {report.UploadResumeCount} resumes)");
        Console.WriteLine(
            $"Download: {report.DownloadMs:F0} ms; " +
            $"read-back decode: {report.HardwareReadbackDecodeMs:F0} ms; " +
            $"SQLite restore: {report.HardwareReadbackRestoreMs:F0} ms");
        Console.WriteLine(
            $"Wire: upload sent {FormatBytes(report.UploadWireBytes)}, " +
            $"download received {FormatBytes(report.DownloadWireBytesReceived)}");
        Console.WriteLine($"Report: {Path.Combine(report.RunDirectory, "report.json")}");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024d):F2} MiB",
        >= 1024 => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B"
    };

    private static void WriteReport(string runDirectory, BenchmarkReport report)
    {
        try
        {
            string json = JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(runDirectory, "report.json"), json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write benchmark report: {ex.Message}");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "CabinetLock")) &&
                Directory.Exists(Path.Combine(directory.FullName, "esp32")))
                return directory.FullName;
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "CabinetLock")) &&
                Directory.Exists(Path.Combine(directory.FullName, "esp32")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found");
    }
}

internal sealed class SnapshotSerialClient : IDisposable
{
    private const ushort CmdError = 0x0005;
    private const ushort CmdManifest = 0x0048;
    private const ushort CmdManifestResponse = 0x0049;
    private const ushort CmdBegin = 0x004A;
    private const ushort CmdChunk = 0x004B;
    private const ushort CmdCommit = 0x004C;
    private const ushort CmdSnapshotResponse = 0x004D;
    private const ushort CmdDownload = 0x004E;
    private const ushort CmdDownloadPart = 0x004F;
    private const byte FormatVersion = 1;

    private readonly Options _options;
    private readonly SerialPort _port;
    private readonly FrameStreamDecoder _decoder = new();
    private readonly Queue<AppMessage> _messages = new();
    private readonly byte[] _readBuffer = new byte[8192];
    private ushort _nextMessageId = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
    private readonly ushort _correlationId =
        (ushort)RandomNumberGenerator.GetInt32(1, 65536);

    public SnapshotSerialClient(Options options)
    {
        _options = options;
        _port = new SerialPort(options.Port, options.BaudRate, Parity.None, 8,
            StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 100,
            WriteTimeout = 2000,
            DtrEnable = false,
            RtsEnable = false,
            ReadBufferSize = 256 * 1024,
            WriteBufferSize = 64 * 1024
        };
    }

    public long WireBytesSent { get; private set; }
    public long WireBytesReceived { get; private set; }
    public long UnframedReceiveBytes { get; private set; }
    public int InvalidApplicationMessages { get; private set; }

    public void Open()
    {
        _port.Open();
        _port.DtrEnable = false;
        _port.RtsEnable = false;
        Thread.Sleep(150);
        try { _port.DiscardInBuffer(); } catch { }
        _decoder.Reset();
    }

    public SnapshotManifest QueryManifest()
    {
        ushort messageId = Send(CmdManifest, new byte[] { FormatVersion }, needsAck: true);
        AppMessage response = WaitForMessage(messageId,
            new HashSet<ushort> { CmdManifestResponse, CmdError }, _options.TimeoutMs);
        ThrowIfError(response, "manifest query");
        byte[] payload = response.Payload;
        if (payload.Length < 4 || payload[0] != FormatVersion)
            throw new InvalidDataException("Invalid root snapshot manifest response");
        if (payload[1] == 1)
            return new SnapshotManifest { Exists = false };
        if (payload[1] != 0 || payload.Length != 4 + BusinessSnapshotCodec.HeaderSize)
            throw new InvalidDataException($"Root snapshot manifest status is {payload[1]}");

        byte[] header = payload.AsSpan(4, BusinessSnapshotCodec.HeaderSize).ToArray();
        if (!BusinessSnapshotCodec.TryReadHeader(header, out uint compressedSize,
                out uint rawSize, out byte[] contentHash))
            throw new InvalidDataException("Root snapshot manifest header is invalid");
        return new SnapshotManifest
        {
            Exists = true,
            Header = header,
            CompressedBytes = compressedSize,
            RawBytes = rawSize,
            ContentSha256 = contentHash
        };
    }

    public SnapshotUploadMetrics UploadSnapshot(
        BusinessSnapshot snapshot, Action<long, long>? progress = null)
    {
        Stopwatch totalWatch = Stopwatch.StartNew();
        SnapshotResponse begin = Begin(snapshot);
        if (begin.Status != 0)
            throw new IOException($"Snapshot begin failed with status {begin.Status}");

        uint offset = Math.Min(begin.NextOffset, (uint)snapshot.CompressedPayload.Length);
        int consecutiveResumeAttempts = 0;
        int totalResumeCount = 0;
        int ackCount = 0;
        TimeSpan ackWait = TimeSpan.Zero;
        progress?.Invoke(offset, snapshot.CompressedPayload.Length);

        while (offset < snapshot.CompressedPayload.Length)
        {
            uint groupStart = offset;
            SnapshotResponse acknowledgement = default;
            bool acknowledged = false;
            try
            {
                for (int index = 0;
                     index < _options.AckWindow && offset < snapshot.CompressedPayload.Length;
                     index++)
                {
                    int length = Math.Min(_options.ChunkSize,
                        snapshot.CompressedPayload.Length - (int)offset);
                    bool requestAck = index == _options.AckWindow - 1 ||
                        offset + length >= snapshot.CompressedPayload.Length;
                    byte[] payload = PackChunk(snapshot.UploadId, offset,
                        snapshot.CompressedPayload.AsSpan((int)offset, length), requestAck);
                    ushort messageId = Send(CmdChunk, payload, needsAck: false);
                    offset += (uint)length;

                    if (requestAck)
                    {
                        Stopwatch ackWatch = Stopwatch.StartNew();
                        AppMessage response = WaitForMessage(messageId,
                            new HashSet<ushort> { CmdSnapshotResponse, CmdError },
                            _options.TimeoutMs);
                        ackWatch.Stop();
                        ackWait += ackWatch.Elapsed;
                        ackCount++;
                        ThrowIfError(response, "snapshot chunk");
                        acknowledgement = ParseSnapshotResponse(response, operation: 2);
                        acknowledged = acknowledgement.Status == 0;
                        break;
                    }
                }
            }
            catch (TimeoutException)
            {
                acknowledged = false;
            }

            if (!acknowledged)
            {
                consecutiveResumeAttempts++;
                totalResumeCount++;
                if (consecutiveResumeAttempts > 3)
                    throw new TimeoutException("Snapshot upload did not receive a valid chunk ACK");
                begin = Begin(snapshot);
                if (begin.Status != 0)
                    throw new IOException($"Snapshot resume begin failed with status {begin.Status}");
                offset = Math.Min(begin.NextOffset,
                    (uint)snapshot.CompressedPayload.Length);
                if (offset > groupStart + (uint)(_options.ChunkSize * _options.AckWindow))
                    throw new InvalidDataException("Root returned an invalid snapshot resume offset");
                progress?.Invoke(offset, snapshot.CompressedPayload.Length);
                continue;
            }

            consecutiveResumeAttempts = 0;
            offset = Math.Min(acknowledgement.NextOffset,
                (uint)snapshot.CompressedPayload.Length);
            progress?.Invoke(offset, snapshot.CompressedPayload.Length);
        }

        Stopwatch commitWatch = Stopwatch.StartNew();
        byte[] commitPayload = new byte[20];
        commitPayload[0] = FormatVersion;
        snapshot.UploadId.CopyTo(commitPayload, 4);
        ushort commitMessageId = Send(CmdCommit, commitPayload, needsAck: true);
        AppMessage commitMessage = WaitForMessage(commitMessageId,
            new HashSet<ushort> { CmdSnapshotResponse, CmdError }, _options.TimeoutMs);
        commitWatch.Stop();
        ThrowIfError(commitMessage, "snapshot commit");
        SnapshotResponse commit = ParseSnapshotResponse(commitMessage, operation: 3);
        if (commit.Status != 0)
            throw new IOException($"Snapshot commit failed with status {commit.Status}");
        totalWatch.Stop();
        return new SnapshotUploadMetrics
        {
            Elapsed = totalWatch.Elapsed,
            AckWait = ackWait,
            AckCount = ackCount,
            ResumeCount = totalResumeCount,
            CommitElapsed = commitWatch.Elapsed
        };
    }

    public byte[] DownloadSnapshot(
        SnapshotManifest manifest, Action<long, long>? progress = null)
    {
        if (!manifest.Exists || manifest.Header.Length != BusinessSnapshotCodec.HeaderSize)
            throw new InvalidOperationException("Cannot download a missing snapshot");
        int expectedSize = checked(BusinessSnapshotCodec.HeaderSize +
            (int)manifest.CompressedBytes);
        Exception? lastError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                byte[] buffer = new byte[expectedSize];
                uint nextOffset = 0;
                byte[] request = new byte[8];
                request[0] = FormatVersion;
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4, 4), 0);
                ushort messageId = Send(CmdDownload, request, needsAck: true);
                Stopwatch idle = Stopwatch.StartNew();
                while (idle.ElapsedMilliseconds < _options.DownloadTimeoutMs)
                {
                    AppMessage response = WaitForMessage(messageId,
                        new HashSet<ushort> { CmdDownloadPart, CmdError },
                        Math.Max(250, _options.DownloadTimeoutMs - (int)idle.ElapsedMilliseconds));
                    ThrowIfError(response, "snapshot download");
                    byte[] payload = response.Payload;
                    if (payload.Length < 12 || payload[0] != FormatVersion)
                        throw new InvalidDataException("Invalid snapshot download part");
                    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                    uint total = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
                    int length = payload.Length - 12;
                    if (total != expectedSize || offset > total ||
                        (uint)length > total - offset || offset != nextOffset)
                    {
                        throw new InvalidDataException(
                            $"Out-of-order snapshot part: offset={offset}, expected={nextOffset}");
                    }
                    Buffer.BlockCopy(payload, 12, buffer, (int)offset, length);
                    nextOffset += (uint)length;
                    progress?.Invoke(nextOffset, expectedSize);
                    idle.Restart();
                    bool last = (payload[1] & 1) != 0;
                    if (last)
                    {
                        if (nextOffset != expectedSize)
                            throw new InvalidDataException("Final snapshot part ended early");
                        BusinessSnapshot decoded = BusinessSnapshotCodec.Decode(buffer);
                        if (!CryptographicOperations.FixedTimeEquals(
                                decoded.ContentSha256, manifest.ContentSha256))
                            throw new InvalidDataException("Downloaded snapshot content hash mismatch");
                        return buffer;
                    }
                }
                throw new TimeoutException("Snapshot download timed out");
            }
            catch (Exception ex) when (attempt == 0)
            {
                lastError = ex;
                _messages.Clear();
                _decoder.Reset();
            }
        }
        throw new IOException("Snapshot download failed after retry", lastError);
    }

    private SnapshotResponse Begin(BusinessSnapshot snapshot)
    {
        ushort messageId = Send(CmdBegin, snapshot.Header, needsAck: true);
        AppMessage response = WaitForMessage(messageId,
            new HashSet<ushort> { CmdSnapshotResponse, CmdError }, _options.TimeoutMs);
        ThrowIfError(response, "snapshot begin");
        return ParseSnapshotResponse(response, operation: 1);
    }

    private ushort Send(ushort command, byte[] payload, bool needsAck)
    {
        _nextMessageId++;
        if (_nextMessageId == 0) _nextMessageId = 1;
        var app = new AppMessage
        {
            Flags = needsAck ? AppMessageFlags.NeedsAck : AppMessageFlags.None,
            CmdId = command,
            MsgId = _nextMessageId,
            CorrId = _correlationId,
            DeviceId = _options.RootId,
            TimestampUnix = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = payload
        };
        byte[] wire = FrameCodec.Encode(BinaryMessageCodec.Encode(app))
            ?? throw new InvalidOperationException("Could not encode snapshot frame");
        int offset = 0;
        while (offset < wire.Length)
        {
            int length = Math.Min(_options.WriteSize, wire.Length - offset);
            _port.Write(wire, offset, length);
            WireBytesSent += length;
            offset += length;
            if (offset < wire.Length && _options.WriteDelayMs > 0)
                Thread.Sleep(_options.WriteDelayMs);
        }
        _port.BaseStream.Flush();
        return _nextMessageId;
    }

    private AppMessage WaitForMessage(
        ushort messageId, HashSet<ushort> commands, int timeoutMs)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            int pendingCount = _messages.Count;
            for (int index = 0; index < pendingCount; index++)
            {
                AppMessage candidate = _messages.Dequeue();
                if (candidate.MsgId == messageId && commands.Contains(candidate.CmdId))
                    return candidate;
                if (candidate.CmdId == CmdDownloadPart)
                    _messages.Enqueue(candidate);
            }

            ReadAvailable();
            if (_messages.Count == 0)
                Thread.Sleep(1);
        }
        throw new TimeoutException(
            $"Response timeout: msg_id={messageId}, commands={string.Join(',', commands.Select(c => $"0x{c:X4}"))}");
    }

    private void ReadAvailable()
    {
        int guard = 0;
        while (_port.BytesToRead > 0 && guard++ < 256)
        {
            int length = Math.Min(_readBuffer.Length, _port.BytesToRead);
            int read = _port.Read(_readBuffer, 0, length);
            if (read <= 0) break;
            WireBytesReceived += read;
            _decoder.AppendBytes(_readBuffer, 0, read, payload =>
            {
                if (BinaryMessageCodec.TryDecode(payload, out AppMessage? app) && app != null)
                    _messages.Enqueue(app);
                else
                    InvalidApplicationMessages++;
            }, noise => UnframedReceiveBytes += noise.Length);
        }
    }

    private static SnapshotResponse ParseSnapshotResponse(
        AppMessage message, byte operation)
    {
        byte[] payload = message.Payload;
        if (message.CmdId != CmdSnapshotResponse || payload.Length < 28 ||
            payload[0] != FormatVersion || payload[1] != operation)
            throw new InvalidDataException("Invalid root snapshot operation response");
        return new SnapshotResponse
        {
            Status = payload[2],
            NextOffset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)),
            TotalSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4))
        };
    }

    private static byte[] PackChunk(
        byte[] uploadId, uint offset, ReadOnlySpan<byte> data, bool requestAck)
    {
        byte[] payload = new byte[24 + data.Length];
        payload[0] = FormatVersion;
        payload[1] = requestAck ? (byte)1 : (byte)0;
        uploadId.CopyTo(payload, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20, 4), offset);
        data.CopyTo(payload.AsSpan(24));
        return payload;
    }

    private static void ThrowIfError(AppMessage message, string operation)
    {
        if (message.CmdId != CmdError) return;
        if (BinaryMessageCodec.ErrorPayload.TryUnpack(message.Payload,
                out _, out ushort code, out string text))
            throw new IOException($"Root rejected {operation}: code={code}, message={text}");
        throw new IOException($"Root rejected {operation} with an undecodable error");
    }

    public void Dispose()
    {
        try { _port.Close(); } catch { }
        _port.Dispose();
    }
}

internal sealed class Options
{
    public string Port { get; private set; } = "COM16";
    public string RootId { get; private set; } = "ROOT_B81F3FA9F404";
    public int BaudRate { get; private set; } = 921600;
    public int ChunkSize { get; private set; } = 3000;
    public int AckWindow { get; private set; } = 4;
    public int WriteSize { get; private set; } = 1024;
    public int WriteDelayMs { get; private set; } = 1;
    public int TimeoutMs { get; private set; } = 10000;
    public int DownloadTimeoutMs { get; private set; } = 30000;
    public bool LocalOnly { get; private set; }
    public bool KeepBenchmark { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            string Value() => index + 1 < args.Length
                ? args[++index]
                : throw new ArgumentException($"Missing value for {argument}");
            switch (argument)
            {
                case "--port": options.Port = Value(); break;
                case "--root-id": options.RootId = Value(); break;
                case "--baud": options.BaudRate = int.Parse(Value()); break;
                case "--chunk-size": options.ChunkSize = int.Parse(Value()); break;
                case "--ack-window": options.AckWindow = int.Parse(Value()); break;
                case "--write-size": options.WriteSize = int.Parse(Value()); break;
                case "--write-delay-ms": options.WriteDelayMs = int.Parse(Value()); break;
                case "--timeout-ms": options.TimeoutMs = int.Parse(Value()); break;
                case "--download-timeout-ms":
                    options.DownloadTimeoutMs = int.Parse(Value());
                    break;
                case "--local-only": options.LocalOnly = true; break;
                case "--keep-benchmark": options.KeepBenchmark = true; break;
                case "--help":
                case "-h": PrintUsage(); Environment.Exit(0); break;
                default: throw new ArgumentException($"Unknown argument: {argument}");
            }
        }
        if (options.ChunkSize is < 256 or > 8000)
            throw new ArgumentOutOfRangeException(nameof(options.ChunkSize));
        if (options.AckWindow is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(options.AckWindow));
        if (options.WriteSize is < 64 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(options.WriteSize));
        if (options.WriteDelayMs is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options.WriteDelayMs));
        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            "BusinessSnapshotBenchmark [--local-only] [--keep-benchmark] [--port COM16] " +
            "[--root-id ROOT_xxx] [--baud 921600] [--chunk-size 3000] " +
            "[--ack-window 4] [--write-size 1024] [--write-delay-ms 1]");
    }
}

internal sealed class SnapshotManifest
{
    public bool Exists { get; init; }
    public byte[] Header { get; init; } = Array.Empty<byte>();
    public uint CompressedBytes { get; init; }
    public uint RawBytes { get; init; }
    public byte[] ContentSha256 { get; init; } = Array.Empty<byte>();
}

internal struct SnapshotResponse
{
    public byte Status { get; init; }
    public uint NextOffset { get; init; }
    public uint TotalSize { get; init; }
}

internal sealed class SnapshotUploadMetrics
{
    public TimeSpan Elapsed { get; init; }
    public TimeSpan AckWait { get; init; }
    public int AckCount { get; init; }
    public int ResumeCount { get; init; }
    public TimeSpan CommitElapsed { get; init; }
}

internal sealed class BenchmarkReport
{
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string RunDirectory { get; set; } = "";
    public string Port { get; set; } = "";
    public string RootId { get; set; } = "";
    public int BaudRate { get; set; }
    public int SnapshotChunkSize { get; set; }
    public int AckWindow { get; set; }
    public int HostWriteSize { get; set; }
    public int HostWriteDelayMs { get; set; }
    public int Classes { get; set; }
    public int Users { get; set; }
    public int Students { get; set; }
    public int Teachers { get; set; }
    public int Devices { get; set; }
    public int Permissions { get; set; }
    public long DatabaseBytes { get; set; }
    public long RestoredDatabaseBytes { get; set; }
    public int RawSnapshotBytes { get; set; }
    public int CompressedSnapshotBytes { get; set; }
    public int ContainerBytes { get; set; }
    public string ContentSha256 { get; set; } = "";
    public double FixtureGenerationMs { get; set; }
    public double DatabaseImportMs { get; set; }
    public double SnapshotEncodeMs { get; set; }
    public double LocalDecodeMs { get; set; }
    public double LocalRestoreMs { get; set; }
    public bool RootHadOriginalSnapshot { get; set; }
    public double InitialManifestMs { get; set; }
    public double OriginalDownloadMs { get; set; }
    public bool OriginalSnapshotRestored { get; set; }
    public double OriginalRestoreMs { get; set; }
    public string RestoreError { get; set; } = "";
    public double UploadMs { get; set; }
    public double UploadAckWaitMs { get; set; }
    public int UploadAckCount { get; set; }
    public int UploadResumeCount { get; set; }
    public double UploadCommitMs { get; set; }
    public long UploadWireBytes { get; set; }
    public double DownloadMs { get; set; }
    public long DownloadWireBytesSent { get; set; }
    public long DownloadWireBytesReceived { get; set; }
    public int DownloadedContainerBytes { get; set; }
    public double HardwareReadbackDecodeMs { get; set; }
    public double HardwareReadbackRestoreMs { get; set; }
    public long UnframedReceiveBytes { get; set; }
    public int InvalidApplicationMessages { get; set; }
}
