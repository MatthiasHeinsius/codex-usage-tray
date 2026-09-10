using System.Diagnostics;
using System.Globalization;

namespace CodexUsageTray;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ApplicationConfiguration.Initialize();
                return SelfTest.Run();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await using var onceSnapshots = new UsageSnapshots(new CodexUsageObservationReader());
                var snapshot = await onceSnapshots.RefreshWithActivityAsync();
                var presentation = UsagePresentation.Create(
                    snapshot,
                    DateTimeOffset.Now,
                    CultureInfo.CurrentCulture);
                Console.WriteLine(presentation.ConsoleText);
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
        var traySnapshots = new UsageSnapshots(new CodexUsageObservationReader());
        using var activation = new AllowanceWindowActivation(
            new CodexWindowStarter(),
            traySnapshots,
            new RegistryAllowanceWindowActivationSettings(),
            TimeProvider.System);
        Application.Run(new TrayApplicationContext(traySnapshots, activation));
        GC.KeepAlive(singleInstance);
        return 0;
    }
}
