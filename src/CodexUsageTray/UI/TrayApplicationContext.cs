using System.Diagnostics;
using System.Globalization;

namespace CodexUsageTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CodexAppServerClient client = new();
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer expiryTimer;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem windowStartItem;
    private Icon currentIcon;
    private bool refreshInProgress;
    private bool windowStartInProgress;
    private DateTimeOffset retryWindowStartAfter = DateTimeOffset.MinValue;
    private UsageSnapshot? snapshot;

    public TrayApplicationContext()
    {
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

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowPopup());
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(windowStartItem);
        menu.Items.Add("Open Codex usage page", null, (_, _) => OpenUsagePage());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        notifyIcon = new NotifyIcon
        {
            Icon = currentIcon,
            Text = "Codex usage · connecting",
            Visible = true,
            ContextMenuStrip = menu
        };
        notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                ShowPopup();
            }
        };

        popup.RefreshRequested += async (_, _) => await RefreshAsync();
        popup.UsagePageRequested += (_, _) => OpenUsagePage();
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
        base.ExitThreadCore();
    }

    private async Task CheckExpiredWindowsAsync()
    {
        if (!windowStartItem.Checked
            || windowStartInProgress
            || refreshInProgress
            || snapshot is null
            || DateTimeOffset.Now < retryWindowStartAfter
            || !HasExpiredWindow(snapshot, DateTimeOffset.Now))
        {
            return;
        }

        windowStartInProgress = true;
        try
        {
            // Re-read first in case another Codex client already started the new window.
            await RefreshAsync();
            if (snapshot is not { } current)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var startFiveHour = WindowStartSettings.ShouldStartFiveHour(current.FiveHour, now);
            var startWeekly = WindowStartSettings.ShouldStartWeekly(current.Weekly, now);
            if (!startFiveHour && !startWeekly)
            {
                return;
            }

            var windowNames = startFiveHour && startWeekly
                ? "5-hour and weekly windows"
                : startFiveHour ? "5-hour window" : "weekly window";
            await CodexWindowStarter.SendHiAsync(CancellationToken.None);
            WindowStartSettings.MarkStarted(startFiveHour, startWeekly, current);
            retryWindowStartAfter = DateTimeOffset.MinValue;
            notifyIcon.ShowBalloonTip(
                5000,
                "Codex usage",
                $"Started new {windowNames} with \"Hi\".",
                ToolTipIcon.Info);

            await Task.Delay(1000);
            await RefreshAsync();
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

    private async Task RefreshAsync()
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        popup.SetLoading(true);
        try
        {
            snapshot = await client.ReadUsageAsync(CancellationToken.None);
            popup.ShowSnapshot(snapshot);
            UpdateTray(snapshot);
        }
        catch (Exception exception)
        {
            var message = OneLine(exception.Message);
            popup.ShowError(message);
            notifyIcon.Text = TruncateTooltip($"Codex usage · {message}");
        }
        finally
        {
            popup.SetLoading(false);
            refreshInProgress = false;
        }
    }

    private void ShowPopup()
    {
        if (popup.Visible)
        {
            popup.Hide();
            return;
        }

        if (snapshot is not null)
        {
            popup.ShowSnapshot(snapshot);
        }

        popup.ShowNearTray();
    }

    private void UpdateTray(UsageSnapshot usage)
    {
        var fiveHourRemaining = usage.FiveHour?.RemainingPercent ?? 100;
        var weeklyRemaining = usage.Weekly?.RemainingPercent ?? 100;
        var replacement = TrayIconRenderer.Create(fiveHourRemaining, weeklyRemaining);
        notifyIcon.Icon = replacement;
        var old = currentIcon;
        currentIcon = replacement;
        old.Dispose();

        notifyIcon.Text = TruncateTooltip(
            $"Codex · 5h {usage.FiveHour?.RemainingPercent.ToString(CultureInfo.InvariantCulture) ?? "?"}% · week {usage.Weekly?.RemainingPercent.ToString(CultureInfo.InvariantCulture) ?? "?"}% · total {UsageText.Tokens(usage.LifetimeTokens)}");
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
