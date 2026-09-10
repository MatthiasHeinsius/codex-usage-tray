using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace CodexUsageTrayDocs;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--app-icon", var iconPath])
        {
            SaveAppIcon(Path.GetFullPath(iconPath));
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(defaultValue: false);

        var outputDirectory = args.Length == 1
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "images"));
        Directory.CreateDirectory(outputDirectory);

        var now = DateTimeOffset.Now;
        var snapshot = CodexUsageTray.UsageSnapshot.Reconcile(
            previous: null,
            new CodexUsageTray.AccountUsageObservation(
                now,
                [
                    new CodexUsageTray.AllowanceWindow(36, TimeSpan.FromHours(5), now.AddHours(3).AddMinutes(12)),
                    new CodexUsageTray.AllowanceWindow(58, TimeSpan.FromDays(7), now.AddDays(3).AddHours(8))
                ],
                "plus",
                "Codex",
                new CodexUsageTray.AccountActivityObservation.Observed(
                    LifetimeTokens: 128_440_800,
                    TodayTokens: 784_200,
                    LatestDailyBucketDate: DateOnly.FromDateTime(now.LocalDateTime))),
            local: null);
        var presentation = CodexUsageTray.UsagePresentation.Create(
            snapshot,
            now,
            CultureInfo.CurrentCulture);

        using var popup = new CodexUsageTray.UsagePopupForm();
        popup.ShowSnapshot(snapshot);
        popup.Location = new Point(-10_000, -10_000);
        popup.Show();
        try
        {
            popup.SetViewModeForScreenshot(compact: false);
            Application.DoEvents();
            Save(popup, Path.Combine(outputDirectory, "extended.png"));

            popup.SetViewModeForScreenshot(compact: true);
            Application.DoEvents();
            Save(popup, Path.Combine(outputDirectory, "compact.png"));

            SaveTrayTooltip(presentation.Tray, Path.Combine(outputDirectory, "tray-tooltip.png"));
            SaveContextMenu(presentation.Tray, Path.Combine(outputDirectory, "context-menu.png"));
        }
        finally
        {
            popup.Hide();
        }

        return 0;
    }

    private static void SaveAppIcon(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The application icon directory is unavailable."));
        File.WriteAllBytes(path, CodexUsageTray.TrayIconRenderer.CreateStaticIcoData());
    }

    private static void Save(Control control, string path)
    {
        control.CreateControl();
        control.PerformLayout();

        using var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void SaveContextMenu(CodexUsageTray.UsagePresentation.TrayPresentation presentation, string path)
    {
        using var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true
        };
        using var windowStartItem = new ToolStripMenuItem("Auto-activate unused windows with \"Hi\"")
        {
            CheckOnClick = true
        };
        using var allowanceNotificationsItem = new ToolStripMenuItem("Allowance notifications")
        {
            CheckOnClick = true
        };
        using var menu = CodexUsageTray.TrayApplicationContext.CreateContextMenu(
            startupItem,
            windowStartItem,
            allowanceNotificationsItem,
            (_, _) => { },
            (_, _) => { },
            (_, _) => { },
            (_, _) => { });
        menu.CreateControl();
        menu.PerformLayout();
        menu.Size = menu.GetPreferredSize(Size.Empty);

        const int width = 440;
        const int taskbarHeight = 52;
        const int menuGap = 8;
        var height = menu.Height + menuGap + taskbarHeight;
        using var menuBitmap = new Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(menuBitmap, menu.ClientRectangle);
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(24, 28, 36));
        graphics.DrawImageUnscaled(menuBitmap, (width - menu.Width) / 2, 0);
        DrawTrayArea(graphics, width, height, presentation);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void SaveTrayTooltip(CodexUsageTray.UsagePresentation.TrayPresentation presentation, string path)
    {
        const int width = 440;
        const int height = 120;
        var tooltipBounds = new Rectangle(32, 12, width - 64, 40);

        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(24, 28, 36));
        DrawTrayArea(graphics, width, height, presentation);

        using (var tooltipBrush = new SolidBrush(Color.FromArgb(43, 43, 43)))
        using (var tooltipBorder = new Pen(Color.FromArgb(91, 91, 91)))
        using (var tooltipFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
        using (var textBrush = new SolidBrush(Color.White))
        using (var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            graphics.FillRectangle(tooltipBrush, tooltipBounds);
            graphics.DrawRectangle(tooltipBorder, tooltipBounds);
            graphics.DrawString(
                presentation.Tooltip,
                tooltipFont,
                textBrush,
                tooltipBounds,
                textFormat);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static void DrawTrayArea(
        Graphics graphics,
        int width,
        int height,
        CodexUsageTray.UsagePresentation.TrayPresentation presentation)
    {
        const int taskbarHeight = 52;
        using (var taskbarBrush = new SolidBrush(Color.FromArgb(31, 31, 31)))
        {
            graphics.FillRectangle(taskbarBrush, 0, height - taskbarHeight, width, taskbarHeight);
        }

        using var icon = CodexUsageTray.TrayIconRenderer.Create(
            presentation.FiveHourRemaining,
            presentation.WeeklyRemaining);
        using var iconBitmap = icon.ToBitmap();
        graphics.DrawImage(
            iconBitmap,
            new Rectangle((width - 32) / 2, height - taskbarHeight + 10, 32, 32));
    }
}
