using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexUsageTray;

internal static class TrayIconRenderer
{
    public static Icon Create(int fiveHourRemaining, int weeklyRemaining)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        // Weekly is the outer ring. The 5-hour ring sits directly against it.
        DrawRing(graphics, new RectangleF(2.5f, 2.5f, 27f, 27f), 5f, weeklyRemaining);
        DrawRing(graphics, new RectangleF(7.5f, 7.5f, 17f, 17f), 5f, fiveHourRemaining);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawRing(Graphics graphics, RectangleF bounds, float width, int remainingPercent)
    {
        var remaining = Math.Clamp(remainingPercent, 0, 100);
        using var track = new Pen(Color.FromArgb(66, 73, 86), width);
        using var progress = new Pen(StatusColor(remaining), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        graphics.DrawArc(track, bounds, -90, 360);
        if (remaining > 0)
        {
            graphics.DrawArc(progress, bounds, -90, Math.Max(4, remaining * 3.6f));
        }
    }

    private static Color StatusColor(int remainingPercent) => remainingPercent switch
    {
        <= 10 => Color.FromArgb(239, 68, 68),
        <= 30 => Color.FromArgb(245, 158, 11),
        _ => Color.FromArgb(16, 185, 129)
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
