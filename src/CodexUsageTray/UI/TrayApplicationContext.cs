using System.Diagnostics;

namespace CodexUsageTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly UsageUpdates usageUpdates;
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem windowStartItem;
    private readonly ToolStripMenuItem allowanceNotificationsItem;
    private Icon currentIcon;
    private bool popupVisibleWhenTrayMousePressed;
    private bool exiting;
    private int pendingRefreshes;
    private long? lastHandledTrayClickTimestamp;

    public TrayApplicationContext(UsageUpdates usageUpdates)
    {
        this.usageUpdates = usageUpdates;
        currentIcon = TrayIconRenderer.Create(100, 100);
        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        windowStartItem = new ToolStripMenuItem("Auto-activate unused windows with \"Hi\"")
        {
            Checked = usageUpdates.ActivationEnabled,
            CheckOnClick = true
        };
        windowStartItem.CheckedChanged += WindowStartItemOnCheckedChanged;
        allowanceNotificationsItem = new ToolStripMenuItem("Allowance notifications")
        {
            Checked = usageUpdates.NotificationsEnabled,
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
        exiting = true;
        refreshTimer.Stop();
        notifyIcon.Visible = false;
        usageUpdates.DisposeAsync().AsTask().GetAwaiter().GetResult();
        notifyIcon.Dispose();
        currentIcon.Dispose();
        popup.Dispose();
        base.ExitThreadCore();
    }

    private async Task<bool> RefreshAsync(bool includeActivity = false)
    {
        pendingRefreshes++;
        popup.SetLoading(true);
        try
        {
            var presentation = includeActivity
                ? await usageUpdates.RefreshWithActivityAsync()
                : await usageUpdates.RefreshAsync();
            popup.ShowPresentation(presentation.Popup);
            UpdateTray(presentation.Tray);
            ShowNotices(presentation.Notices);
            return true;
        }
        catch (OperationCanceledException) when (exiting)
        {
            return false;
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
            pendingRefreshes--;
            if (!exiting)
            {
                popup.SetLoading(pendingRefreshes > 0);
            }
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
            usageUpdates.ActivationEnabled = windowStartItem.Checked;
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
            usageUpdates.NotificationsEnabled = allowanceNotificationsItem.Checked;
        }
        catch (Exception exception)
        {
            allowanceNotificationsItem.CheckedChanged -= AllowanceNotificationsItemOnCheckedChanged;
            allowanceNotificationsItem.Checked = !allowanceNotificationsItem.Checked;
            allowanceNotificationsItem.CheckedChanged += AllowanceNotificationsItemOnCheckedChanged;
            MessageBox.Show(exception.Message, "Codex usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static string OneLine(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
