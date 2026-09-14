using System.Drawing;

namespace CodexUsageTray.Tests;

public sealed class TrayIconRendererTests
{
    [Fact]
    public void TrayIconMatchesTheSystemSmallIconSize()
    {
        var expectedSize = Math.Max(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height);

        using var icon = TrayIconRenderer.Create(65, 7);

        Assert.Equal(new Size(expectedSize, expectedSize), icon.Size);
    }

    [Fact]
    public void MultiResolutionIconContainsNine32BitImages()
    {
        var iconData = TrayIconRenderer.CreateIcoData(65, 7);

        Assert.Equal(9, BitConverter.ToUInt16(iconData, 4));
        Assert.Equal(
            [16, 20, 24, 32, 40, 48, 64, 128, 0],
            Enumerable.Range(0, 9)
                .Select(index => iconData[6 + (index * 16)])
                .ToArray());
        for (var index = 0; index < 9; index++)
        {
            Assert.Equal(32, BitConverter.ToUInt16(iconData, 6 + (index * 16) + 6));
        }
    }

    [Fact]
    public void FiveHourAllowanceUsesOuterRing()
    {
        using var stream = new MemoryStream(TrayIconRenderer.RenderPng(256, 100, 0));
        using var bitmap = new Bitmap(stream);

        Assert.Equal(
            UsageStatusColor.ForRemainingPercent(100).ToArgb(),
            bitmap.GetPixel(20, 128).ToArgb());
        Assert.Equal(
            Color.FromArgb(66, 73, 86).ToArgb(),
            bitmap.GetPixel(60, 128).ToArgb());
    }

    [Fact]
    public void RingSegmentsHaveFlatEnds()
    {
        using var stream = new MemoryStream(TrayIconRenderer.RenderPng(256, 25, 0));
        using var bitmap = new Bitmap(stream);

        Assert.Equal(
            Color.FromArgb(66, 73, 86).ToArgb(),
            bitmap.GetPixel(110, 20).ToArgb());
        Assert.Equal(
            UsageStatusColor.ForRemainingPercent(25).ToArgb(),
            bitmap.GetPixel(130, 20).ToArgb());
    }

    [Fact]
    public void WeeklyAllowanceUsesOuterRingWhenItIsTheOnlyWindow()
    {
        using var stream = new MemoryStream(TrayIconRenderer.RenderPng(256, null, 100));
        using var bitmap = new Bitmap(stream);

        Assert.Equal(
            UsageStatusColor.ForRemainingPercent(100).ToArgb(),
            bitmap.GetPixel(24, 128).ToArgb());
        Assert.Equal(0, bitmap.GetPixel(60, 128).A);
    }

    [Fact]
    public void SingleAllowanceWindowUsesThickerRing()
    {
        using var singleStream = new MemoryStream(TrayIconRenderer.RenderPng(256, 100, null));
        using var singleBitmap = new Bitmap(singleStream);
        using var twoWindowStream = new MemoryStream(TrayIconRenderer.RenderPng(256, 100, 0));
        using var twoWindowBitmap = new Bitmap(twoWindowStream);

        Assert.Equal(
            UsageStatusColor.ForRemainingPercent(100).ToArgb(),
            singleBitmap.GetPixel(46, 128).ToArgb());
        Assert.Equal(
            Color.FromArgb(66, 73, 86).ToArgb(),
            twoWindowBitmap.GetPixel(46, 128).ToArgb());
    }
}
