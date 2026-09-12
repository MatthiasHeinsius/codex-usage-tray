using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace CodexUsageTray;

internal static class LegalNotices
{
    private const string LicenseResourceName = "CodexUsageTray.LICENSE.txt";
    private const string ThirdPartyNoticesResourceName = "CodexUsageTray.THIRD-PARTY-NOTICES.txt";
    private const string OutputFileName = "CodexUsageTray-LICENSES.txt";

    internal static string ReadAll()
        => $"""
            CODEX USAGE TRAY LICENSE
            ========================

            {ReadResource(LicenseResourceName)}


            THIRD-PARTY SOFTWARE NOTICES
            ============================

            {ReadResource(ThirdPartyNoticesResourceName)}
            """;

    internal static void Open()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, OutputFileName);
        File.WriteAllText(path, ReadAll(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded legal notice '{name}' is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().TrimEnd();
    }
}
