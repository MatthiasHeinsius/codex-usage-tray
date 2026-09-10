using System.Diagnostics;
using System.Globalization;

namespace CodexUsageTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly UsageSnapshots usageSnapshots;
    private readonly AllowanceWindowActivation activation;
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem windowStartItem;
    private readonly ToolStripMenuItem allowanceNotificationsItem;
    private Icon currentIcon;
    private bool popupVisibleWhenTrayMousePressed;
    private long? lastHandledTrayClickTimestamp;

    public TrayApplicationContext(
        UsageSnapshots usageSnapshots,
        AllowanceWindowActivation activation)
    {
        this.usageSnapshots = usageSnapshots;
        this.activation = activation;
        currentIcon = TrayIconRenderer.Create(100, 100);
        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        windowStartItem = new ToolStripMenuItem("Auto-activate unused windows with \"Hi\"")
        {
            Checked = activation.ActivationEnabled,
            CheckOnClick = true
        };
        windowStartItem.CheckedChanged += WindowStartItemOnCheckedChanged;
        allowanceNotificationsItem = new ToolStripMenuItem("Allowance notifications")
        {
            Checked = activation.NotificationsEnabled,
            CheckOnClick = true
        };
        allowanceNotificationsItem.CheckedChanged += AllowanceNotificationsItemOnCheckedChanged;

        var menu = CreateContextMenu(
            startupItem,
            windowStartItem,
            allowanceNotificationsItem,
            (_, _) => ShowPopup(),
            async (_, _) => await RefreshAsync(includeActivity: true),
            (_, _) => OpenUsagePage(),
            (_, _) => ExitThread());

        notifyIcon = new NotifyIcon
        {
            Icon = currentIcon,
            Text = "Codex usage · connecting",
            Visible = true,
            ContextMenuStrip = menu
        };
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

        popup.RefreshRequested += async (_, _) => await RefreshAsync(includeActivity: true);
        popup.UsagePageRequested += (_, _) => OpenUsagePage();
        popup.ExtendedViewActivated += async (_, _) => await RefreshAsync(includeActivity: true);
        refreshTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        refreshTimer.Start();

        _ = RefreshAsync();
    }

    protected override void ExitThreadCore()
    {
        refreshTimer.Stop();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        currentIcon.Dispose();
        popup.Dispose();
        usageSnapshots.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.ExitThreadCore();
    }

    private async Task<bool> RefreshAsync(bool includeActivity = false)
    {
        popup.SetLoading(true);
        try
        {
            var snapshot = includeActivity
                ? await usageSnapshots.RefreshWithActivityAsync()
                : await usageSnapshots.RefreshAsync();
            popup.ShowSnapshot(snapshot);
            UpdateTray(snapshot);
            var activationResult = await activation.ObserveAsync(snapshot);
            ShowAllowanceEvents(activationResult);
            return true;
        }
        catch (Exception exception)
        {
            var message = OneLine(exception.Message);
            popup.ShowError(message);
            notifyIcon.Text = TruncateTooltip($"Codex usage · {message}");
            return false;
        }
        finally
        {
            popup.SetLoading(false);
        }
    }

    private void ShowPopup(bool? visibleWhenMousePressed = null)
    {
        if (!ShouldShowAfterTrayClick(visibleWhenMousePressed ?? popup.Visible))
        {
            popup.Hide();
            return;
        }

        if (usageSnapshots.Current is { } snapshot)
        {
            popup.ShowSnapshot(snapshot);
        }

        popup.ShowNearTray();
        if (popup.IsExtendedView)
        {
            _ = RefreshAsync(includeActivity: true);
        }
    }

    internal static bool ShouldShowAfterTrayClick(bool visibleWhenMousePressed) => !visibleWhenMousePressed;

    internal static bool ShouldHandleTrayClick(long currentTimestamp, long? previousTimestamp, int doubleClickTime) =>
        previousTimestamp is null || currentTimestamp - previousTimestamp > doubleClickTime;

    internal static ContextMenuStrip CreateContextMenu(
        ToolStripMenuItem startupMenuItem,
        ToolStripMenuItem windowStartMenuItem,
        ToolStripMenuItem allowanceNotificationsMenuItem,
        EventHandler open,
        EventHandler refresh,
        EventHandler openUsagePage,
        EventHandler exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, open);
        menu.Items.Add("Refresh", null, refresh);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupMenuItem);
        menu.Items.Add(windowStartMenuItem);
        menu.Items.Add(allowanceNotificationsMenuItem);
        menu.Items.Add("Open Codex usage page", null, openUsagePage);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, exit);
        return menu;
    }

    private void UpdateTray(UsageSnapshot usage)
    {
        var presentation = UsagePresentation.Create(
            usage,
            DateTimeOffset.Now,
            CultureInfo.CurrentCulture).Tray;
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
        try
        {
            StartupRegistration.SetEnabled(startupItem.Checked);
        }
        catch (Exception exception)
        {
            startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
            startupItem.Checked = !startupItem.Checked;
            startupItem.CheckedChanged += StartupItemOnCheckedChanged;
            MessageBox.Show(exception.Message, "Codex usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void WindowStartItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            activation.ActivationEnabled = windowStartItem.Checked;
            if (windowStartItem.Checked)
            {
                _ = RefreshAsync();
            }
        }
        catch (Exception exception)
        {
            windowStartItem.CheckedChanged -= WindowStartItemOnCheckedChanged;
            windowStartItem.Checked = !windowStartItem.Checked;
            windowStartItem.CheckedChanged += WindowStartItemOnCheckedChanged;
            MessageBox.Show(exception.Message, "Codex usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AllowanceNotificationsItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            activation.NotificationsEnabled = allowanceNotificationsItem.Checked;
        }
        catch (Exception exception)
        {
            allowanceNotificationsItem.CheckedChanged -= AllowanceNotificationsItemOnCheckedChanged;
            allowanceNotificationsItem.Checked = !allowanceNotificationsItem.Checked;
            allowanceNotificationsItem.CheckedChanged += AllowanceNotificationsItemOnCheckedChanged;
            MessageBox.Show(exception.Message, "Codex usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowAllowanceEvents(AllowanceWindowActivationResult result)
    {
        if (allowanceNotificationsItem.Checked)
        {
            if (result.UsedUp != AllowanceWindows.None)
            {
                notifyIcon.ShowBalloonTip(
                    5000,
                    "Codex usage",
                    $"{AllowanceNames(result.UsedUp)} allowance used up.",
                    ToolTipIcon.Warning);
            }

            if (result.Reset != AllowanceWindows.None)
            {
                notifyIcon.ShowBalloonTip(
                    5000,
                    "Codex usage",
                    $"{AllowanceNames(result.Reset)} allowance reset.",
                    ToolTipIcon.Info);
            }
        }

        if (result.Unconfirmed != AllowanceWindows.None)
        {
            notifyIcon.ShowBalloonTip(
                7000,
                "Codex usage",
                $"Could not confirm {AllowanceNames(result.Unconfirmed).ToLowerInvariant()} allowance activation after four requests.",
                ToolTipIcon.Warning);
        }
    }

    private static string AllowanceNames(AllowanceWindows windows) => windows switch
    {
        AllowanceWindows.FiveHour => "5-hour",
        AllowanceWindows.Weekly => "Weekly",
        AllowanceWindows.FiveHour | AllowanceWindows.Weekly => "5-hour and weekly",
        _ => "Codex"
    };

    private static void OpenUsagePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://chatgpt.com/codex/settings/usage",
            UseShellExecute = true
        });
    }

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static string OneLine(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
