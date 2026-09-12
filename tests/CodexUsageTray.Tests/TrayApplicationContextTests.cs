namespace CodexUsageTray.Tests;

public sealed class TrayApplicationContextTests
{
    [Fact]
    public void AllowanceMenuLabelsUseRequestedWording()
    {
        Assert.Equal(
            "Auto-activate allowance window",
            TrayApplicationContext.AllowanceActivationMenuText);
        Assert.Equal(
            "Notify on allowance changes",
            TrayApplicationContext.AllowanceNotificationsMenuText);
        Assert.Equal(
            "Automatically check for updates",
            TrayApplicationContext.AutomaticUpdateMenuText);
        Assert.Equal("Check for updates", TrayApplicationContext.CheckForUpdatesMenuText);
    }

    [Fact]
    public void TrayClickClosesPopupThatWasVisibleWhenTheMouseWasPressed()
    {
        Assert.False(TrayApplicationContext.ShouldShowAfterTrayClick(visibleWhenMousePressed: true));
    }

    [Theory]
    [InlineData(1_100L, 1_000L, 500, false)]
    [InlineData(1_501L, 1_000L, 500, true)]
    public void TrayClickSuppressesOnlyTheSecondClickOfADoubleClick(
        long currentTimestamp,
        long previousTimestamp,
        int doubleClickTime,
        bool expected)
    {
        Assert.Equal(
            expected,
            TrayApplicationContext.ShouldHandleTrayClick(
                currentTimestamp,
                previousTimestamp,
                doubleClickTime));
    }
}
