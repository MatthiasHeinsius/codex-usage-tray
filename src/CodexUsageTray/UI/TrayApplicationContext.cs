using System.Diagnostics;
using System.Globalization;

namespace CodexUsageTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly UsageSnapshots usageSnapshots;
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer expiryTimer;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem windowStartItem;
    private Icon currentIcon;
    private bool windowStartInProgress;
    private DateTimeOffset retryWindowStartAfter = DateTimeOffset.MinValue;
    private bool popupVisibleWhenTrayMousePressed;
    private long? lastHandledTrayClickTimestamp;

    public TrayApplicationContext(UsageSnapshots usageSnapshots)
    {
        this.usageSnapshots = usageSnapshots;
        currentIcon = TrayIconRenderer.Create(100, 100);
        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        windowStartItem = new ToolStripMenuItem("Auto-start expired windows with \"Hi\"")
        {
            Checked = WindowStartSettings.IsEnabled(),
            CheckOnClick = true
        };
        windowStartItem.CheckedChanged += WindowStartItemOnCheckedChanged;

        var menu = CreateContextMenu(
            startupItem,
            windowStartItem,
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
        expiryTimer = new System.Windows.Forms.Timer { Interval = 15 * 1000 };
        expiryTimer.Tick += async (_, _) => await CheckExpiredWindowsAsync();
        expiryTimer.Start();

        _ = RefreshAsync();
    }

    protected override void ExitThreadCore()
    {
        refreshTimer.Stop();
        expiryTimer.Stop();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        currentIcon.Dispose();
        popup.Dispose();
        usageSnapshots.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.ExitThreadCore();
    }

    private async Task CheckExpiredWindowsAsync()
    {
        if (!windowStartItem.Checked
            || windowStartInProgress
            || usageSnapshots.Current is not { } latest
            || DateTimeOffset.Now < retryWindowStartAfter
            || !HasExpiredWindow(latest, DateTimeOffset.Now))
        {
            return;
        }

        var observed = latest;
        var observedAt = DateTimeOffset.Now;
        var startFiveHour = WindowStartSettings.ShouldStartFiveHour(observed.FiveHour, observedAt);
        var startWeekly = WindowStartSettings.ShouldStartWeekly(observed.Weekly, observedAt);
        if (!startFiveHour && !startWeekly)
        {
            return;
        }

        windowStartInProgress = true;
        try
        {
            // Re-read first in case another Codex client already started the new window.
            if (!await RefreshAsync(checkExpiredWindows: false))
            {
                retryWindowStartAfter = DateTimeOffset.Now.AddMinutes(1);
                return;
            }

            if (usageSnapshots.Current is not { } current)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            startFiveHour = startFiveHour
                && WindowStartSettings.ShouldStartFiveHourAfterRefresh(observed.FiveHour, current.FiveHour, now);
            startWeekly = startWeekly
                && WindowStartSettings.ShouldStartWeeklyAfterRefresh(observed.Weekly, current.Weekly, now);
            if (!startFiveHour && !startWeekly)
            {
                return;
            }

            var windowNames = startFiveHour && startWeekly
                ? "5-hour and weekly windows"
                : startFiveHour ? "5-hour window" : "weekly window";
            await CodexWindowStarter.SendHiAsync(CancellationToken.None);
            WindowStartSettings.MarkStarted(startFiveHour, startWeekly, observed);
            retryWindowStartAfter = DateTimeOffset.MinValue;
            notifyIcon.ShowBalloonTip(
                5000,
                "Codex usage",
                $"Started new {windowNames} with \"Hi\".",
                ToolTipIcon.Info);

            await Task.Delay(1000);
            await RefreshAsync(checkExpiredWindows: false);
        }
        catch (Exception exception)
        {
            retryWindowStartAfter = DateTimeOffset.Now.AddMinutes(5);
            var message = OneLine(exception.Message);
            notifyIcon.ShowBalloonTip(7000, "Codex usage", message, ToolTipIcon.Warning);
        }
        finally
        {
            windowStartInProgress = false;
        }
    }

    private async Task<bool> RefreshAsync(bool includeActivity = false, bool checkExpiredWindows = true)
    {
        if (checkExpiredWindows)
        {
            await CheckExpiredWindowsAsync();
        }

        popup.SetLoading(true);
        try
        {
            var snapshot = includeActivity
                ? await usageSnapshots.RefreshWithActivityAsync()
                : await usageSnapshots.RefreshAsync();
            popup.ShowSnapshot(snapshot);
            UpdateTray(snapshot);
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
            WindowStartSettings.SetEnabled(windowStartItem.Checked);
            if (windowStartItem.Checked)
            {
                _ = CheckExpiredWindowsAsync();
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

    private static void OpenUsagePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://chatgpt.com/codex/settings/usage",
            UseShellExecute = true
        });
    }

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static bool HasExpiredWindow(UsageSnapshot usage, DateTimeOffset now) =>
        usage.FiveHour?.ResetsAt <= now || usage.Weekly?.ResetsAt <= now;

    private static string OneLine(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
