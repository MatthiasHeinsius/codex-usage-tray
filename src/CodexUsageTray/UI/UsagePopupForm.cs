namespace CodexUsageTray;

internal sealed class UsagePopupForm : Form
{
    private const int LogicalDpi = 96;
    private const int BorderInset = 8;
    private const int SnapDistance = 12;
    private readonly Label title;
    private readonly Label statusLabel;
    private readonly Label fiveHourTitle;
    private readonly Label fiveHourValue;
    private readonly Label fiveHourReset;
    private readonly UsageProgressBar fiveHourBar;
    private readonly Label weeklyTitle;
    private readonly Label weeklyValue;
    private readonly Label weeklyReset;
    private readonly UsageProgressBar weeklyBar;
    private readonly Panel limitsDivider;
    private readonly Label todayTitle;
    private readonly Label todayTokens;
    private readonly Label lifetimeTitle;
    private readonly Label lifetimeTokens;
    private readonly Panel inferenceDivider;
    private readonly Label updatedLabel;
    private readonly UsageLinkIconButton usagePageButton;
    private readonly ViewModeIconButton viewModeButton;
    private readonly PinIconButton pinButton;
    private readonly RefreshIconButton refreshButton;
    private readonly ToolTip toolTip = new();
    private readonly System.Windows.Forms.Timer deactivateTimer = new() { Interval = 100 };
    private UsagePresentation.PopupPresentation? displayedPresentation;
    private bool compactView;
    private Control? dragControl;
    private Point dragStartCursor;
    private Point dragStartLocation;

    public event EventHandler? RefreshRequested;
    public event EventHandler? UsagePageRequested;
    public event EventHandler? ExtendedViewActivated;

    public bool IsExtendedView => !compactView;

    internal bool HeaderControlsOverlap =>
        title.Left
        + TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, TextFormatFlags.NoPadding).Width
        + ScaleLayoutValue(8)
        > usagePageButton.Left
        || statusLabel.Bounds.IntersectsWith(usagePageButton.Bounds)
        || statusLabel.Bounds.IntersectsWith(viewModeButton.Bounds)
        || statusLabel.Bounds.IntersectsWith(pinButton.Bounds);

    internal bool InferenceDividerPaddingIsBalanced =>
        todayTitle.Top - limitsDivider.Bottom == inferenceDivider.Top - lifetimeTitle.Bottom;

    internal bool CompactRefreshLayoutIsCorrect =>
        compactView
        && refreshButton.Visible
        && !updatedLabel.Visible
        && ClientSize.Width - refreshButton.Right == ScaleLayoutValue(26)
        && ClientSize.Height - refreshButton.Bottom == ScaleLayoutValue(16)
        && weeklyReset.Right + ScaleLayoutValue(10) <= refreshButton.Left;

    internal int RefreshButtonBottomInset => ClientSize.Height - refreshButton.Bottom;

    internal int StatusTextBottomClearance => statusLabel.Height - statusLabel.PreferredHeight;

    internal int FixedLabelVerticalClearance => Controls
        .OfType<Label>()
        .Where(label => !label.AutoSize)
        .Min(label => label.Height - TextRenderer.MeasureText(
            label.Text,
            label.Font,
            Size.Empty,
            TextFormatFlags.NoPadding).Height);

    public UsagePopupForm()
    {
        Text = "Codex usage";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 27, 34);
        ForeColor = Color.FromArgb(235, 238, 244);
        ClientSize = new Size(440, 407);
        Padding = new Padding(26, 20, 26, 20);
        Font = new Font("Segoe UI", 9.25f);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

        title = new Label
        {
            Text = "Codex usage",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(26, 11)
        };

        usagePageButton = new UsageLinkIconButton
        {
            AccessibleName = "Open Codex usage page",
            AccessibleDescription = "Open the Codex usage page in the browser",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(96, 165, 250),
            BackColor = Color.FromArgb(30, 41, 59),
            Location = new Point(296, 20),
            Size = new Size(34, 30),
            Padding = new Padding(0),
            Cursor = Cursors.Hand
        };
        usagePageButton.FlatAppearance.BorderColor = Color.FromArgb(61, 68, 82);
        usagePageButton.Click += (_, _) => UsagePageRequested?.Invoke(this, EventArgs.Empty);
        toolTip.SetToolTip(usagePageButton, "Open Codex usage page");

        viewModeButton = new ViewModeIconButton
        {
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(203, 213, 225),
            BackColor = Color.FromArgb(36, 41, 51),
            Location = new Point(338, 20),
            Size = new Size(34, 30),
            Padding = new Padding(0),
            Cursor = Cursors.Hand
        };
        viewModeButton.SetCompact(PopupViewSettings.IsCompact());
        viewModeButton.FlatAppearance.BorderColor = Color.FromArgb(61, 68, 82);
        viewModeButton.Click += (_, _) =>
        {
            ApplyViewMode(viewModeButton.IsCompact, preserveBottom: true);
            PopupViewSettings.SetCompact(viewModeButton.IsCompact);
            if (!viewModeButton.IsCompact)
            {
                ExtendedViewActivated?.Invoke(this, EventArgs.Empty);
            }
        };
        toolTip.SetToolTip(viewModeButton, viewModeButton.IsCompact ? "Show extended view" : "Show compact view");

        statusLabel = new Label
        {
            Text = "Connecting...",
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoEllipsis = true
        };

        pinButton = new PinIconButton
        {
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(203, 213, 225),
            BackColor = Color.FromArgb(36, 41, 51),
            Location = new Point(380, 20),
            Size = new Size(34, 30),
            Padding = new Padding(0),
            Cursor = Cursors.Hand
        };
        pinButton.FlatAppearance.BorderColor = Color.FromArgb(61, 68, 82);
        pinButton.Click += (_, _) =>
        {
            TopMost = pinButton.IsPinned;
            Cursor = pinButton.IsPinned ? Cursors.SizeAll : Cursors.Default;
            if (pinButton.IsPinned)
            {
                deactivateTimer.Stop();
            }

            toolTip.SetToolTip(
                pinButton,
                pinButton.IsPinned ? "Unpin popup" : "Keep open and on top");
        };
        toolTip.SetToolTip(pinButton, "Keep open and on top");
        deactivateTimer.Tick += (_, _) =>
        {
            deactivateTimer.Stop();
            if (!pinButton.IsPinned && !ContainsFocus)
            {
                Hide();
            }
        };
        Deactivate += (_, _) =>
        {
            if (!pinButton.IsPinned)
            {
                deactivateTimer.Start();
            }
        };
        Activated += (_, _) => deactivateTimer.Stop();

        refreshButton = new RefreshIconButton
        {
            AccessibleName = "Refresh usage",
            AccessibleDescription = "Refresh the Codex usage figures",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(203, 213, 225),
            BackColor = Color.FromArgb(36, 41, 51),
            Size = new Size(34, 30),
            Padding = new Padding(0),
            Cursor = Cursors.Hand
        };
        refreshButton.FlatAppearance.BorderColor = Color.FromArgb(61, 68, 82);
        refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        fiveHourTitle = MakeSectionTitle("5-hour limit");
        fiveHourValue = MakeValueLabel();
        fiveHourReset = MakeMutedLabel("Reset time unavailable");
        fiveHourBar = new UsageProgressBar { Value = 0 };

        weeklyTitle = MakeSectionTitle("Weekly limit");
        weeklyValue = MakeValueLabel();
        weeklyReset = MakeMutedLabel("Reset time unavailable");
        weeklyBar = new UsageProgressBar { Value = 0 };

        limitsDivider = new Panel
        {
            BackColor = Color.FromArgb(48, 54, 66)
        };

        todayTitle = MakeMutedLabel("Inference today");
        todayTokens = MakeTokenValue();
        lifetimeTitle = MakeMutedLabel("Inference total");
        lifetimeTokens = MakeTokenValue();
        inferenceDivider = new Panel
        {
            BackColor = Color.FromArgb(48, 54, 66)
        };
        updatedLabel = MakeMutedLabel("Not updated yet");
        updatedLabel.Font = new Font("Segoe UI", 8f);
        Controls.AddRange([
            title, usagePageButton, statusLabel, viewModeButton, pinButton, refreshButton,
            fiveHourTitle, fiveHourValue, fiveHourReset, fiveHourBar,
            weeklyTitle, weeklyValue, weeklyReset, weeklyBar,
            limitsDivider, todayTitle, todayTokens, lifetimeTitle, lifetimeTokens, inferenceDivider, updatedLabel
        ]);
        AddDragHandlers(this);
        ApplyViewMode(viewModeButton.IsCompact, preserveBottom: false);
        AutoScaleDimensions = new SizeF(LogicalDpi, LogicalDpi);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    public void ShowPresentation(UsagePresentation presentation)
    {
        displayedPresentation = presentation.Popup;
        refreshButton.Enabled = presentation is not UsagePresentation.Loading;
        RenderPresentation(presentation.Popup);
    }

    internal void SetViewModeForScreenshot(bool compact, bool preserveBottom = false)
    {
        viewModeButton.SetCompact(compact);
        ApplyViewMode(compact, preserveBottom);
    }

    private void RenderPresentation(UsagePresentation.PopupPresentation presentation)
    {
        statusLabel.Text = presentation.AccountStatus;
        fiveHourValue.Text = presentation.FiveHour.RemainingText;
        fiveHourReset.Text = compactView
            ? presentation.FiveHour.CompactResetText
            : presentation.FiveHour.ResetText;
        fiveHourBar.Value = presentation.FiveHour.ProgressValue;
        weeklyValue.Text = presentation.Weekly.RemainingText;
        weeklyReset.Text = compactView
            ? presentation.Weekly.CompactResetText
            : presentation.Weekly.ResetText;
        weeklyBar.Value = presentation.Weekly.ProgressValue;
        todayTokens.Text = presentation.TodayTokens;
        lifetimeTokens.Text = presentation.LifetimeTokens;
        updatedLabel.Text = presentation.UpdatedText;
    }

    private void ApplyViewMode(bool compact, bool preserveBottom)
    {
        var previousBounds = Bounds;
        var previousBottom = Bottom;
        var targetHeight = ScaleLayoutValue(compact ? 141 : 407);
        compactView = compact;

        statusLabel.Visible = !compact;
        fiveHourBar.Visible = !compact;
        weeklyBar.Visible = !compact;
        limitsDivider.Visible = !compact;
        todayTitle.Visible = !compact;
        todayTokens.Visible = !compact;
        lifetimeTitle.Visible = !compact;
        lifetimeTokens.Visible = !compact;
        inferenceDivider.Visible = !compact;
        updatedLabel.Visible = !compact;
        refreshButton.Visible = true;

        if (compact)
        {
            fiveHourTitle.Location = ScaleLayoutPoint(26, 64);
            fiveHourValue.Location = ScaleLayoutPoint(142, 62);
            fiveHourValue.Size = ScaleLayoutSize(112, 24);
            fiveHourReset.Location = ScaleLayoutPoint(270, 64);
            fiveHourReset.Size = ScaleLayoutSize(144, 24);

            weeklyTitle.Location = ScaleLayoutPoint(26, 100);
            weeklyValue.Location = ScaleLayoutPoint(142, 98);
            weeklyValue.Size = ScaleLayoutSize(112, 24);
            weeklyReset.Location = ScaleLayoutPoint(270, 100);
            weeklyReset.Size = ScaleLayoutSize(100, 24);
        }
        else
        {
            statusLabel.Location = ScaleLayoutPoint(27, 46);
            statusLabel.Size = ScaleLayoutSize(261, 28);

            fiveHourTitle.Location = ScaleLayoutPoint(26, 86);
            fiveHourValue.Location = ScaleLayoutPoint(264, 86);
            fiveHourValue.Size = ScaleLayoutSize(150, 24);
            fiveHourReset.Location = ScaleLayoutPoint(26, 117);
            fiveHourReset.Size = ScaleLayoutSize(388, 24);
            fiveHourBar.Location = ScaleLayoutPoint(26, 145);
            fiveHourBar.Width = ScaleLayoutValue(388);

            weeklyTitle.Location = ScaleLayoutPoint(26, 176);
            weeklyValue.Location = ScaleLayoutPoint(264, 176);
            weeklyValue.Size = ScaleLayoutSize(150, 24);
            weeklyReset.Location = ScaleLayoutPoint(26, 207);
            weeklyReset.Size = ScaleLayoutSize(388, 24);
            weeklyBar.Location = ScaleLayoutPoint(26, 235);
            weeklyBar.Width = ScaleLayoutValue(388);

            limitsDivider.Location = ScaleLayoutPoint(26, 264);
            limitsDivider.Size = ScaleLayoutSize(388, 1);
            todayTitle.Location = ScaleLayoutPoint(26, 281);
            todayTitle.Size = ScaleLayoutSize(220, 24);
            todayTokens.Location = ScaleLayoutPoint(254, 281);
            todayTokens.Size = ScaleLayoutSize(160, 24);
            lifetimeTitle.Location = ScaleLayoutPoint(26, 311);
            lifetimeTitle.Size = ScaleLayoutSize(220, 24);
            lifetimeTokens.Location = ScaleLayoutPoint(254, 311);
            lifetimeTokens.Size = ScaleLayoutSize(160, 24);
            inferenceDivider.Location = ScaleLayoutPoint(26, 351);
            inferenceDivider.Size = ScaleLayoutSize(388, 1);
            updatedLabel.Location = ScaleLayoutPoint(26, 367);
            updatedLabel.Size = ScaleLayoutSize(338, 24);
        }

        refreshButton.Location = new Point(
            ScaleLayoutValue(440) - ScaleLayoutValue(26) - refreshButton.Width,
            targetHeight - ScaleLayoutValue(16) - refreshButton.Height);

        if (preserveBottom && Visible)
        {
            var screen = Screen.FromRectangle(previousBounds);
            var targetY = previousBounds.Top == screen.Bounds.Top
                || previousBounds.Top == screen.Bounds.Top + BorderInset
                || previousBounds.Top == screen.WorkingArea.Top
                || previousBounds.Top == screen.WorkingArea.Top + BorderInset
                ? previousBounds.Top
                : previousBottom - targetHeight;
            var targetLocation = KeepWithinBounds(
                new Point(Left, targetY),
                new Size(ScaleLayoutValue(440), targetHeight),
                screen.Bounds);
            SetBounds(
                targetLocation.X,
                targetLocation.Y,
                ScaleLayoutValue(440),
                targetHeight,
                BoundsSpecified.All);
        }
        else
        {
            ClientSize = new Size(ScaleLayoutValue(440), targetHeight);
        }

        toolTip.SetToolTip(viewModeButton, compact ? "Show extended view" : "Show compact view");
        if (displayedPresentation is not null)
        {
            RenderPresentation(displayedPresentation);
        }

        Invalidate(invalidateChildren: true);
        Update();
    }

    public void ShowNearTray()
    {
        var cursorLocation = Cursor.Position;
        var workingArea = Screen.FromPoint(cursorLocation).WorkingArea;
        Location = GetLocationWhenShown(
            Location,
            pinButton.IsPinned,
            cursorLocation,
            Size,
            workingArea);
        Show();
        Activate();
    }

    internal static Point GetLocationWhenShown(
        Point currentLocation,
        bool pinned,
        Point cursorLocation,
        Size windowSize,
        Rectangle workingArea)
    {
        if (pinned)
        {
            return currentLocation;
        }

        var x = Math.Clamp(cursorLocation.X - windowSize.Width + 20, workingArea.Left, workingArea.Right - windowSize.Width);
        var y = workingArea.Bottom - windowSize.Height - BorderInset;
        return new Point(x, y);
    }

    internal static Point SnapToScreen(
        Point location,
        Size windowSize,
        Rectangle screenBounds,
        Rectangle workingArea)
    {
        var x = SnapCoordinate(
            location.X,
            windowSize.Width,
            screenBounds.Left,
            workingArea.Left,
            screenBounds.Right,
            workingArea.Right);
        var y = SnapCoordinate(
            location.Y,
            windowSize.Height,
            screenBounds.Top,
            workingArea.Top,
            screenBounds.Bottom,
            workingArea.Bottom);

        return new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var pen = new Pen(Color.FromArgb(61, 68, 82))
        {
            Alignment = System.Drawing.Drawing2D.PenAlignment.Inset
        };
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            deactivateTimer.Dispose();
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs eventArgs)
    {
        if (!Visible)
        {
            deactivateTimer.Stop();
            StopDragging();
        }

        base.OnVisibleChanged(eventArgs);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private static Label MakeSectionTitle(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
        AutoSize = true
    };

    private static Label MakeValueLabel() => new()
    {
        Text = "Unavailable",
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold)
    };

    private static Label MakeMutedLabel(string text) => new()
    {
        Text = text,
        ForeColor = Color.FromArgb(148, 163, 184),
        AutoEllipsis = true
    };

    private static Label MakeTokenValue() => new()
    {
        Text = "Unavailable",
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)
    };

    private int ScaleLayoutValue(int value) =>
        IsHandleCreated
            ? (int)Math.Round(value * DeviceDpi / (double)LogicalDpi)
            : value;

    private Point ScaleLayoutPoint(int x, int y) =>
        new(ScaleLayoutValue(x), ScaleLayoutValue(y));

    private Size ScaleLayoutSize(int width, int height) =>
        new(ScaleLayoutValue(width), ScaleLayoutValue(height));

    private void AddDragHandlers(Control control)
    {
        if (control is not ButtonBase)
        {
            control.MouseDown += DragControlOnMouseDown;
            control.MouseMove += DragControlOnMouseMove;
            control.MouseUp += DragControlOnMouseUp;
            control.MouseCaptureChanged += DragControlOnMouseCaptureChanged;
        }

        foreach (Control child in control.Controls)
        {
            AddDragHandlers(child);
        }
    }

    private void DragControlOnMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        if (!pinButton.IsPinned || eventArgs.Button != MouseButtons.Left || sender is not Control control)
        {
            return;
        }

        dragControl = control;
        dragStartCursor = Cursor.Position;
        dragStartLocation = Location;
        control.Capture = true;
    }

    private void DragControlOnMouseMove(object? sender, MouseEventArgs eventArgs)
    {
        if (dragControl is null || eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        var cursor = Cursor.Position;
        var proposedLocation = new Point(
            dragStartLocation.X + cursor.X - dragStartCursor.X,
            dragStartLocation.Y + cursor.Y - dragStartCursor.Y);
        var proposedBounds = new Rectangle(proposedLocation, Size);
        var screen = Screen.FromRectangle(proposedBounds);
        Location = SnapToScreen(proposedLocation, Size, screen.Bounds, screen.WorkingArea);
    }

    private void DragControlOnMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            StopDragging();
        }
    }

    private void DragControlOnMouseCaptureChanged(object? sender, EventArgs eventArgs)
    {
        if (sender == dragControl && dragControl is { Capture: false })
        {
            StopDragging();
        }
    }

    private void StopDragging()
    {
        var capturedControl = dragControl;
        dragControl = null;
        if (capturedControl is not null)
        {
            capturedControl.Capture = false;
        }
    }

    private static int SnapCoordinate(
        int location,
        int length,
        int boundsStart,
        int workingStart,
        int boundsEnd,
        int workingEnd)
    {
        var nearestOffset = SnapDistance + 1;
        Consider(boundsStart - location);
        Consider(boundsStart + BorderInset - location);
        Consider(workingStart - location);
        Consider(workingStart + BorderInset - location);
        Consider(boundsEnd - location - length);
        Consider(boundsEnd - BorderInset - location - length);
        Consider(workingEnd - location - length);
        Consider(workingEnd - BorderInset - location - length);
        return location + (Math.Abs(nearestOffset) <= SnapDistance ? nearestOffset : 0);

        void Consider(int offset)
        {
            if (Math.Abs(offset) < Math.Abs(nearestOffset))
            {
                nearestOffset = offset;
            }
        }
    }

    private static Point KeepWithinBounds(Point location, Size windowSize, Rectangle bounds)
    {
        var maximumX = Math.Max(bounds.Left, bounds.Right - windowSize.Width);
        var maximumY = Math.Max(bounds.Top, bounds.Bottom - windowSize.Height);
        return new Point(
            Math.Clamp(location.X, bounds.Left, maximumX),
            Math.Clamp(location.Y, bounds.Top, maximumY));
    }

}
