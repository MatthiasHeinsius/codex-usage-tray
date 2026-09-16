namespace CodexUsageTray.Tests;

public sealed class GuidNotifyIconTests
{
    [Fact]
    public void ShellDataUsesThePermanentTrayIconGuid()
    {
        var data = GuidNotifyIcon.CreateData(123, 456, "tooltip");

        Assert.Equal(new Guid("f5d55d46-5431-46c5-86af-21c872a4fd06"), data.Guid);
        Assert.True(data.Flags.HasFlag(GuidNotifyIcon.NotifyIconDataFlags.Guid));
    }
}
