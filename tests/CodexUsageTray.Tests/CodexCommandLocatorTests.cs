namespace CodexUsageTray.Tests;

public sealed class CodexCommandLocatorTests
{
    [Fact]
    public void FindPrefersAnExistingConfiguredExecutable()
    {
        using var directory = new TemporaryDirectory("configured-codex");
        var configured = CreateFile(directory.FilePath("custom-codex.exe"));
        var appData = Directory.CreateDirectory(directory.FilePath("appdata")).FullName;
        CreateFile(Path.Combine(appData, "npm", "codex.cmd"));

        var result = CodexCommandLocator.Find(configured, appData, searchPath: null);

        Assert.Equal(Path.GetFullPath(configured), result);
    }

    [Fact]
    public void FindFallsBackFromMissingConfigurationToAppData()
    {
        using var directory = new TemporaryDirectory("appdata-codex");
        var appData = Directory.CreateDirectory(directory.FilePath("appdata")).FullName;
        var expected = CreateFile(Path.Combine(appData, "npm", "codex.cmd"));

        var result = CodexCommandLocator.Find(
            directory.FilePath("missing.exe"),
            appData,
            searchPath: null);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindSearchesQuotedPathEntriesAndPrefersCmd()
    {
        using var directory = new TemporaryDirectory("path-codex");
        var executable = CreateFile(directory.FilePath("codex.exe"));
        var command = CreateFile(directory.FilePath("codex.cmd"));
        var searchPath = $"\"{directory.RootPath}\"{Path.PathSeparator}{directory.FilePath("missing")}";

        var result = CodexCommandLocator.Find(configured: null, appData: null, searchPath);

        Assert.Equal(command, result);
        Assert.NotEqual(executable, result);
    }

    [Fact]
    public void FindThrowsWhenNoExecutableExists()
    {
        using var directory = new TemporaryDirectory("missing-codex");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexCommandLocator.Find(
                directory.FilePath("configured.exe"),
                directory.FilePath("appdata"),
                directory.FilePath("path")));

        Assert.Equal(
            "Codex CLI was not found. Install it or set CODEX_USAGE_CODEX_PATH to codex.cmd or codex.exe.",
            exception.Message);
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return Path.GetFullPath(path);
    }
}
