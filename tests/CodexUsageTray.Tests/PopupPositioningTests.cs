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
    public void DraggingUsesPhysicalAndWorkingAreaEdges(
        int x,
        int y,
        int expectedX,
        int expectedY)
    {
        var result = PopupPositioning.GetLocationWhenDragged(
            new Point(x, y),
            PopupSize,
            ScreenBounds,
            WorkingArea);

        Assert.Equal(new Point(expectedX, expectedY), result);
    }

    [Fact]
    public void DraggingDoesNotClampLocationsBeyondTheScreen()
    {
        var location = new Point(-500, -500);

        var result = PopupPositioning.GetLocationWhenDragged(
            location,
            PopupSize,
            ScreenBounds,
            WorkingArea);

        Assert.Equal(location, result);
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
            WorkingArea,
            contentInset: 20);

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
            WorkingArea,
            contentInset: 20);

        Assert.Equal(new Point(480, 592), result);
    }

    [Theory]
    [InlineData(-500, 100)]
    [InlineData(2_000, 660)]
    public void UnpinnedPopupClampsToTheWorkingAreaHorizontally(int cursorX, int expectedX)
    {
        var result = PopupPositioning.GetLocationWhenShown(
            new Point(300, 620),
            pinned: false,
            new Point(cursorX, 700),
            PopupSize,
            WorkingArea,
            contentInset: 20);

        Assert.Equal(new Point(expectedX, 592), result);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(58, 58)]
    public void ResizingPreservesPhysicalScreenTopPositions(int currentTop, int expectedTop)
    {
        var result = PopupPositioning.GetBoundsWhenResized(
            new Rectangle(300, currentTop, 440, 411),
            new Size(440, 150),
            ScreenBounds,
            WorkingArea);

        Assert.Equal(new Rectangle(300, expectedTop, 440, 150), result);
    }

    [Theory]
    [InlineData(80, 80)]
    [InlineData(88, 88)]
    public void ResizingPreservesWorkingAreaTopPositions(int currentTop, int expectedTop)
    {
        var workingArea = new Rectangle(100, 80, 1_000, 670);

        var result = PopupPositioning.GetBoundsWhenResized(
            new Rectangle(300, currentTop, 440, 411),
            new Size(440, 150),
            ScreenBounds,
            workingArea);

        Assert.Equal(new Rectangle(300, expectedTop, 440, 150), result);
    }

    [Fact]
    public void ResizingPreservesTheBottomForAnUnsnappedPopup()
    {
        var result = PopupPositioning.GetBoundsWhenResized(
            new Rectangle(300, 300, 440, 411),
            new Size(440, 150),
            ScreenBounds,
            WorkingArea);

        Assert.Equal(new Rectangle(300, 561, 440, 150), result);
    }

    [Fact]
    public void ResizingClampsTheResultToThePhysicalScreen()
    {
        var result = PopupPositioning.GetBoundsWhenResized(
            new Rectangle(-200, 700, 440, 411),
            new Size(440, 150),
            ScreenBounds,
            WorkingArea);

        Assert.Equal(new Rectangle(100, 650, 440, 150), result);
    }

    [Fact]
    public void ResizingAnOversizedPopupStartsAtThePhysicalScreenOrigin()
    {
        var targetSize = new Size(1_200, 900);

        var result = PopupPositioning.GetBoundsWhenResized(
            new Rectangle(300, 300, 440, 411),
            targetSize,
            ScreenBounds,
            WorkingArea);

        Assert.Equal(new Rectangle(ScreenBounds.Location, targetSize), result);
    }
}
