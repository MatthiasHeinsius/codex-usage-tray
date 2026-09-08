using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CodexUsageTray;

internal static class TrayIconRenderer
{
    internal const int StaticFiveHourRemaining = 66;
    internal const int StaticWeeklyRemaining = 75;
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static Icon Create(int fiveHourRemaining, int weeklyRemaining)
    {
        var size = Math.Max(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height);
        using var stream = new MemoryStream(CreateIcoData([size], fiveHourRemaining, weeklyRemaining));
        using var icon = new Icon(stream, size, size);
        return (Icon)icon.Clone();
    }

    internal static byte[] CreateIcoData(int fiveHourRemaining, int weeklyRemaining) =>
        CreateIcoData(IconSizes, fiveHourRemaining, weeklyRemaining);

    internal static byte[] CreateStaticIcoData() =>
        CreateIcoData(StaticFiveHourRemaining, StaticWeeklyRemaining);

    private static byte[] CreateIcoData(
        int[] sizes,
        int fiveHourRemaining,
        int weeklyRemaining)
    {
        var images = sizes
            .Select(size => RenderPng(size, fiveHourRemaining, weeklyRemaining))
            .ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Length);

        var imageOffset = 6 + (16 * images.Length);
        for (var index = 0; index < images.Length; index++)
        {
            var size = sizes[index];
            writer.Write(size == 256 ? (byte)0 : (byte)size);
            writer.Write(size == 256 ? (byte)0 : (byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[index].Length);
            writer.Write(imageOffset);
            imageOffset += images[index].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] RenderPng(int size, int fiveHourRemaining, int weeklyRemaining)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var scale = size / 32f;
        DrawRing(graphics, Scale(new RectangleF(2.5f, 2.5f, 27f, 27f), scale), 5f * scale, weeklyRemaining);
        DrawRing(graphics, Scale(new RectangleF(7.5f, 7.5f, 17f, 17f), scale), 5f * scale, fiveHourRemaining);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static RectangleF Scale(RectangleF bounds, float scale) => new(
        bounds.X * scale,
        bounds.Y * scale,
        bounds.Width * scale,
        bounds.Height * scale);

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
}
