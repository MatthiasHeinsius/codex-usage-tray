namespace CodexUsageTray.Tests;

public sealed class WinFormsApplicationUpdateInteractionTests
{
    [Fact]
    public async Task ProgressPresentationsControlTheUpdateMenuItem()
    {
        using var dispatcher = new Control();
        using var updateItem = new ToolStripMenuItem();
        var interaction = new WinFormsApplicationUpdateInteraction(
            dispatcher,
            updateItem,
            () => { });
        var version = new Version(1, 3, 0);

        await interaction.PresentAsync(
            new ApplicationUpdatePresentation.Checking(),
            TestContext.Current.CancellationToken);
        Assert.False(updateItem.Enabled);
        Assert.Equal("Checking for updates...", updateItem.Text);

        await interaction.PresentAsync(
            new ApplicationUpdatePresentation.Downloading(version),
            TestContext.Current.CancellationToken);
        Assert.False(updateItem.Enabled);
        Assert.Equal("Downloading version 1.3.0...", updateItem.Text);

        await interaction.PresentAsync(
            new ApplicationUpdatePresentation.Installing(version),
            TestContext.Current.CancellationToken);
        Assert.False(updateItem.Enabled);
        Assert.Equal("Installing version 1.3.0...", updateItem.Text);

        await interaction.PresentAsync(
            new ApplicationUpdatePresentation.Idle(),
            TestContext.Current.CancellationToken);
        Assert.True(updateItem.Enabled);
        Assert.Equal(WinFormsApplicationUpdateInteraction.CheckForUpdatesMenuText, updateItem.Text);
    }

    [Fact]
    public async Task CallsFromAWorkerThreadRunOnTheDispatcherThread()
    {
        var ready = new TaskCompletionSource<DispatcherState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exitThreadId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dispatcher = new Control();
                dispatcher.CreateControl();
                using var updateItem = new ToolStripMenuItem();
                var dispatcherThreadId = Environment.CurrentManagedThreadId;
                var interaction = new WinFormsApplicationUpdateInteraction(
                    dispatcher,
                    updateItem,
                    () =>
                    {
                        exitThreadId.TrySetResult(Environment.CurrentManagedThreadId);
                        Application.ExitThread();
                    });
                ready.TrySetResult(new DispatcherState(
                    dispatcher,
                    updateItem,
                    interaction,
                    dispatcherThreadId));
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
            Assert.NotEqual(Environment.CurrentManagedThreadId, state.DispatcherThreadId);
            await state.Interaction.PresentAsync(
                new ApplicationUpdatePresentation.Checking(),
                TestContext.Current.CancellationToken);
            var menuText = await state.Dispatcher.InvokeAsync(
                () => state.UpdateItem.Text,
                TestContext.Current.CancellationToken);

            Assert.Equal("Checking for updates...", menuText);
            state.Interaction.ExitApplication();
            Assert.Equal(
                state.DispatcherThreadId,
                await exitThreadId.Task.WaitAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (thread.IsAlive)
            {
                state.Dispatcher.BeginInvoke((Action)Application.ExitThread);
                Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            }
        }
    }

    private sealed record DispatcherState(
        Control Dispatcher,
        ToolStripMenuItem UpdateItem,
        WinFormsApplicationUpdateInteraction Interaction,
        int DispatcherThreadId);
}
