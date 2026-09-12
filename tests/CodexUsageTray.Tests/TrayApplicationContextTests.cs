namespace CodexUsageTray.Tests;

public sealed class TrayApplicationContextTests
{
    [Fact]
    public void MenuLabelsUseRequestedWording()
    {
        Assert.Equal(
            "Auto-start allowance window",
            TrayApplicationContext.AllowanceActivationMenuText);
        Assert.Equal(
            "Notify on allowance changes",
            TrayApplicationContext.AllowanceNotificationsMenuText);
        Assert.Equal(
            "Check for updates on startup",
            TrayApplicationContext.AutomaticUpdateMenuText);
        Assert.Equal("Check for updates", TrayApplicationContext.CheckForUpdatesMenuText);
        Assert.Equal("Open licenses and notices", TrayApplicationContext.LegalNoticesMenuText);
        Assert.Equal("Open README on GitHub", TrayApplicationContext.ProjectReadmeMenuText);
        Assert.Equal(
            "https://github.com/MatthiasHeinsius/codex-usage-tray/blob/main/README.md",
            TrayApplicationContext.ProjectReadmeUrl);
    }

    [Fact]
    public void ContextMenuSeparatesSettingsFromOpenCommands()
    {
        using var menu = TrayApplicationContext.CreateContextMenu(
            CreateMenuItems(),
            CreateMenuCommands());

        var usagePageItem = Assert.Single(
            menu.Items.OfType<ToolStripMenuItem>(),
            item => item.Text == "Open Codex usage page");
        var usagePageIndex = menu.Items.IndexOf(usagePageItem);

        Assert.IsType<ToolStripSeparator>(menu.Items[usagePageIndex - 1]);
    }

    [Fact]
    public void ContextMenuIncludesWorkingProjectReadmeItem()
    {
        var readmeRequested = false;
        using var menu = TrayApplicationContext.CreateContextMenu(
            CreateMenuItems(),
            CreateMenuCommands(openProjectReadme: (_, _) => readmeRequested = true));

        var readmeItem = Assert.Single(
            menu.Items.OfType<ToolStripMenuItem>(),
            item => item.Text == TrayApplicationContext.ProjectReadmeMenuText);
        readmeItem.PerformClick();

        Assert.True(readmeRequested);
    }

    [Fact]
    public void ContextMenuIncludesLegalNoticesItem()
    {
        var noticesRequested = false;
        using var menu = TrayApplicationContext.CreateContextMenu(
            CreateMenuItems(),
            CreateMenuCommands(openLegalNotices: (_, _) => noticesRequested = true));

        var noticesItem = Assert.Single(
            menu.Items.OfType<ToolStripMenuItem>(),
            item => item.Text == TrayApplicationContext.LegalNoticesMenuText);
        noticesItem.PerformClick();

        Assert.True(noticesRequested);
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

    private static TrayApplicationContext.ContextMenuItems CreateMenuItems()
        => new(
            new ToolStripMenuItem("Start with Windows"),
            new ToolStripMenuItem(TrayApplicationContext.AutomaticUpdateMenuText),
            new ToolStripMenuItem(TrayApplicationContext.AllowanceActivationMenuText),
            new ToolStripMenuItem(TrayApplicationContext.AllowanceNotificationsMenuText),
            new ToolStripMenuItem(TrayApplicationContext.CheckForUpdatesMenuText));

    private static TrayApplicationContext.ContextMenuCommands CreateMenuCommands(
        EventHandler? openProjectReadme = null,
        EventHandler? openLegalNotices = null)
    {
        EventHandler noOp = (_, _) => { };
        return new TrayApplicationContext.ContextMenuCommands(
            noOp,
            noOp,
            noOp,
            openProjectReadme ?? noOp,
            openLegalNotices ?? noOp,
            noOp,
            noOp);
    }
}
