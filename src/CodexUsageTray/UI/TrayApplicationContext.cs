using System.Diagnostics;

namespace CodexUsageTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ProjectReadmeUrl =
        "https://github.com/MatthiasHeinsius/codex-usage-tray/blob/main/README.md";
    private readonly WinFormsApplicationShell shell;
    private readonly UsagePresentations usagePresentations;
    private readonly ApplicationUpdates applicationUpdates;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private bool exiting;

    public TrayApplicationContext()
    {
        var automaticUpdateEnabled = ApplicationUpdateSettings.IsAutomaticCheckEnabled();
        var createdShell = new WinFormsApplicationShell(
            StartupRegistration.IsEnabled(),
            automaticUpdateEnabled,
            new WinFormsApplicationShell.Commands(
                RequestUsageUpdate: RequestAsync,
                RequestApplicationUpdate: RequestApplicationUpdateAsync,
                SetStartupEnabled: StartupRegistration.SetEnabled,
                SetAutomaticUpdateEnabled: ApplicationUpdateSettings.SetAutomaticCheckEnabled,
                SetAllowanceActivationEnabled: SetAllowanceActivationEnabled,
                SetAllowanceNotificationsEnabled: SetAllowanceNotificationsEnabled,
                OpenUsagePage: OpenUsagePage,
                OpenProjectReadme: () => OpenWebPage(ProjectReadmeUrl),
                OpenLegalNotices: LegalNotices.Open,
                Exit: ExitThread));
        UsagePresentations? createdPresentations = null;
        ApplicationUpdates? createdUpdates = null;
        System.Windows.Forms.Timer? createdTimer = null;
        try
        {
            createdPresentations = new UsagePresentations(
                UsageUpdates.CreateDefault(createdShell),
                createdShell);
            createdShell.InitializeUsagePreferences(
                createdPresentations.ActivationEnabled,
                createdPresentations.NotificationsEnabled);
            createdUpdates = ApplicationUpdates.CreateDefault(createdShell);
            createdTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };

            shell = createdShell;
            usagePresentations = createdPresentations;
            applicationUpdates = createdUpdates;
            refreshTimer = createdTimer;

            refreshTimer.Tick += async (_, _) => await RequestAsync(UsageUpdateIntent.Routine);
            shell.Activate();
            refreshTimer.Start();
        }
        catch
        {
            createdTimer?.Dispose();
            createdUpdates?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            createdPresentations?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            createdShell.Dispose();
            throw;
        }

        _ = RequestAsync(UsageUpdateIntent.Routine);
        if (automaticUpdateEnabled)
        {
            _ = RequestApplicationUpdateAsync(ApplicationUpdateIntent.Automatic);
        }
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        refreshTimer.Stop();
        refreshTimer.Dispose();
        applicationUpdates.DisposeAsync().AsTask().GetAwaiter().GetResult();
        usagePresentations.DisposeAsync().AsTask().GetAwaiter().GetResult();
        shell.Dispose();
        base.ExitThreadCore();
    }

    private async Task RequestAsync(UsageUpdateIntent intent)
    {
        try
        {
            await usagePresentations.RequestAsync(intent);
        }
        catch (OperationCanceledException) when (exiting)
        {
            // Disposal cancels active and queued Usage Updates.
        }
    }

    private async Task RequestApplicationUpdateAsync(ApplicationUpdateIntent intent)
    {
        try
        {
            await applicationUpdates.RequestAsync(intent);
        }
        catch (OperationCanceledException) when (exiting)
        {
            // Disposal cancels the active Application Update.
        }
    }

    private void SetAllowanceActivationEnabled(bool enabled)
    {
        usagePresentations.ActivationEnabled = enabled;
        if (enabled)
        {
            _ = RequestAsync(UsageUpdateIntent.Routine);
        }
    }

    private void SetAllowanceNotificationsEnabled(bool enabled)
    {
        usagePresentations.NotificationsEnabled = enabled;
    }

    private static void OpenUsagePage()
    {
        OpenWebPage("https://chatgpt.com/codex/settings/usage");
    }

    private static void OpenWebPage(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
