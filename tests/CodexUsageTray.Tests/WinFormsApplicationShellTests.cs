using System.Security.AccessControl;

namespace CodexUsageTray.Tests;

public sealed class WinFormsApplicationShellTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PopupButtonsRequestActivityAndKeepWorkingWhenPreferenceWritesAreDenied(bool denyWrites) =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var registry = new TestRegistryKey();
            var settings = new RegistryApplicationSettings(registry.Path) { CompactPopup = true };
            if (denyWrites)
            {
                registry.Deny(RegistryRights.SetValue);
            }

            using var popup = new UsagePopupForm(settings.CompactPopup, compact => settings.CompactPopup = compact)
            {
                Opacity = 0
            };
            var recording = new RecordingCommands();
            using var shell = CreateShell(recording, popup: popup);
            await shell.PerformTrayClickAsync(timestamp: 1_000, doubleClickTime: 500, cancellationToken);
            Assert.False(popup.IsExtendedView);
            Assert.Empty(recording.UsageRequests);
            var viewButton = popup.Controls.OfType<ViewModeIconButton>().Single();
            var todayTitle = popup.Controls.OfType<Label>().Single(label => label.Text == "Inference today");
            var compactHeight = popup.Height;

            viewButton.PerformClick();

            Assert.True(popup.IsExtendedView);
            Assert.True(todayTitle.Visible);
            Assert.True(popup.Height > compactHeight);
            Assert.Equal("Show compact view", viewButton.AccessibleName);
            Assert.Equal([UsageUpdateIntent.Activity], recording.UsageRequests);
            Assert.Equal(denyWrites, new RegistryApplicationSettings(registry.Path).CompactPopup);

            viewButton.PerformClick();

            Assert.False(popup.IsExtendedView);
            Assert.False(todayTitle.Visible);
            Assert.Equal(compactHeight, popup.Height);
            Assert.Equal("Show extended view", viewButton.AccessibleName);
            Assert.Equal([UsageUpdateIntent.Activity], recording.UsageRequests);
            Assert.True(new RegistryApplicationSettings(registry.Path).CompactPopup);

            var refreshButton = popup.Controls.OfType<RefreshIconButton>().Single();
            shell.Present(UsagePresentation.CreateLoading(previous: null));
            refreshButton.PerformClick();
            Assert.Single(recording.UsageRequests);
            shell.Present(UsagePresentation.CreateInitial());
            refreshButton.PerformClick();
            Assert.Equal([UsageUpdateIntent.Activity, UsageUpdateIntent.Activity], recording.UsageRequests);
        });

    [Fact]
    public Task MenuCommandsUseTypedCallbacksAndUsagePreferencesInitializeSilently() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var recording = new RecordingCommands();
            using var shell = CreateShell(recording, startupEnabled: true, automaticUpdateEnabled: false);

            shell.InitializeUsagePreferences(activationEnabled: true, notificationsEnabled: false);
            Assert.Empty(recording.ActivationSettings);
            Assert.Empty(recording.NotificationSettings);

            await shell.PerformMenuClickAsync("Refresh", cancellationToken);
            await shell.PerformMenuClickAsync("Start with Windows", cancellationToken);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.AutomaticUpdateMenuText, cancellationToken);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.AllowanceActivationMenuText, cancellationToken);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.AllowanceNotificationsMenuText, cancellationToken);
            await shell.PerformMenuClickAsync("Open Codex usage page", cancellationToken);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.ProjectReadmeMenuText, cancellationToken);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.LegalNoticesMenuText, cancellationToken);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.CheckForUpdatesMenuText, cancellationToken);
            await shell.PerformMenuClickAsync("Exit", cancellationToken);

            var state = await shell.CaptureStateAsync(cancellationToken);
            Assert.False(state.StartupEnabled);
            Assert.True(state.AutomaticUpdateEnabled);
            Assert.False(state.AllowanceActivationEnabled);
            Assert.True(state.AllowanceNotificationsEnabled);
            Assert.Equal([UsageUpdateIntent.Activity], recording.UsageRequests);
            Assert.Equal([ApplicationUpdateIntent.Manual], recording.ApplicationUpdateRequests);
            Assert.Equal([false], recording.StartupSettings);
            Assert.Equal([true], recording.AutomaticUpdateSettings);
            Assert.Equal([false], recording.ActivationSettings);
            Assert.Equal([true], recording.NotificationSettings);
            Assert.Equal(1, recording.UsagePageRequests);
            Assert.Equal(1, recording.ProjectReadmeRequests);
            Assert.Equal(1, recording.LegalNoticesRequests);
            Assert.Equal(1, recording.ExitRequests);
        });

    [Fact]
    public Task FailedSettingChangeRestoresTheMenuState() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var recording = new RecordingCommands
            {
                SetStartup = _ => throw new IOException("startup setting failed")
            };
            string? failure = null;
            using var shell = CreateShell(
                recording,
                startupEnabled: false,
                automaticUpdateEnabled: false,
                showSettingFailure: message => failure = message);

            await shell.PerformMenuClickAsync("Start with Windows", cancellationToken);

            var state = await shell.CaptureStateAsync(cancellationToken);
            Assert.False(state.StartupEnabled);
            Assert.Equal("startup setting failed", failure);
        });

    [Fact]
    public Task UsagePresentationUpdatesTheOwnedTrayState() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var shell = CreateShell(new RecordingCommands());
            var popup = new UsagePresentation.PopupPresentation(
                "Plus · Codex",
                UsagePresentation.AllowancePresentation.Show(75, "75% left", "Resets tomorrow", "1d"),
                UsagePresentation.AllowancePresentation.Show(60, "60% left", "Resets Friday", "4d"),
                "1.2K tokens",
                "4.5M tokens",
                "Updated 12:00",
                UsagePresentation.ActivityIndicatorPresentation.Unavailable);
            var tooltip = new string('x', 70);
            var notice = new UsagePresentation.NoticePresentation(
                "5-hour allowance reset.",
                UsagePresentation.NoticeSeverity.Information,
                TimeSpan.FromSeconds(5));
            var presentation = new UsagePresentation.Ready(
                popup,
                new UsagePresentation.TrayPresentation(75, 60, tooltip),
                [notice]);

            ((IUsagePresentationSink)shell).Present(presentation);

            var state = await shell.CaptureStateAsync(cancellationToken);
            Assert.Equal(tooltip[..63], state.TrayTooltip);
        });

    [Fact]
    public Task AuthenticationRecoveryConfirmsAndOpensTheSignInPage() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            Uri? openedPage = null;
            using var shell = CreateShell(
                new RecordingCommands(),
                confirmAndOpenSignIn: page =>
                {
                    openedPage = page;
                    return true;
                });
            var signInPage = new Uri("https://chatgpt.com/auth");

            var confirmed = await ((ICodexAuthenticationInteraction)shell)
                .ConfirmAndOpenSignInAsync(signInPage, cancellationToken);

            Assert.True(confirmed);
            Assert.Equal(signInPage, openedPage);
        });

    [Fact]
    public Task TrayClicksToggleThePopupAndSuppressTheSecondHalfOfADoubleClick() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var shell = CreateShell(new RecordingCommands());

            await shell.PerformTrayClickAsync(timestamp: 1_000, doubleClickTime: 500, cancellationToken);
            Assert.True((await shell.CaptureStateAsync(cancellationToken)).PopupVisible);

            await shell.PerformTrayClickAsync(timestamp: 1_100, doubleClickTime: 500, cancellationToken);
            Assert.True((await shell.CaptureStateAsync(cancellationToken)).PopupVisible);

            await shell.PerformTrayClickAsync(timestamp: 1_501, doubleClickTime: 500, cancellationToken);
            Assert.False((await shell.CaptureStateAsync(cancellationToken)).PopupVisible);
        });

    [Fact]
    public Task ApplicationUpdateProgressControlsTheOwnedMenuItem() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var shell = CreateShell(new RecordingCommands());
            var version = new Version(1, 3, 0);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Checking(),
                cancellationToken);
            var checking = await shell.CaptureStateAsync(cancellationToken);
            Assert.False(checking.UpdateEnabled);
            Assert.Equal("Checking for updates...", checking.UpdateText);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Downloading(version),
                cancellationToken);
            Assert.Equal(
                "Downloading version 1.3.0...",
                (await shell.CaptureStateAsync(cancellationToken)).UpdateText);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Installing(version),
                cancellationToken);
            Assert.Equal(
                "Installing version 1.3.0...",
                (await shell.CaptureStateAsync(cancellationToken)).UpdateText);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Idle(),
                cancellationToken);
            var idle = await shell.CaptureStateAsync(cancellationToken);
            Assert.True(idle.UpdateEnabled);
            Assert.Equal(WinFormsApplicationShell.CheckForUpdatesMenuText, idle.UpdateText);
        });

    [Fact]
    public Task CallsFromAWorkerThreadRunOnTheShellThread() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var shellThreadId = Environment.CurrentManagedThreadId;
            var exitThreadId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recording = new RecordingCommands
            {
                Exit = () => exitThreadId.TrySetResult(Environment.CurrentManagedThreadId)
            };
            using var shell = CreateShell(recording);

            await Task.Run(async () =>
            {
                Assert.NotEqual(shellThreadId, Environment.CurrentManagedThreadId);
                await ((IApplicationUpdateInteraction)shell).PresentAsync(
                    new ApplicationUpdatePresentation.Checking(),
                    cancellationToken);

                Assert.Equal(
                    "Checking for updates...",
                    (await shell.CaptureStateAsync(cancellationToken)).UpdateText);
                ((IApplicationUpdateInteraction)shell).ExitApplication();
                Assert.Equal(
                    shellThreadId,
                    await exitThreadId.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }, cancellationToken);

            Assert.Equal(shellThreadId, Environment.CurrentManagedThreadId);
        });

    [Fact]
    public Task DisposalClosesThePresentationSeam() =>
        StaTest.RunAsync(() =>
        {
            var shell = CreateShell(new RecordingCommands());
            shell.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => ((IUsagePresentationSink)shell).Present(UsagePresentation.CreateInitial()));
        });

    private static WinFormsApplicationShell CreateShell(
        RecordingCommands recording,
        bool startupEnabled = false,
        bool automaticUpdateEnabled = false,
        Action<string>? showSettingFailure = null,
        Func<Uri, bool>? confirmAndOpenSignIn = null,
        UsagePopupForm? popup = null) =>
        new(
            startupEnabled,
            automaticUpdateEnabled,
            new WinFormsApplicationShell.Commands(
                RequestUsageUpdate: intent =>
                {
                    recording.UsageRequests.Add(intent);
                    return Task.CompletedTask;
                },
                RequestApplicationUpdate: intent =>
                {
                    recording.ApplicationUpdateRequests.Add(intent);
                    return Task.CompletedTask;
                },
                SetStartupEnabled: enabled =>
                {
                    recording.StartupSettings.Add(enabled);
                    recording.SetStartup(enabled);
                },
                SetAutomaticUpdateEnabled: enabled => recording.AutomaticUpdateSettings.Add(enabled),
                SetAllowanceActivationEnabled: enabled => recording.ActivationSettings.Add(enabled),
                SetAllowanceNotificationsEnabled: enabled => recording.NotificationSettings.Add(enabled),
                OpenUsagePage: () => recording.UsagePageRequests++,
                OpenProjectReadme: () => recording.ProjectReadmeRequests++,
                OpenLegalNotices: () => recording.LegalNoticesRequests++,
                Exit: () =>
                {
                    recording.ExitRequests++;
                    recording.Exit();
                }),
            showSettingFailure,
            confirmAndOpenSignIn,
            popup ?? new UsagePopupForm(initialCompactView: false));

    private sealed class RecordingCommands
    {
        public List<UsageUpdateIntent> UsageRequests { get; } = [];
        public List<ApplicationUpdateIntent> ApplicationUpdateRequests { get; } = [];
        public List<bool> StartupSettings { get; } = [];
        public List<bool> AutomaticUpdateSettings { get; } = [];
        public List<bool> ActivationSettings { get; } = [];
        public List<bool> NotificationSettings { get; } = [];
        public int UsagePageRequests { get; set; }
        public int ProjectReadmeRequests { get; set; }
        public int LegalNoticesRequests { get; set; }
        public int ExitRequests { get; set; }
        public Action<bool> SetStartup { get; init; } = _ => { };
        public Action Exit { get; init; } = () => { };
    }
}
