namespace CodexUsageTray;

internal sealed class UsagePopupForm : Form
{
    private const int BorderInset = 8;
    private const int ContentInset = 20;
    private const int PopupWidth = 428;
    private const int ContentWidth = PopupWidth - (2 * ContentInset);
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
        + 8
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
        && ClientSize.Width - refreshButton.Right == ContentInset
        && ClientSize.Height - refreshButton.Bottom == ContentInset
        && weeklyReset.Right + 10 <= refreshButton.Left;

    internal bool ContentPaddingIsUniform =>
        fiveHourTitle.Left == Padding.Left
        && ClientSize.Width - fiveHourBar.Right == Padding.Right
        && ClientSize.Width - pinButton.Right == Padding.Right
        && pinButton.Top == Padding.Top
        && ClientSize.Height - refreshButton.Bottom == Padding.Bottom;

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
        ClientSize = new Size(PopupWidth, 411);
        Padding = new Padding(ContentInset);
        Font = new Font("Segoe UI", 9.25f);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

        title = new Label
        {
            Text = "Codex usage",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(ContentInset, 11)
        };

        usagePageButton = new UsageLinkIconButton
        {
            AccessibleName = "Open Codex usage page",
            AccessibleDescription = "Open the Codex usage page in the browser",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(96, 165, 250),
            BackColor = Color.FromArgb(30, 41, 59),
            Location = new Point(290, ContentInset),
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
            Location = new Point(332, ContentInset),
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
            Location = new Point(374, ContentInset),
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
    }

    public void ShowPresentation(UsagePresentation presentation)
    {
        displayedPresentation = presentation.Popup;
        refreshButton.Enabled = presentation is not UsagePresentation.Loading;
        RenderPresentation(presentation.Popup);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyViewMode(compactView, preserveBottom: false);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        ApplyViewMode(compactView, preserveBottom: Visible);
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
        var targetHeight = compact ? 141 : 411;
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
            fiveHourTitle.Location = new Point(ContentInset, 64);
            fiveHourValue.Location = new Point(136, 62);
            fiveHourValue.Size = new Size(112, LabelHeight(fiveHourValue, 24));
            fiveHourReset.Location = new Point(264, 64);
            fiveHourReset.Size = new Size(144, LabelHeight(fiveHourReset, 24));

            weeklyTitle.Location = new Point(ContentInset, 100);
            weeklyValue.Location = new Point(136, 98);
            weeklyValue.Size = new Size(112, LabelHeight(weeklyValue, 24));
            weeklyReset.Location = new Point(264, 100);
            weeklyReset.Size = new Size(100, LabelHeight(weeklyReset, 24));
            targetHeight = Math.Max(
                141,
                Math.Max(weeklyValue.Bottom, weeklyReset.Bottom) + ContentInset);
        }
        else
        {
            statusLabel.Location = new Point(21, 46);
            statusLabel.Size = new Size(261, LabelHeight(statusLabel, 28));

            fiveHourTitle.Location = new Point(ContentInset, 86);
            fiveHourValue.Location = new Point(258, 86);
            fiveHourValue.Size = new Size(150, LabelHeight(fiveHourValue, 24));
            fiveHourReset.Location = new Point(ContentInset, 117);
            fiveHourReset.Size = new Size(ContentWidth, LabelHeight(fiveHourReset, 24));
            fiveHourBar.Location = new Point(ContentInset, 145);
            fiveHourBar.Width = ContentWidth;

            weeklyTitle.Location = new Point(ContentInset, 176);
            weeklyValue.Location = new Point(258, 176);
            weeklyValue.Size = new Size(150, LabelHeight(weeklyValue, 24));
            weeklyReset.Location = new Point(ContentInset, 207);
            weeklyReset.Size = new Size(ContentWidth, LabelHeight(weeklyReset, 24));
            weeklyBar.Location = new Point(ContentInset, 235);
            weeklyBar.Width = ContentWidth;

            limitsDivider.Location = new Point(ContentInset, 264);
            limitsDivider.Size = new Size(ContentWidth, 1);
            todayTitle.Location = new Point(ContentInset, 281);
            todayTitle.Size = new Size(220, LabelHeight(todayTitle, 24));
            todayTokens.Location = new Point(248, 281);
            todayTokens.Size = new Size(160, LabelHeight(todayTokens, 24));
            lifetimeTitle.Location = new Point(ContentInset, 311);
            lifetimeTitle.Size = new Size(220, LabelHeight(lifetimeTitle, 24));
            lifetimeTokens.Location = new Point(248, 311);
            lifetimeTokens.Size = new Size(160, LabelHeight(lifetimeTokens, 24));
            inferenceDivider.Location = new Point(
                ContentInset,
                lifetimeTitle.Bottom + todayTitle.Top - limitsDivider.Bottom);
            inferenceDivider.Size = new Size(ContentWidth, 1);
            updatedLabel.Location = new Point(ContentInset, inferenceDivider.Bottom + 15);
            updatedLabel.Size = new Size(338, LabelHeight(updatedLabel, 24));
            targetHeight = Math.Max(411, updatedLabel.Bottom + ContentInset);
        }

        refreshButton.Location = new Point(
            PopupWidth - ContentInset - refreshButton.Width,
            targetHeight - ContentInset - refreshButton.Height);

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
                new Size(PopupWidth, targetHeight),
                screen.Bounds);
            SetBounds(
                targetLocation.X,
                targetLocation.Y,
                PopupWidth,
                targetHeight,
                BoundsSpecified.All);
        }
        else
        {
            ClientSize = new Size(PopupWidth, targetHeight);
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

        var x = Math.Clamp(
            cursorLocation.X - windowSize.Width + ContentInset,
            workingArea.Left,
            workingArea.Right - windowSize.Width);
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

    private static int LabelHeight(Label label, int minimum) =>
        Math.Max(minimum, label.PreferredHeight + 4);

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
