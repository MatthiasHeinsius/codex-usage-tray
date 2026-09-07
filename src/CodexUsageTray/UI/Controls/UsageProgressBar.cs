using System.ComponentModel;

namespace CodexUsageTray;

internal sealed class UsageProgressBar : Control
{
    private int value;

    [DefaultValue(0)]
    public int Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, 0, 100);
            Invalidate();
        }
    }

    public UsageProgressBar()
    {
        DoubleBuffered = true;
        Height = 7;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var track = ClientRectangle;
        track.Height = Math.Min(track.Height, 7);
        using var trackPath = RoundedRectangle(track, 3);
        using var trackBrush = new SolidBrush(Color.FromArgb(48, 54, 66));
        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        eventArgs.Graphics.FillPath(trackBrush, trackPath);

        if (Value <= 0)
        {
            return;
        }

        var width = Math.Max(7, (int)Math.Round(track.Width * Value / 100d));
        var fill = new Rectangle(track.X, track.Y, Math.Min(width, track.Width), track.Height);
        using var fillPath = RoundedRectangle(fill, 3);
        using var fillBrush = new SolidBrush(Value switch
        {
            <= 10 => Color.FromArgb(239, 68, 68),
            <= 30 => Color.FromArgb(245, 158, 11),
            _ => Color.FromArgb(16, 185, 129)
        });
        eventArgs.Graphics.FillPath(fillBrush, fillPath);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
