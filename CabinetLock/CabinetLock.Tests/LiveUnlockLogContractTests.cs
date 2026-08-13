namespace CabinetLock.Tests;

public class LiveUnlockLogContractTests
{
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
