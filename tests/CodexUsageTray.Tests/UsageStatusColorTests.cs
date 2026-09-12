using System.Drawing;

namespace CodexUsageTray.Tests;

public sealed class UsageStatusColorTests
{
    [Theory]
    [InlineData(100, 16, 185, 129)]
    [InlineData(50, 234, 179, 8)]
    [InlineData(25, 245, 158, 11)]
    [InlineData(10, 239, 68, 68)]
    public void UsesRequestedColorStops(int remaining, int red, int green, int blue)
    {
        Assert.Equal(
            Color.FromArgb(red, green, blue).ToArgb(),
            UsageStatusColor.ForRemainingPercent(remaining).ToArgb());
    }

    [Theory]
    [InlineData(75, 125, 182, 68)]
    [InlineData(37, 240, 168, 10)]
    [InlineData(17, 242, 110, 41)]
    public void InterpolatesBetweenColorStops(int remaining, int red, int green, int blue)
    {
        Assert.Equal(
            Color.FromArgb(red, green, blue).ToArgb(),
            UsageStatusColor.ForRemainingPercent(remaining).ToArgb());
    }
}
