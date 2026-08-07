namespace CabinetLock.Tests;

public class ClassEditStatusContractTests
{
    [Fact]
    public void ClassEditor_ExposesEnabledAndDisabledStates()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Views", "ClassEditWindow.xaml")));
        string code = File.ReadAllText(FindRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Views", "ClassEditWindow.xaml.cs")));

        Assert.Contains("x:Name=\"EnabledStatusButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisabledStatusButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("public bool RequestedEnabled", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassStatusChange_UsesLifecycleProcessing()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Views", "ClassManagePage.xaml.cs")));

        Assert.Contains("requestedEnabled != wasEnabled", source, StringComparison.Ordinal);
        Assert.Contains("ClassLifecycleAction.Enable", source, StringComparison.Ordinal);
        Assert.Contains("ClassLifecycleAction.Disable", source, StringComparison.Ordinal);
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
