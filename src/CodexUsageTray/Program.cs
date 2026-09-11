namespace CodexUsageTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
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

        using var singleInstance = new Mutex(true, "Local\\CodexUsageTray.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var trayUpdates = UsageUpdates.CreateDefault();
        Application.Run(new TrayApplicationContext(trayUpdates));
        GC.KeepAlive(singleInstance);
        return 0;
    }
}
