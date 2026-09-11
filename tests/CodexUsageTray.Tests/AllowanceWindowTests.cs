namespace CodexUsageTray.Tests;

public sealed class AllowanceWindowTests
{
    [Theory]
    [InlineData(-1, 0, 100, true, false)]
    [InlineData(0, 0, 100, true, false)]
    [InlineData(35, 35, 65, false, false)]
    [InlineData(100, 100, 0, false, true)]
    [InlineData(101, 100, 0, false, true)]
    public void ConstructorNormalizesAllowanceState(
        int usedPercent,
        int expectedUsedPercent,
        int expectedRemainingPercent,
        bool expectedUnused,
        bool expectedUsedUp)
    {
        var window = new AllowanceWindow(usedPercent, TimeSpan.FromHours(5), ResetsAt: null);

        Assert.Equal(expectedUsedPercent, window.UsedPercent);
        Assert.Equal(expectedRemainingPercent, window.RemainingPercent);
        Assert.Equal(expectedUnused, window.IsUnused);
        Assert.Equal(expectedUsedUp, window.IsUsedUp);
    }

    [Fact]
    public void ResetRequiresUsedUpToUnusedTransitionWithChangedReset()
    {
        var originalReset = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var usedUp = new AllowanceWindow(100, TimeSpan.FromHours(5), originalReset);
        var reset = new AllowanceWindow(0, TimeSpan.FromHours(5), originalReset.AddHours(5));
        var unchanged = new AllowanceWindow(0, TimeSpan.FromHours(5), originalReset);
        var partiallyUsed = new AllowanceWindow(1, TimeSpan.FromHours(5), originalReset.AddHours(5));

        Assert.True(reset.IsResetOf(usedUp));
        Assert.False(unchanged.IsResetOf(usedUp));
        Assert.False(partiallyUsed.IsResetOf(usedUp));
        Assert.False(reset.IsResetOf(new AllowanceWindow(99, TimeSpan.FromHours(5), originalReset)));
    }
}
