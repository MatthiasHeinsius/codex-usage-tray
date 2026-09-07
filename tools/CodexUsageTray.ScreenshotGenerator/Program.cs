using System.Drawing.Imaging;

namespace CodexUsageTrayDocs;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(defaultValue: false);

        var outputDirectory = args.Length == 1
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "images"));
        Directory.CreateDirectory(outputDirectory);

        var now = DateTimeOffset.Now;
        var snapshot = new CodexUsageTray.UsageSnapshot(
            now,
            new CodexUsageTray.UsageWindow(36, 300, now.AddHours(3).AddMinutes(12)),
            new CodexUsageTray.UsageWindow(58, 10_080, now.AddDays(3).AddHours(8)),
            128_440_800,
            784_200,
            "plus",
            "Codex");

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
        }
        finally
        {
            popup.Hide();
        }

        return 0;
    }

    private static void Save(Form form, string path)
    {
        form.CreateControl();
        form.PerformLayout();

        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(path, ImageFormat.Png);
    }
}
