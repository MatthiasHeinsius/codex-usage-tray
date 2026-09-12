using System.Drawing;

namespace CodexUsageTray.Tests;

public sealed class TrayIconRendererTests
{
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
}
