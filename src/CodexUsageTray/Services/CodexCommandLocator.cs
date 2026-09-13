namespace CodexUsageTray;

internal static class CodexCommandLocator
{
    public static string Find() => Find(
        Environment.GetEnvironmentVariable("CODEX_USAGE_CODEX_PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("PATH"));

    internal static string Find(string? configured, string? appData, string? searchPath)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.Combine(appData, "npm", "codex.cmd"));
            candidates.Add(Path.Combine(appData, "npm", "codex.exe"));
        }

        foreach (var directory in (searchPath ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory.Trim('"'), "codex.cmd"));
            candidates.Add(Path.Combine(directory.Trim('"'), "codex.exe"));
        }

        var match = candidates.FirstOrDefault(File.Exists);
        return match ?? throw new InvalidOperationException(
            "Codex CLI was not found. Install it or set CODEX_USAGE_CODEX_PATH to codex.cmd or codex.exe.");
    }
}
