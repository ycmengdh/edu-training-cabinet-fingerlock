namespace CabinetLock.Tests;

public class CabinetLockMappingContractTests
{
    [Fact]
    public void CabinetFirmware_UsesConfirmedRelayAndLedOutputs()
    {
        string lockControl = File.ReadAllText(FindRepositoryFile(
            Path.Combine("esp32_firmware", "cabinet_node", "src", "lock_control.cpp")));

        Assert.Contains(
            "RELAY_BIT_BY_LOCK_ID[LOCK_COUNT] = {4, 5, 6, 7}",
            lockControl,
            StringComparison.Ordinal);
        Assert.Contains(
            "LED_BIT_BY_LOCK_ID[LOCK_COUNT]   = {3, 2, 1, 0}",
            lockControl,
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
