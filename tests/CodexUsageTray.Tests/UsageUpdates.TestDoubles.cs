namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    private sealed class NoOpActivationCommand : IAllowanceWindowActivationCommand
    {
        public Task SendHiAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DisabledActivationSettings : IAllowanceWindowActivationSettings
    {
        public bool ActivationEnabled { get; set; }
        public bool NotificationsEnabled { get; set; }
        public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window) => null;

        public void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset)
        {
        }
    }
}
