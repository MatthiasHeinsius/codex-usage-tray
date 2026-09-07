using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal sealed class RefreshIconButton : Button
{
    public RefreshIconButton()
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

        var center = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
        const float radius = 6f;
        var bounds = new RectangleF(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        var color = Enabled ? ForeColor : Color.FromArgb(105, 114, 130);
        using var pen = new Pen(color, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(pen, bounds, 45f, 280f);

        const double endAngle = 325d * Math.PI / 180d;
        var tip = new PointF(
            center.X + radius * (float)Math.Cos(endAngle),
            center.Y + radius * (float)Math.Sin(endAngle));
        const double directionAngle = 55d * Math.PI / 180d;
        var direction = new PointF((float)Math.Cos(directionAngle), (float)Math.Sin(directionAngle));
        var perpendicular = new PointF(-direction.Y, direction.X);
        var arrowBase = new PointF(tip.X - direction.X * 4f, tip.Y - direction.Y * 4f);
        var arrow = new[]
        {
            tip,
            new PointF(arrowBase.X + perpendicular.X * 2.1f, arrowBase.Y + perpendicular.Y * 2.1f),
            new PointF(arrowBase.X - perpendicular.X * 2.1f, arrowBase.Y - perpendicular.Y * 2.1f)
        };
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, arrow);
    }
}
