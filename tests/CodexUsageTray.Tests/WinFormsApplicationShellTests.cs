namespace CodexUsageTray.Tests;

public sealed class WinFormsApplicationShellTests
{
    [Fact]
    public Task MenuCommandsUseTypedCallbacksAndUsagePreferencesInitializeSilently() =>
        RunInStaThreadAsync(async () =>
        {
            var recording = new RecordingCommands();
            using var shell = CreateShell(recording, startupEnabled: true, automaticUpdateEnabled: false);

            shell.InitializeUsagePreferences(activationEnabled: true, notificationsEnabled: false);
            Assert.Empty(recording.ActivationSettings);
            Assert.Empty(recording.NotificationSettings);

            await shell.PerformMenuClickAsync("Refresh");
            await shell.PerformMenuClickAsync("Start with Windows");
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.AutomaticUpdateMenuText);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.AllowanceActivationMenuText);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.AllowanceNotificationsMenuText);
            await shell.PerformMenuClickAsync("Open Codex usage page");
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.ProjectReadmeMenuText);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.LegalNoticesMenuText);
            await shell.PerformMenuClickAsync(WinFormsApplicationShell.CheckForUpdatesMenuText);
            await shell.PerformMenuClickAsync("Exit");

            var state = await shell.CaptureStateAsync();
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
        RunInStaThreadAsync(async () =>
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

            await shell.PerformMenuClickAsync("Start with Windows");

            var state = await shell.CaptureStateAsync();
            Assert.False(state.StartupEnabled);
            Assert.Equal("startup setting failed", failure);
        });

    [Fact]
    public Task UsagePresentationUpdatesTheOwnedTrayState() =>
        RunInStaThreadAsync(async () =>
        {
            using var shell = CreateShell(new RecordingCommands());
            var popup = new UsagePresentation.PopupPresentation(
                "Plus · Codex",
                new UsagePresentation.AllowancePresentation(75, "75% left", "Resets tomorrow", "1d"),
                new UsagePresentation.AllowancePresentation(60, "60% left", "Resets Friday", "4d"),
                "1.2K tokens",
                "4.5M tokens",
                "Updated 12:00");
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

            var state = await shell.CaptureStateAsync();
            Assert.Equal(tooltip[..63], state.TrayTooltip);
        });

    [Fact]
    public Task TrayClicksToggleThePopupAndSuppressTheSecondHalfOfADoubleClick() =>
        RunInStaThreadAsync(async () =>
        {
            using var shell = CreateShell(new RecordingCommands());

            await shell.PerformTrayClickAsync(timestamp: 1_000, doubleClickTime: 500);
            Assert.True((await shell.CaptureStateAsync()).PopupVisible);

            await shell.PerformTrayClickAsync(timestamp: 1_100, doubleClickTime: 500);
            Assert.True((await shell.CaptureStateAsync()).PopupVisible);

            await shell.PerformTrayClickAsync(timestamp: 1_501, doubleClickTime: 500);
            Assert.False((await shell.CaptureStateAsync()).PopupVisible);
        });

    [Fact]
    public Task ApplicationUpdateProgressControlsTheOwnedMenuItem() =>
        RunInStaThreadAsync(async () =>
        {
            using var shell = CreateShell(new RecordingCommands());
            var version = new Version(1, 3, 0);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Checking(),
                TestContext.Current.CancellationToken);
            var checking = await shell.CaptureStateAsync();
            Assert.False(checking.UpdateEnabled);
            Assert.Equal("Checking for updates...", checking.UpdateText);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Downloading(version),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "Downloading version 1.3.0...",
                (await shell.CaptureStateAsync()).UpdateText);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Installing(version),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "Installing version 1.3.0...",
                (await shell.CaptureStateAsync()).UpdateText);

            await ((IApplicationUpdateInteraction)shell).PresentAsync(
                new ApplicationUpdatePresentation.Idle(),
                TestContext.Current.CancellationToken);
            var idle = await shell.CaptureStateAsync();
            Assert.True(idle.UpdateEnabled);
            Assert.Equal(WinFormsApplicationShell.CheckForUpdatesMenuText, idle.UpdateText);
        });

    [Fact]
    public async Task CallsFromAWorkerThreadRunOnTheShellThread()
    {
        var ready = new TaskCompletionSource<ShellThreadState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exitThreadId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var recording = new RecordingCommands
                {
                    Exit = () =>
                    {
                        exitThreadId.TrySetResult(Environment.CurrentManagedThreadId);
                        Application.ExitThread();
                    }
                };
                using var shell = CreateShell(recording);
                ready.TrySetResult(new ShellThreadState(shell, Environment.CurrentManagedThreadId));
                Application.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                exitThreadId.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var state = await ready.Task.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            Assert.NotEqual(Environment.CurrentManagedThreadId, state.ThreadId);
            await ((IApplicationUpdateInteraction)state.Shell).PresentAsync(
                new ApplicationUpdatePresentation.Checking(),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "Checking for updates...",
                (await state.Shell.CaptureStateAsync(TestContext.Current.CancellationToken)).UpdateText);
            ((IApplicationUpdateInteraction)state.Shell).ExitApplication();
            Assert.Equal(
                state.ThreadId,
                await exitThreadId.Task.WaitAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (thread.IsAlive)
            {
                ((IApplicationUpdateInteraction)state.Shell).ExitApplication();
                Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            }
        }
    }

    [Fact]
    public Task DisposalClosesThePresentationSeam() =>
        RunInStaThreadAsync(() =>
        {
            var shell = CreateShell(new RecordingCommands());
            shell.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => ((IUsagePresentationSink)shell).Present(UsagePresentation.CreateInitial()));
            return Task.CompletedTask;
        });

    private static WinFormsApplicationShell CreateShell(
        RecordingCommands recording,
        bool startupEnabled = false,
        bool automaticUpdateEnabled = false,
        Action<string>? showSettingFailure = null) =>
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
            showSettingFailure);

    private static Task RunInStaThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

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

    private sealed record ShellThreadState(WinFormsApplicationShell Shell, int ThreadId);
}
