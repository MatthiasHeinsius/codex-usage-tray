using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal sealed class PinIconButton : Button
{
    private bool isPinned;

    public PinIconButton()
    {
        Text = string.Empty;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        UpdateAccessibleText();
    }

    public bool IsPinned
    {
        get => isPinned;
        private set
        {
            if (isPinned == value)
            {
                return;
            }

            isPinned = value;
            BackColor = isPinned ? Color.FromArgb(37, 99, 235) : Color.FromArgb(36, 41, 51);
            UpdateAccessibleText();
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        IsPinned = !IsPinned;
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

        graphics.DrawLine(pen, centerX - 4.5f, centerY - 6.5f, centerX + 4.5f, centerY - 6.5f);
        using var body = new GraphicsPath();
        body.AddPolygon([
            new PointF(centerX - 3.5f, centerY - 5.5f),
            new PointF(centerX + 3.5f, centerY - 5.5f),
            new PointF(centerX + 2.5f, centerY + 1.5f),
            new PointF(centerX + 5f, centerY + 3f),
            new PointF(centerX - 5f, centerY + 3f),
            new PointF(centerX - 2.5f, centerY + 1.5f)
        ]);

        if (IsPinned)
        {
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, body);
        }
        else
        {
            graphics.DrawPath(pen, body);
        }

        graphics.DrawLine(pen, centerX, centerY + 3f, centerX, centerY + 8f);
    }

    private void UpdateAccessibleText()
    {
        AccessibleName = isPinned ? "Unpin popup" : "Pin popup";
        AccessibleDescription = isPinned
            ? "Allow the Codex usage popup to close when it loses focus"
            : "Keep the Codex usage popup open and above other windows";
    }
}
