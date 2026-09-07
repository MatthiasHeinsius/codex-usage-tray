using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal sealed class UsageLinkIconButton : Button
{
    public UsageLinkIconButton()
    {
        Text = string.Empty;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);

        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var centerX = ClientSize.Width / 2f;
        var centerY = ClientSize.Height / 2f;
        var color = Enabled ? ForeColor : Color.FromArgb(105, 114, 130);
        using var pen = new Pen(color, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        graphics.DrawLines(pen, [
            new PointF(centerX + 1f, centerY - 5f),
            new PointF(centerX - 6f, centerY - 5f),
            new PointF(centerX - 6f, centerY + 6f),
            new PointF(centerX + 5f, centerY + 6f),
            new PointF(centerX + 5f, centerY - 1f)
        ]);
        graphics.DrawLine(pen, centerX - 1f, centerY + 1f, centerX + 6f, centerY - 6f);
        graphics.DrawLines(pen, [
            new PointF(centerX + 1f, centerY - 6f),
            new PointF(centerX + 6f, centerY - 6f),
            new PointF(centerX + 6f, centerY - 1f)
        ]);
    }
}
