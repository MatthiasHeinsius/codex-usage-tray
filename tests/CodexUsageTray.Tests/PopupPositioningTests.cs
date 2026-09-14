using System.Drawing;

namespace CodexUsageTray.Tests;

public sealed class PopupPositioningTests
{
    private static readonly Rectangle ScreenBounds = new(100, 50, 1_000, 750);
    private static readonly Rectangle WorkingArea = new(100, 50, 1_000, 700);
    private static readonly Size PopupSize = new(440, 150);

    [Theory]
    [InlineData(109, 59, 108, 58)]
    [InlineData(101, 51, 100, 50)]
    [InlineData(651, 591, 652, 592)]
    [InlineData(300, 599, 300, 600)]
    [InlineData(300, 620, 300, 620)]
    [InlineData(300, 641, 300, 642)]
    [InlineData(300, 649, 300, 650)]
    [InlineData(130, 90, 130, 90)]
    public void SnapToScreenUsesPhysicalAndWorkingAreaEdges(
        int x,
        int y,
        int expectedX,
        int expectedY)
    {
        var result = PopupPositioning.SnapToScreen(
            new Point(x, y),
            PopupSize,
            ScreenBounds,
            WorkingArea);

        Assert.Equal(new Point(expectedX, expectedY), result);
    }

    [Fact]
    public void PinnedPopupKeepsItsLocationWhenShown()
    {
        var currentLocation = new Point(300, 620);

        var result = PopupPositioning.GetLocationWhenShown(
            currentLocation,
            pinned: true,
            new Point(900, 700),
            PopupSize,
            WorkingArea);

        Assert.Equal(currentLocation, result);
    }

    [Fact]
    public void UnpinnedPopupReturnsToTheTrayWhenShown()
    {
        var result = PopupPositioning.GetLocationWhenShown(
            new Point(300, 620),
            pinned: false,
            new Point(900, 700),
            PopupSize,
            WorkingArea);

        Assert.Equal(new Point(480, 592), result);
    }
}
