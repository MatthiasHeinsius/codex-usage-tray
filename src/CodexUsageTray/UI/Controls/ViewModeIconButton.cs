using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal sealed class ViewModeIconButton : Button
{
    private bool isCompact;

    public ViewModeIconButton()
    {
        Text = string.Empty;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        UpdateAccessibleText();
    }

    public bool IsCompact => isCompact;

    public void SetCompact(bool compact)
    {
        if (isCompact == compact)
        {
            return;
        }

        isCompact = compact;
        UpdateAccessibleText();
        Invalidate();
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        SetCompact(!IsCompact);
        base.OnClick(eventArgs);
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

        if (IsCompact)
        {
            graphics.DrawLines(pen, [
                new PointF(centerX - 4f, centerY - 2f),
                new PointF(centerX, centerY - 6f),
                new PointF(centerX + 4f, centerY - 2f)
            ]);
            graphics.DrawLines(pen, [
                new PointF(centerX - 4f, centerY + 2f),
                new PointF(centerX, centerY + 6f),
                new PointF(centerX + 4f, centerY + 2f)
            ]);
        }
        else
        {
            graphics.DrawLines(pen, [
                new PointF(centerX - 4f, centerY - 6f),
                new PointF(centerX, centerY - 2f),
                new PointF(centerX + 4f, centerY - 6f)
            ]);
            graphics.DrawLines(pen, [
                new PointF(centerX - 4f, centerY + 6f),
                new PointF(centerX, centerY + 2f),
                new PointF(centerX + 4f, centerY + 6f)
            ]);
        }
    }

    private void UpdateAccessibleText()
    {
        AccessibleName = isCompact ? "Show extended view" : "Show compact view";
        AccessibleDescription = isCompact
            ? "Show reset times, bars, inference, and update details"
            : "Show only limit percentages and countdowns";
    }
}
