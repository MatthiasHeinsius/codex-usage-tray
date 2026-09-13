namespace CodexUsageTray;

internal sealed class WinFormsApplicationUpdateInteraction : IApplicationUpdateInteraction
{
    internal const string CheckForUpdatesMenuText = "Check for updates";
    private const string DialogTitle = "Codex usage update";
    private readonly Control dispatcher;
    private readonly ToolStripMenuItem updateItem;
    private readonly Action exitApplication;

    internal WinFormsApplicationUpdateInteraction(
        Control dispatcher,
        ToolStripMenuItem updateItem,
        Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(updateItem);
        ArgumentNullException.ThrowIfNull(exitApplication);
        this.dispatcher = dispatcher;
        this.updateItem = updateItem;
        this.exitApplication = exitApplication;
    }

    public ValueTask PresentAsync(
        ApplicationUpdatePresentation presentation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return InvokeOnUiThreadAsync(() => PresentOnUiThread(presentation), cancellationToken);
    }

    public ValueTask<bool> ConfirmAsync(
        ApplicationUpdateOffer offer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return InvokeOnUiThreadAsync(() => MessageBox.Show(
            $"Version {offer.AvailableVersion.ToString(3)} is available. "
                + $"You are using version {offer.CurrentVersion.ToString(3)}.\n\n"
                + "Download and install the update now?",
            DialogTitle,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes,
            cancellationToken);
    }

    public void ExitApplication()
    {
        dispatcher.BeginInvoke(exitApplication);
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
                    DialogTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            case ApplicationUpdatePresentation.Failed failed:
                MessageBox.Show(
                    $"The update failed.\n\n{failed.Message}",
                    DialogTitle,
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
        if (dispatcher.InvokeRequired)
        {
            return new ValueTask(dispatcher.InvokeAsync(action, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        action();
        return ValueTask.CompletedTask;
    }

    private ValueTask<T> InvokeOnUiThreadAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        if (dispatcher.InvokeRequired)
        {
            return new ValueTask<T>(dispatcher.InvokeAsync(action, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(action());
    }
}
