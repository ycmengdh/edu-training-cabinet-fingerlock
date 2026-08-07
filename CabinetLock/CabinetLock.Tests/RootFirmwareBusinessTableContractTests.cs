using System.Text.RegularExpressions;

namespace CabinetLock.Tests;

public class RootFirmwareBusinessTableContractTests
{
    [Fact]
    public void RootSdWhitelist_AllowsEveryHostBusinessTable()
    {
        string sourcePath = FindRepositoryFile(
            Path.Combine("esp32_firmware", "root_node", "src", "message_handler.cpp"));
        string source = File.ReadAllText(sourcePath);
        Match function = Regex.Match(
            source,
            @"static\s+bool\s+isAllowedTable\s*\([^)]*\)\s*\{(?<body>.*?)\}",
            RegexOptions.Singleline);

        Assert.True(function.Success, "Root firmware isAllowedTable() was not found.");
        string body = function.Groups["body"].Value;
        foreach (string table in BusinessDatabase.BusinessTables)
        {
            Assert.Contains($"table == \"{table}\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RootSdVersionLookup_UsesFingerprintVersion()
    {
        string sourcePath = FindRepositoryFile(
            Path.Combine("esp32_firmware", "root_node", "src", "message_handler.cpp"));
        string source = File.ReadAllText(sourcePath);
        Match function = Regex.Match(
            source,
            @"static\s+uint32_t\s+getTableVersion\s*\([^)]*\)\s*\{(?<body>.*?)\}",
            RegexOptions.Singleline);

        Assert.True(function.Success, "Root firmware getTableVersion() was not found.");
        Assert.Contains(
            "if (table == \"fingerprints\") return fingerprintVersion;",
            function.Groups["body"].Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionVersion_RegistrationAndRootSyncUseTheSameCounters()
    {
        uint expected;
        unchecked
        {
            expected = 2166136261U;
            expected = (expected ^ 17U) * 16777619U;
            expected = (expected ^ 19U) * 16777619U;
            expected = (expected ^ 29U) * 16777619U;
            expected = (expected ^ 31U) * 16777619U;
        }
        Assert.Equal(expected,
            CabinetSyncService.ComposePermissionVersion(17, 19, 29, 31));

        string sourcePath = FindRepositoryFile(
            Path.Combine("esp32_firmware", "root_node", "src", "message_handler.cpp"));
        string source = File.ReadAllText(sourcePath);
        Match function = Regex.Match(
            source,
            @"static\s+uint32_t\s+composePermissionVersion\s*\([^)]*\)\s*\{(?<body>.*?)\}",
            RegexOptions.Singleline);

        Assert.True(function.Success, "Root firmware composePermissionVersion() was not found.");
        string body = function.Groups["body"].Value;
        Assert.Contains("value = (value ^ usersVersion) * 16777619U;", body,
            StringComparison.Ordinal);
        Assert.Contains("value = (value ^ classesVersion) * 16777619U;", body,
            StringComparison.Ordinal);
        Assert.Contains("value = (value ^ permissionsVersion) * 16777619U;", body,
            StringComparison.Ordinal);
        Assert.Contains("value = (value ^ fingerprintsVersion) * 16777619U;", body,
            StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source,
            @"composePermissionVersion\(\s*usersVersion,\s*classesVersion,\s*permissionsVersion,\s*fingerprintVersion\s*\)",
            RegexOptions.Singleline).Count);
    }

    [Fact]
    public void PermissionTableSave_SchedulesOnlineCabinetResync()
    {
        string sourcePath = FindRepositoryFile(
            Path.Combine("esp32_firmware", "root_node", "src", "message_handler.cpp"));
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("schedulePermissionSyncAfterDataChange(table);", source,
            StringComparison.Ordinal);
        Assert.Contains("queuePermissionSyncForOnlineCabinets();", source,
            StringComparison.Ordinal);
        Assert.Contains("table != \"users\" && table != \"classes\"", source,
            StringComparison.Ordinal);
        Assert.Contains("table != \"permissions\"", source, StringComparison.Ordinal);
        Assert.Contains("table != \"role_permissions\"", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RootMeshDownlink_UsesApplicationReliabilityWithoutSdkP2pQueue()
    {
        string sourcePath = FindRepositoryFile(
            Path.Combine("esp32_firmware", "common", "mesh_comm.cpp"));
        string source = File.ReadAllText(sourcePath);
        Match function = Regex.Match(
            source,
            @"bool\s+MeshComm::sendToNodeApp\s*\([^)]*\)\s*\{(?<body>.*?)\n\}",
            RegexOptions.Singleline);

        Assert.True(function.Success, "MeshComm::sendToNodeApp() was not found.");
        string body = function.Groups["body"].Value;
        Assert.Contains("data.tos = MESH_TOS_DEF", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MESH_TOS_P2P", body, StringComparison.Ordinal);
        Assert.Contains("MESH_DATA_FROMDS", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CabinetResponse_RepliesWithTheRequestSessionId()
    {
        string sourcePath = FindRepositoryFile(
            Path.Combine("esp32_firmware", "common", "mesh_comm.cpp"));
        string source = File.ReadAllText(sourcePath);
        Match function = Regex.Match(
            source,
            @"bool\s+MeshComm::sendAppRaw\s*\([^)]*\)\s*\{(?<body>.*?)\n\}",
            RegexOptions.Singleline);

        Assert.True(function.Success, "MeshComm::sendAppRaw() was not found.");
        string body = function.Groups["body"].Value;
        Assert.Contains("view.corr_id == 0 && s_activeIngressSessionId != 0", body,
            StringComparison.Ordinal);
        Assert.Contains("s_activeIngressSessionId, view.flags", body,
            StringComparison.Ordinal);
        Assert.Contains("cacheResponse(route, outgoing, outgoingLen)", body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CabinetMesh_RecoversWhenRootStartsLaterOrRestarts()
    {
        string sourcePath = FindRepositoryFile(
            Path.Combine("esp32_firmware", "common", "mesh_comm.cpp"));
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("REGISTER_RETRY_INTERVAL_MS", source, StringComparison.Ordinal);
        Assert.Contains("phasedLastSend(", source, StringComparison.Ordinal);
        Assert.Contains("rootResponseTimedOut = true;", source, StringComparison.Ordinal);
        Assert.Contains("restartCabinetMeshStack();", source, StringComparison.Ordinal);
        Assert.Contains("MESH_EVENT_PARENT_DISCONNECTED", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
