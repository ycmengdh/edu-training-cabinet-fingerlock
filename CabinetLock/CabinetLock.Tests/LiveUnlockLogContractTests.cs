namespace CabinetLock.Tests;

public class LiveUnlockLogContractTests
{
    [Fact]
    public void CabinetLogger_SendsLiveEventWithoutLocalPersistenceOrRetryState()
    {
        string source = ReadRepositoryFile(
            Path.Combine("esp32_firmware", "cabinet_node", "src", "logger.cpp"));

        Assert.Contains("MeshComm::sendMessage(\"LOG_REPORT\", data)", source,
            StringComparison.Ordinal);
        Assert.Contains("MeshComm::isMeshConnected()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Storage::appendLog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("reportBatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("awaitingAck", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RootLogPath_PersistsOnlyWhileHostProtocolIsInactive()
    {
        string bridgeSource = ReadRepositoryFile(
            Path.Combine("esp32_firmware", "root_node", "src", "mesh_bridge.cpp"));
        string handlerSource = ReadRepositoryFile(
            Path.Combine("esp32_firmware", "root_node", "src", "message_handler.cpp"));

        Assert.Contains("const bool forwardToHost = isHostProtocolActive()", bridgeSource,
            StringComparison.Ordinal);
        Assert.Contains("view.cmd_id != CMD_LOG_REPORT", handlerSource,
            StringComparison.Ordinal);
        Assert.Contains("if (!MeshBridge::isUplinkConnected())", handlerSource,
            StringComparison.Ordinal);
        Assert.Contains("SdStorage::appendLogs(logJson)", handlerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CMD_LOG_REPORT_ACK", handlerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RootPendingLogs_AreDailyRetainedForThirtyDaysAndClearOnlyByEmptyAck()
    {
        string configSource = ReadRepositoryFile(
            Path.Combine("esp32_firmware", "common", "config_common.h"));
        string storageSource = ReadRepositoryFile(
            Path.Combine("esp32_firmware", "root_node", "src", "sd_storage.cpp"));

        Assert.Contains("#define SD_LOG_RETENTION_DAYS   30", configSource,
            StringComparison.Ordinal);
        Assert.Contains("#define SD_LOG_DIR", configSource, StringComparison.Ordinal);
        Assert.Contains("%04d-%02d-%02d.jsonl", storageSource, StringComparison.Ordinal);
        Assert.Contains("prunePendingLogs", storageSource, StringComparison.Ordinal);
        Assert.Contains("tableName == \"logs\"", storageSource, StringComparison.Ordinal);
        Assert.Contains("isEmptyJsonArray(json)", storageSource, StringComparison.Ordinal);
        Assert.Contains("clearPendingLogs()", storageSource, StringComparison.Ordinal);
        Assert.Contains("Reject non-empty logs upload", storageSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostStartup_MergesSnapshotBeforeVersionedRootClear()
    {
        string source = ReadRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Services", "SdBusinessSyncService.cs"));

        int merge = source.IndexOf("LogDatabase.MergeUnlockFromArray(array)",
            StringComparison.Ordinal);
        int clear = source.IndexOf(
            "SaveTableAsync(\"logs\", \"[]\", logSnap.Version, timeoutMs)",
            StringComparison.Ordinal);
        Assert.True(merge >= 0, "Host startup log merge was not found.");
        Assert.True(clear > merge, "Root logs must only be cleared after the local merge commits.");
    }

    [Fact]
    public void BusinessUpload_DoesNotPushLocalLogsBackToRoot()
    {
        string source = ReadRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Services", "SdBusinessSyncService.cs"));

        string pushSection = source[(source.IndexOf("PushBusinessToSdCoreAsync",
            StringComparison.Ordinal))..];
        Assert.DoesNotContain("LogDatabase.ReadAllUnlock", pushSection,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SaveTableAsync(\"logs\"", pushSection,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
