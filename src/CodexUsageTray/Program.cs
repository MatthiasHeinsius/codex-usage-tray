using System.Diagnostics;

namespace CodexUsageTray;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.Run();
        }

        if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var snapshot = await new CodexAppServerClient().ReadUsageAsync(CancellationToken.None);
                Console.WriteLine(UsageText.FormatConsole(snapshot));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        using var singleInstance = new Mutex(true, "Local\\CodexUsageTray.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        GC.KeepAlive(singleInstance);
        return 0;
    }
}
