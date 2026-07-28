using System.Text.RegularExpressions;

namespace FingerprintLockManager.Tests;

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
    public void PermissionVersion_ComposesTheSameTwoVersionCountersOnHostAndRoot()
    {
        uint expected = CabinetSyncService.ComposePermissionVersion(17, 29);

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
        Assert.Contains("value = (value ^ permissionsVersion) * 16777619U;", body,
            StringComparison.Ordinal);
        Assert.NotEqual(0u, expected);
        Assert.Contains("composePermissionVersion(usersVersion, permissionsVersion)", source,
            StringComparison.Ordinal);
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
        Assert.Contains("table != \"users\" && table != \"permissions\"", source,
            StringComparison.Ordinal);
        Assert.Contains("table != \"role_permissions\"", source,
            StringComparison.Ordinal);
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
