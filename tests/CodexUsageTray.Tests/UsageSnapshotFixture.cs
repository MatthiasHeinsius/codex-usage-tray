namespace CodexUsageTray.Tests;

internal static class UsageSnapshotFixture
{
    public static UsageSnapshot Create(
        AccountUsageObservation account,
        LocalUsageObservation? local = null) =>
        Create(new UsageObservations(account, local));

    public static UsageSnapshot Create(params UsageObservations[] observations)
    {
        if (observations.Length == 0)
        {
            throw new ArgumentException("At least one Usage Observation is required.", nameof(observations));
        }

        var reader = new QueueUsageObservationReader(observations);
        var snapshots = new UsageSnapshots(reader);
        try
        {
            UsageSnapshot? snapshot = null;
            foreach (var observation in observations)
            {
                snapshot = observation.Account.Activity is AccountActivityObservation.NotRequested
                    ? snapshots.RefreshAsync().GetAwaiter().GetResult()
                    : snapshots.RefreshWithActivityAsync().GetAwaiter().GetResult();
            }

            return snapshot!;
        }
        finally
        {
            snapshots.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class QueueUsageObservationReader(IEnumerable<UsageObservations> observations)
        : IUsageObservationReader
    {
        private readonly Queue<UsageObservations> observations = new(observations);

        public Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = observations.Dequeue();
            var expected = observation.Account.Activity is AccountActivityObservation.NotRequested
                ? UsageObservationRequest.AllowanceWindows
                : UsageObservationRequest.AllowanceWindowsAndActivity;
            Assert.Equal(expected, request);
            return Task.FromResult(observation);
        }
    }
}
