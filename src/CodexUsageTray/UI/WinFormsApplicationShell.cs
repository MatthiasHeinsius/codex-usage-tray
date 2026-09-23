using System.Diagnostics;

namespace CodexUsageTray;

internal sealed class WinFormsApplicationShell :
    IUsagePresentationSink,
    ICodexAuthenticationInteraction,
    IApplicationUpdateInteraction,
    IDisposable
{
    internal const string AllowanceActivationMenuText = "Auto-start allowance window";
    internal const string AllowanceNotificationsMenuText = "Notify on allowance changes";
    internal const string AutomaticUpdateMenuText = "Check for updates on startup";
    internal const string CheckForUpdatesMenuText = "Check for updates";
    internal const string LegalNoticesMenuText = "Open licenses and notices";
    internal const string ProjectReadmeMenuText = "Open README on GitHub";
    private const string UpdateDialogTitle = "Codex usage update";
    private readonly Commands commands;
    private readonly Action<string> showSettingFailure;
    private readonly Func<Uri, bool> confirmAndOpenSignIn;
    private readonly UsagePopupForm popup;
    private readonly GuidNotifyIcon notifyIcon;
    private readonly ContextMenuStrip contextMenu;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem automaticUpdateItem;
    private readonly ToolStripMenuItem allowanceActivationItem;
    private readonly ToolStripMenuItem allowanceNotificationsItem;
    private readonly ToolStripMenuItem updateItem;
    private Icon currentIcon;
    private bool popupVisibleWhenTrayMousePressed;
    private bool initializingUsagePreferences;
    private long? lastHandledTrayClickTimestamp;
    private int disposed;

    internal WinFormsApplicationShell(
        bool startupEnabled,
        bool automaticUpdateEnabled,
        Commands commands,
        Action<string>? showSettingFailure = null,
        Func<Uri, bool>? confirmAndOpenSignIn = null,
        UsagePopupForm? popupForm = null,
        Guid? trayIconId = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        this.commands = commands;
        this.showSettingFailure = showSettingFailure ?? ShowSettingFailure;
        this.confirmAndOpenSignIn = confirmAndOpenSignIn ?? ConfirmAndOpenSignIn;
        // The shell owns and disposes its popup.
        popup = popupForm ?? new UsagePopupForm();

        popup.CreateControl();
        _ = popup.Handle;
        currentIcon = TrayIconRenderer.Create(100, 100);
        notifyIcon = new GuidNotifyIcon(trayIconId)
        {
            Icon = currentIcon,
            Text = "Codex usage · connecting"
        };
        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = startupEnabled,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        automaticUpdateItem = new ToolStripMenuItem(AutomaticUpdateMenuText)
        {
            Checked = automaticUpdateEnabled,
            CheckOnClick = true
        };
        automaticUpdateItem.CheckedChanged += AutomaticUpdateItemOnCheckedChanged;
        allowanceActivationItem = new ToolStripMenuItem(AllowanceActivationMenuText)
        {
            CheckOnClick = true
        };
        allowanceActivationItem.CheckedChanged += AllowanceActivationItemOnCheckedChanged;
        allowanceNotificationsItem = new ToolStripMenuItem(AllowanceNotificationsMenuText)
        {
            CheckOnClick = true
        };
        allowanceNotificationsItem.CheckedChanged += AllowanceNotificationsItemOnCheckedChanged;
        updateItem = new ToolStripMenuItem(CheckForUpdatesMenuText);
        contextMenu = CreateContextMenu(
            startupItem,
            automaticUpdateItem,
            allowanceActivationItem,
            allowanceNotificationsItem,
            updateItem,
            open: (_, _) => ShowPopup(),
            refresh: async (_, _) => await commands.RequestUsageUpdate(UsageUpdateIntent.Activity),
            openUsagePage: (_, _) => commands.OpenUsagePage(),
            openProjectReadme: (_, _) => commands.OpenProjectReadme(),
            openLegalNotices: (_, _) => commands.OpenLegalNotices(),
            checkForUpdates: async (_, _) =>
                await commands.RequestApplicationUpdate(ApplicationUpdateIntent.Manual),
            exit: (_, _) => commands.Exit());

        notifyIcon.ContextMenuStrip = contextMenu;
        notifyIcon.MouseDown += NotifyIconOnMouseDown;
        notifyIcon.MouseClick += NotifyIconOnMouseClick;

        popup.RefreshRequested += async (_, _) =>
            await commands.RequestUsageUpdate(UsageUpdateIntent.Activity);
        popup.UsagePageRequested += (_, _) => commands.OpenUsagePage();
        popup.ExtendedViewActivated += async (_, _) =>
            await commands.RequestUsageUpdate(UsageUpdateIntent.Activity);
    }

    internal void InitializeUsagePreferences(bool activationEnabled, bool notificationsEnabled)
    {
        ThrowIfDisposed();
        initializingUsagePreferences = true;
        try
        {
            allowanceActivationItem.Checked = activationEnabled;
            allowanceNotificationsItem.Checked = notificationsEnabled;
        }
        finally
        {
            initializingUsagePreferences = false;
        }
    }

    internal void Activate()
    {
        ThrowIfDisposed();
        notifyIcon.Visible = true;
    }

    public void Present(UsagePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ThrowIfDisposed();
        if (popup.InvokeRequired)
        {
            popup.Invoke(() => Present(presentation));
            return;
        }

        popup.ShowPresentation(presentation);
        UpdateTray(presentation.Tray);
        ShowNotices(presentation.Notices);
    }

    public ValueTask PresentAsync(
        ApplicationUpdatePresentation presentation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ThrowIfDisposed();
        return InvokeOnUiThreadAsync(() => PresentOnUiThread(presentation), cancellationToken);
    }

    public ValueTask<bool> ConfirmAsync(
        ApplicationUpdateOffer offer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ThrowIfDisposed();
        return InvokeOnUiThreadAsync(() => MessageBox.Show(
            $"Version {offer.AvailableVersion.ToString(3)} is available. "
                + $"You are using version {offer.CurrentVersion.ToString(3)}.\n\n"
                + "Download and install the update now?",
            UpdateDialogTitle,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes,
            cancellationToken);
    }

    public ValueTask<bool> ConfirmAndOpenSignInAsync(
        Uri signInPage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signInPage);
        ThrowIfDisposed();
        return InvokeOnUiThreadAsync(
            () => confirmAndOpenSignIn(signInPage),
            cancellationToken);
    }

    public void ExitApplication()
    {
        ThrowIfDisposed();
        popup.BeginInvoke(commands.Exit);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        contextMenu.Dispose();
        currentIcon.Dispose();
        popup.Dispose();
    }

    internal ValueTask<State> CaptureStateAsync(CancellationToken cancellationToken = default) =>
        InvokeOnUiThreadAsync(
            () => new State(
                startupItem.Checked,
                automaticUpdateItem.Checked,
                allowanceActivationItem.Checked,
                allowanceNotificationsItem.Checked,
                updateItem.Enabled,
                updateItem.Text ?? string.Empty,
                notifyIcon.Text,
                popup.Visible),
            cancellationToken);

    internal ValueTask PerformMenuClickAsync(string text, CancellationToken cancellationToken = default) =>
        InvokeOnUiThreadAsync(
            () => contextMenu.Items
                .OfType<ToolStripMenuItem>()
                .Single(item => item.Text == text)
                .PerformClick(),
            cancellationToken);

    internal ValueTask PerformTrayClickAsync(
        long timestamp,
        int doubleClickTime,
        CancellationToken cancellationToken = default) =>
        InvokeOnUiThreadAsync(
            () =>
            {
                popupVisibleWhenTrayMousePressed = popup.Visible;
                HandleTrayClick(timestamp, doubleClickTime);
            },
            cancellationToken);

    internal static ContextMenuStrip CreateContextMenuForScreenshot()
    {
        var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        var automaticUpdate = new ToolStripMenuItem(AutomaticUpdateMenuText) { CheckOnClick = true };
        var allowanceActivation = new ToolStripMenuItem(AllowanceActivationMenuText) { CheckOnClick = true };
        var allowanceNotifications = new ToolStripMenuItem(AllowanceNotificationsMenuText) { CheckOnClick = true };
        var update = new ToolStripMenuItem(CheckForUpdatesMenuText);
        EventHandler noOp = (_, _) => { };
        return CreateContextMenu(
            startup,
            automaticUpdate,
            allowanceActivation,
            allowanceNotifications,
            update,
            noOp,
            noOp,
            noOp,
            noOp,
            noOp,
            noOp,
            noOp);
    }

    private static ContextMenuStrip CreateContextMenu(
        ToolStripMenuItem startup,
        ToolStripMenuItem automaticUpdate,
        ToolStripMenuItem allowanceActivation,
        ToolStripMenuItem allowanceNotifications,
        ToolStripMenuItem update,
        EventHandler open,
        EventHandler refresh,
        EventHandler openUsagePage,
        EventHandler openProjectReadme,
        EventHandler openLegalNotices,
        EventHandler checkForUpdates,
        EventHandler exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, open);
        menu.Items.Add("Refresh", null, refresh);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startup);
        menu.Items.Add(automaticUpdate);
        menu.Items.Add(allowanceActivation);
        menu.Items.Add(allowanceNotifications);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Codex usage page", null, openUsagePage);
        menu.Items.Add(ProjectReadmeMenuText, null, openProjectReadme);
        menu.Items.Add(LegalNoticesMenuText, null, openLegalNotices);
        menu.Items.Add(new ToolStripSeparator());
        update.Click += checkForUpdates;
        menu.Items.Add(update);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, exit);
        return menu;
    }

    private void ShowPopup(bool? visibleWhenMousePressed = null)
    {
        if (visibleWhenMousePressed ?? popup.Visible)
        {
            popup.Hide();
            return;
        }

        popup.ShowNearTray();
        if (popup.IsExtendedView)
        {
            _ = commands.RequestUsageUpdate(UsageUpdateIntent.Activity);
        }
    }

    private void NotifyIconOnMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            popupVisibleWhenTrayMousePressed = popup.Visible;
        }
    }

    private void NotifyIconOnMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            HandleTrayClick(Environment.TickCount64, SystemInformation.DoubleClickTime);
        }
    }

    private void HandleTrayClick(long currentTimestamp, int doubleClickTime)
    {
        if (!ShouldHandleTrayClick(currentTimestamp, lastHandledTrayClickTimestamp, doubleClickTime))
        {
            return;
        }

        lastHandledTrayClickTimestamp = currentTimestamp;
        ShowPopup(popupVisibleWhenTrayMousePressed);
    }

    private static bool ShouldHandleTrayClick(long currentTimestamp, long? previousTimestamp, int doubleClickTime) =>
        previousTimestamp is null || currentTimestamp - previousTimestamp > doubleClickTime;

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
        TryPersistCheckedSetting(startupItem, StartupItemOnCheckedChanged, commands.SetStartupEnabled);
    }

    private void AutomaticUpdateItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        TryPersistCheckedSetting(
            automaticUpdateItem,
            AutomaticUpdateItemOnCheckedChanged,
            commands.SetAutomaticUpdateEnabled);
    }

    private void AllowanceActivationItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        if (!initializingUsagePreferences)
        {
            TryPersistCheckedSetting(
                allowanceActivationItem,
                AllowanceActivationItemOnCheckedChanged,
                commands.SetAllowanceActivationEnabled);
        }
    }

    private void AllowanceNotificationsItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        if (!initializingUsagePreferences)
        {
            TryPersistCheckedSetting(
                allowanceNotificationsItem,
                AllowanceNotificationsItemOnCheckedChanged,
                commands.SetAllowanceNotificationsEnabled);
        }
    }

    private void TryPersistCheckedSetting(
        ToolStripMenuItem item,
        EventHandler changedHandler,
        Action<bool> persist)
    {
        try
        {
            persist(item.Checked);
        }
        catch (Exception exception)
        {
            item.CheckedChanged -= changedHandler;
            item.Checked = !item.Checked;
            item.CheckedChanged += changedHandler;
            showSettingFailure(exception.Message);
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

    private void PresentOnUiThread(ApplicationUpdatePresentation presentation)
    {
        switch (presentation)
        {
            case ApplicationUpdatePresentation.Idle:
                updateItem.Enabled = true;
                updateItem.Text = CheckForUpdatesMenuText;
                break;
            case ApplicationUpdatePresentation.Checking:
                updateItem.Enabled = false;
                updateItem.Text = "Checking for updates...";
                break;
            case ApplicationUpdatePresentation.Downloading downloading:
                updateItem.Text = $"Downloading version {downloading.Version.ToString(3)}...";
                break;
            case ApplicationUpdatePresentation.Installing installing:
                updateItem.Text = $"Installing version {installing.Version.ToString(3)}...";
                break;
            case ApplicationUpdatePresentation.Current current:
                MessageBox.Show(
                    $"Version {current.Version.ToString(3)} is up to date.",
                    UpdateDialogTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            case ApplicationUpdatePresentation.Failed failed:
                MessageBox.Show(
                    $"The update failed.\n\n{failed.Message}",
                    UpdateDialogTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(presentation),
                    presentation,
                    "Unknown Application Update presentation.");
        }
    }

    private ValueTask InvokeOnUiThreadAsync(Action action, CancellationToken cancellationToken)
    {
        if (popup.InvokeRequired)
        {
            return new ValueTask(popup.InvokeAsync(action, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        action();
        return ValueTask.CompletedTask;
    }

    private ValueTask<T> InvokeOnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (popup.InvokeRequired)
        {
            return new ValueTask<T>(popup.InvokeAsync(action, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(action());
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static void ShowSettingFailure(string message) => MessageBox.Show(
        message,
        "Codex usage",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);

    private static bool ConfirmAndOpenSignIn(Uri signInPage)
    {
        var confirmed = MessageBox.Show(
            "Your Codex sign-in expired and could not be refreshed automatically.\n\n"
                + "Codex Usage Tray can open this sign-in page in your browser:\n\n"
                + $"{signInPage.AbsoluteUri}\n\nContinue?",
            "Reconnect Codex",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (!confirmed)
        {
            return false;
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = signInPage.AbsoluteUri,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("The ChatGPT sign-in page could not be opened.");
        return true;
    }

    internal sealed record Commands(
        Func<UsageUpdateIntent, Task> RequestUsageUpdate,
        Func<ApplicationUpdateIntent, Task> RequestApplicationUpdate,
        Action<bool> SetStartupEnabled,
        Action<bool> SetAutomaticUpdateEnabled,
        Action<bool> SetAllowanceActivationEnabled,
        Action<bool> SetAllowanceNotificationsEnabled,
        Action OpenUsagePage,
        Action OpenProjectReadme,
        Action OpenLegalNotices,
        Action Exit);

    internal sealed record State(
        bool StartupEnabled,
        bool AutomaticUpdateEnabled,
        bool AllowanceActivationEnabled,
        bool AllowanceNotificationsEnabled,
        bool UpdateEnabled,
        string UpdateText,
        string TrayTooltip,
        bool PopupVisible);
}
