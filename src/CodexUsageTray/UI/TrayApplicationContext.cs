using System.Diagnostics;

namespace CodexUsageTray;

internal sealed class TrayApplicationContext : ApplicationContext, IUsagePresentationSink
{
    internal const string AllowanceActivationMenuText = "Auto-activate allowance window";
    internal const string AllowanceNotificationsMenuText = "Notify on allowance changes";
    internal const string AutomaticUpdateMenuText = "Automatically check for updates";
    internal const string CheckForUpdatesMenuText = "Check for updates";
    private readonly UsagePresentations usagePresentations;
    private readonly ApplicationUpdater applicationUpdater;
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem automaticUpdateItem;
    private readonly ToolStripMenuItem allowanceActivationItem;
    private readonly ToolStripMenuItem allowanceNotificationsItem;
    private readonly ToolStripMenuItem updateItem;
    private Icon currentIcon;
    private bool popupVisibleWhenTrayMousePressed;
    private bool updateCheckRunning;
    private bool exiting;
    private long? lastHandledTrayClickTimestamp;

    public TrayApplicationContext(Func<IUsagePresentationSink, UsagePresentations> createPresentations)
    {
        ArgumentNullException.ThrowIfNull(createPresentations);
        popup.CreateControl();
        currentIcon = TrayIconRenderer.Create(100, 100);
        notifyIcon = new NotifyIcon
        {
            Icon = currentIcon,
            Text = "Codex usage · connecting"
        };
        usagePresentations = createPresentations(this);
        applicationUpdater = ApplicationUpdater.CreateDefault();
        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        automaticUpdateItem = new ToolStripMenuItem(AutomaticUpdateMenuText)
        {
            Checked = ApplicationUpdateSettings.IsAutomaticCheckEnabled(),
            CheckOnClick = true
        };
        automaticUpdateItem.CheckedChanged += AutomaticUpdateItemOnCheckedChanged;
        allowanceActivationItem = new ToolStripMenuItem(AllowanceActivationMenuText)
        {
            Checked = usagePresentations.ActivationEnabled,
            CheckOnClick = true
        };
        allowanceActivationItem.CheckedChanged += AllowanceActivationItemOnCheckedChanged;
        allowanceNotificationsItem = new ToolStripMenuItem(AllowanceNotificationsMenuText)
        {
            Checked = usagePresentations.NotificationsEnabled,
            CheckOnClick = true
        };
        allowanceNotificationsItem.CheckedChanged += AllowanceNotificationsItemOnCheckedChanged;
        updateItem = new ToolStripMenuItem(CheckForUpdatesMenuText);

        var menu = CreateContextMenu(
            startupItem,
            automaticUpdateItem,
            allowanceActivationItem,
            allowanceNotificationsItem,
            updateItem,
            (_, _) => ShowPopup(),
            async (_, _) => await RequestAsync(UsageUpdateIntent.Activity),
            (_, _) => OpenUsagePage(),
            async (_, _) => await CheckForUpdatesAsync(UpdateCheckIntent.Manual),
            (_, _) => ExitThread());

        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.Visible = true;
        notifyIcon.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                popupVisibleWhenTrayMousePressed = popup.Visible;
            }
        };
        notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                var currentTimestamp = Environment.TickCount64;
                if (!ShouldHandleTrayClick(
                    currentTimestamp,
                    lastHandledTrayClickTimestamp,
                    SystemInformation.DoubleClickTime))
                {
                    return;
                }

                lastHandledTrayClickTimestamp = currentTimestamp;
                ShowPopup(popupVisibleWhenTrayMousePressed);
            }
        };

        popup.RefreshRequested += async (_, _) => await RequestAsync(UsageUpdateIntent.Activity);
        popup.UsagePageRequested += (_, _) => OpenUsagePage();
        popup.ExtendedViewActivated += async (_, _) => await RequestAsync(UsageUpdateIntent.Activity);
        refreshTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        refreshTimer.Tick += async (_, _) => await RequestAsync(UsageUpdateIntent.Routine);
        refreshTimer.Start();

        _ = RequestAsync(UsageUpdateIntent.Routine);
        if (automaticUpdateItem.Checked)
        {
            _ = CheckForUpdatesAsync(UpdateCheckIntent.Automatic);
        }
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        updateCancellation.Cancel();
        refreshTimer.Stop();
        notifyIcon.Visible = false;
        usagePresentations.DisposeAsync().AsTask().GetAwaiter().GetResult();
        notifyIcon.Dispose();
        currentIcon.Dispose();
        popup.Dispose();
        updateCancellation.Dispose();
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

    private void ShowPopup(bool? visibleWhenMousePressed = null)
    {
        if (!ShouldShowAfterTrayClick(visibleWhenMousePressed ?? popup.Visible))
        {
            popup.Hide();
            return;
        }

        popup.ShowNearTray();
        if (popup.IsExtendedView)
        {
            _ = RequestAsync(UsageUpdateIntent.Activity);
        }
    }

    internal static bool ShouldShowAfterTrayClick(bool visibleWhenMousePressed) => !visibleWhenMousePressed;

    internal static bool ShouldHandleTrayClick(long currentTimestamp, long? previousTimestamp, int doubleClickTime) =>
        previousTimestamp is null || currentTimestamp - previousTimestamp > doubleClickTime;

    internal static ContextMenuStrip CreateContextMenu(
        ToolStripMenuItem startupMenuItem,
        ToolStripMenuItem automaticUpdateMenuItem,
        ToolStripMenuItem allowanceActivationMenuItem,
        ToolStripMenuItem allowanceNotificationsMenuItem,
        ToolStripMenuItem updateMenuItem,
        EventHandler open,
        EventHandler refresh,
        EventHandler openUsagePage,
        EventHandler checkForUpdates,
        EventHandler exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, open);
        menu.Items.Add("Refresh", null, refresh);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupMenuItem);
        menu.Items.Add(automaticUpdateMenuItem);
        menu.Items.Add(allowanceActivationMenuItem);
        menu.Items.Add(allowanceNotificationsMenuItem);
        menu.Items.Add("Open Codex usage page", null, openUsagePage);
        menu.Items.Add(new ToolStripSeparator());
        updateMenuItem.Click += checkForUpdates;
        menu.Items.Add(updateMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, exit);
        return menu;
    }

    void IUsagePresentationSink.Present(UsagePresentation presentation)
    {
        if (popup.InvokeRequired)
        {
            popup.Invoke(() => ((IUsagePresentationSink)this).Present(presentation));
            return;
        }

        popup.ShowPresentation(presentation);
        UpdateTray(presentation.Tray);
        ShowNotices(presentation.Notices);
    }

    private void UpdateTray(UsagePresentation.TrayPresentation presentation)
    {
        var replacement = TrayIconRenderer.Create(
            presentation.FiveHourRemaining,
            presentation.WeeklyRemaining);
        notifyIcon.Icon = replacement;
        var old = currentIcon;
        currentIcon = replacement;
        old.Dispose();

        notifyIcon.Text = TruncateTooltip(presentation.Tooltip);
    }

    private void StartupItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        TryPersistCheckedSetting(startupItem, StartupItemOnCheckedChanged, StartupRegistration.SetEnabled);
    }

    private void AllowanceActivationItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        if (TryPersistCheckedSetting(
                allowanceActivationItem,
                AllowanceActivationItemOnCheckedChanged,
                enabled => usagePresentations.ActivationEnabled = enabled)
            && allowanceActivationItem.Checked)
        {
            _ = RequestAsync(UsageUpdateIntent.Routine);
        }
    }

    private void AutomaticUpdateItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        TryPersistCheckedSetting(
            automaticUpdateItem,
            AutomaticUpdateItemOnCheckedChanged,
            ApplicationUpdateSettings.SetAutomaticCheckEnabled);
    }

    private void AllowanceNotificationsItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        TryPersistCheckedSetting(
            allowanceNotificationsItem,
            AllowanceNotificationsItemOnCheckedChanged,
            enabled => usagePresentations.NotificationsEnabled = enabled);
    }

    private static bool TryPersistCheckedSetting(
        ToolStripMenuItem item,
        EventHandler changedHandler,
        Action<bool> persist)
    {
        try
        {
            persist(item.Checked);
            return true;
        }
        catch (Exception exception)
        {
            item.CheckedChanged -= changedHandler;
            item.Checked = !item.Checked;
            item.CheckedChanged += changedHandler;
            MessageBox.Show(exception.Message, "Codex usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void ShowNotices(IReadOnlyList<UsagePresentation.NoticePresentation> notices)
    {
        foreach (var notice in notices)
        {
            var icon = notice.Severity switch
            {
                UsagePresentation.NoticeSeverity.Information => ToolTipIcon.Info,
                UsagePresentation.NoticeSeverity.Warning => ToolTipIcon.Warning,
                _ => ToolTipIcon.None
            };
            notifyIcon.ShowBalloonTip(
                checked((int)notice.Duration.TotalMilliseconds),
                "Codex usage",
                notice.Message,
                icon);
        }
    }

    private static void OpenUsagePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://chatgpt.com/codex/settings/usage",
            UseShellExecute = true
        });
    }

    private async Task CheckForUpdatesAsync(UpdateCheckIntent intent)
    {
        if (updateCheckRunning)
        {
            return;
        }

        updateCheckRunning = true;
        updateItem.Enabled = false;
        updateItem.Text = "Checking for updates...";
        ApplicationUpdate? update = null;
        var installerStarted = false;
        var userAcceptedUpdate = false;
        try
        {
            var availableUpdate = await applicationUpdater.CheckAsync(updateCancellation.Token);
            if (availableUpdate is null)
            {
                if (intent == UpdateCheckIntent.Manual)
                {
                    MessageBox.Show(
                        $"Version {applicationUpdater.CurrentVersion.ToString(3)} is up to date.",
                        "Codex usage update",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return;
            }

            var choice = MessageBox.Show(
                $"Version {availableUpdate.Version.ToString(3)} is available. "
                    + $"You are using version {applicationUpdater.CurrentVersion.ToString(3)}.\n\n"
                    + "Download and install the update now?",
                "Codex usage update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.Yes)
            {
                return;
            }

            userAcceptedUpdate = true;
            updateItem.Text = $"Downloading version {availableUpdate.Version.ToString(3)}...";
            update = await applicationUpdater.DownloadAsync(availableUpdate, updateCancellation.Token);
            updateItem.Text = $"Installing version {update.Version.ToString(3)}...";
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The application executable path is unavailable.");
            UpdateInstaller.Launch(update, Environment.ProcessId, processPath);
            installerStarted = true;
            ExitThread();
        }
        catch (OperationCanceledException) when (exiting)
        {
        }
        catch (Exception exception)
        {
            if (intent == UpdateCheckIntent.Manual || userAcceptedUpdate)
            {
                MessageBox.Show(
                    $"The update failed.\n\n{exception.Message}",
                    "Codex usage update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!installerStarted && update is not null)
            {
                ApplicationUpdater.TryDelete(update.StagedPath);
            }

            if (!exiting)
            {
                updateCheckRunning = false;
                updateItem.Enabled = true;
                updateItem.Text = CheckForUpdatesMenuText;
            }
        }
    }

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private enum UpdateCheckIntent
    {
        Automatic,
        Manual
    }

}
