using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal sealed class UsagePopupForm : Form
{
    private const int ContentInset = 20;
    private const int PopupWidth = 428;
    private const int ContentWidth = PopupWidth - (2 * ContentInset);
    private static readonly Color PopupBackColor = Color.FromArgb(24, 27, 34);
    private static readonly Color PopupBorderColor = Color.FromArgb(61, 68, 82);
    private readonly UsageActivityIndicator activityIndicator;
    private readonly ActivityOverlayForm activityOverlay;
    private readonly Label title;
    private readonly Label statusLabel;
    private readonly AllowanceControls fiveHourAllowance;
    private readonly AllowanceControls weeklyAllowance;
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
    private readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 50 };
    private readonly CodexSessionActivityMonitor? activityMonitor;
    private UsagePresentation.PopupPresentation? displayedPresentation;
    private CodexSessionActivity sessionActivity = CodexSessionActivity.Empty;
    private bool compactView;
    private Control? dragControl;
    private Point dragStartCursor;
    private Point dragStartLocation;

    public event EventHandler? RefreshRequested;
    public event EventHandler? UsagePageRequested;
    public event EventHandler? ExtendedViewActivated;

    public bool IsExtendedView => !compactView;

    public UsagePopupForm()
        : this(
            RegistryApplicationSettings.Current.CompactPopup,
            compact => RegistryApplicationSettings.Current.CompactPopup = compact,
            new CodexSessionActivityMonitor())
    {
    }

    internal UsagePopupForm(bool initialCompactView, Action<bool>? saveViewMode = null)
        : this(initialCompactView, saveViewMode, activityMonitor: null)
    {
    }

    private UsagePopupForm(
        bool initialCompactView,
        Action<bool>? saveViewMode,
        CodexSessionActivityMonitor? activityMonitor)
    {
        this.activityMonitor = activityMonitor;
        Text = "Codex Usage";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = PopupBackColor;
        ForeColor = Color.FromArgb(235, 238, 244);
        ClientSize = new Size(PopupWidth, 411);
        Padding = new Padding(ContentInset);
        Font = new Font("Segoe UI", 9.25f);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

        activityIndicator = new UsageActivityIndicator
        {
            Location = Point.Empty
        };
        activityIndicator.ShowSession(activityMonitor?.Current ?? CodexSessionActivity.Empty);
        activityOverlay = new ActivityOverlayForm(activityIndicator);

        title = new Label
        {
            Text = "Codex Usage",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(80, 11)
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
        viewModeButton.SetCompact(initialCompactView);
        viewModeButton.FlatAppearance.BorderColor = Color.FromArgb(61, 68, 82);
        viewModeButton.Click += (_, _) =>
        {
            ApplyViewMode(viewModeButton.IsCompact, preserveBottom: true);
            saveViewMode?.Invoke(viewModeButton.IsCompact);
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
        animationTimer.Tick += (_, _) =>
        {
            activityIndicator.Advance(DateTimeOffset.Now);
            RenderAccountStatus();
        };

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

        fiveHourAllowance = new AllowanceControls("5-hour limit");
        weeklyAllowance = new AllowanceControls("Weekly limit");

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
            .. fiveHourAllowance.All,
            .. weeklyAllowance.All,
            limitsDivider, todayTitle, todayTokens, lifetimeTitle, lifetimeTokens, inferenceDivider, updatedLabel
        ]);
        if (activityMonitor is not null)
        {
            activityMonitor.Changed += ActivityMonitorOnChanged;
        }

        AddDragHandlers(this);
        LocationChanged += (_, _) => PositionActivityOverlay();
        ApplyViewMode(viewModeButton.IsCompact, preserveBottom: false);
    }

    public void ShowPresentation(UsagePresentation presentation)
    {
        displayedPresentation = presentation.Popup;
        refreshButton.Enabled = presentation is not UsagePresentation.Loading;
        ApplyViewMode(compactView, preserveBottom: Visible);
    }

    protected override void OnLoad(EventArgs eventArgs)
    {
        // Borderless client metrics still include the temporary window frame during handle creation.
        ApplyViewMode(compactView, preserveBottom: false);
        base.OnLoad(eventArgs);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        ApplyViewMode(compactView, preserveBottom: Visible);
    }

    internal void SetViewModeForScreenshot(bool compact)
    {
        viewModeButton.SetCompact(compact);
        ApplyViewMode(compact, preserveBottom: false);
    }

    internal void SetActivityForScreenshot(CodexSessionActivity activity) => ShowActivity(activity);

    private void RenderPresentation(UsagePresentation.PopupPresentation presentation)
    {
        RenderAccountStatus();
        fiveHourAllowance.Render(presentation.FiveHour, compactView);
        weeklyAllowance.Render(presentation.Weekly, compactView);
        todayTokens.Text = presentation.TodayTokens;
        lifetimeTokens.Text = presentation.LifetimeTokens;
        updatedLabel.Text = presentation.UpdatedText;
        activityIndicator.ShowAllowance(presentation.ActivityIndicator);
    }

    private void ApplyViewMode(bool compact, bool preserveBottom)
    {
        var previousBounds = Bounds;
        int targetHeight;
        var fiveHourVisible = displayedPresentation?.FiveHour.IsVisible ?? true;
        var weeklyVisible = displayedPresentation?.Weekly.IsVisible ?? true;
        var visibleAllowanceCount = (fiveHourVisible ? 1 : 0) + (weeklyVisible ? 1 : 0);
        compactView = compact;

        statusLabel.Visible = !compact;
        fiveHourAllowance.SetVisibility(fiveHourVisible, compact);
        weeklyAllowance.SetVisibility(weeklyVisible, compact);
        limitsDivider.Visible = !compact && visibleAllowanceCount > 0;
        todayTitle.Visible = !compact;
        todayTokens.Visible = !compact;
        lifetimeTitle.Visible = !compact;
        lifetimeTokens.Visible = !compact;
        inferenceDivider.Visible = !compact;
        updatedLabel.Visible = !compact;
        if (compact)
        {
            var row = 0;
            if (fiveHourVisible)
            {
                fiveHourAllowance.LayoutCompact(row++, sharesRefreshRow: !weeklyVisible);
            }

            if (weeklyVisible)
            {
                weeklyAllowance.LayoutCompact(row++, sharesRefreshRow: true);
            }

            var contentBottom = 0;
            if (fiveHourVisible)
            {
                contentBottom = fiveHourAllowance.ContentBottom;
            }

            if (weeklyVisible)
            {
                contentBottom = Math.Max(contentBottom, weeklyAllowance.ContentBottom);
            }

            targetHeight = Math.Max(
                105,
                Math.Max(69 + (36 * visibleAllowanceCount), contentBottom + ContentInset));
        }
        else
        {
            statusLabel.Location = new Point(80, 50);
            statusLabel.Size = new Size(
                PopupWidth - ContentInset - statusLabel.Left,
                LabelHeight(statusLabel, 28));

            var section = 0;
            if (fiveHourVisible)
            {
                fiveHourAllowance.LayoutExtended(section++);
            }

            if (weeklyVisible)
            {
                weeklyAllowance.LayoutExtended(section++);
            }

            var allowanceShift = 90 * (2 - visibleAllowanceCount);
            limitsDivider.Location = new Point(ContentInset, 264 - allowanceShift);
            limitsDivider.Size = new Size(ContentWidth, 1);
            todayTitle.Location = new Point(ContentInset, 281 - allowanceShift);
            todayTitle.Size = new Size(220, LabelHeight(todayTitle, 24));
            todayTokens.Location = new Point(248, 281 - allowanceShift);
            todayTokens.Size = new Size(160, LabelHeight(todayTokens, 24));
            lifetimeTitle.Location = new Point(ContentInset, 311 - allowanceShift);
            lifetimeTitle.Size = new Size(220, LabelHeight(lifetimeTitle, 24));
            lifetimeTokens.Location = new Point(248, 311 - allowanceShift);
            lifetimeTokens.Size = new Size(160, LabelHeight(lifetimeTokens, 24));
            inferenceDivider.Location = new Point(
                ContentInset,
                lifetimeTitle.Bottom + todayTitle.Top - limitsDivider.Bottom);
            inferenceDivider.Size = new Size(ContentWidth, 1);
            updatedLabel.Location = new Point(ContentInset, inferenceDivider.Bottom + 15);
            updatedLabel.Size = new Size(338, LabelHeight(updatedLabel, 24));
            targetHeight = Math.Max(
                411 - allowanceShift,
                updatedLabel.Bottom + ContentInset);
        }

        refreshButton.Location = new Point(
            PopupWidth - ContentInset - refreshButton.Width,
            targetHeight - ContentInset - refreshButton.Height);

        if (preserveBottom && Visible)
        {
            var screen = Screen.FromRectangle(previousBounds);
            var targetBounds = PopupPositioning.GetBoundsWhenResized(
                previousBounds,
                new Size(PopupWidth, targetHeight),
                screen.Bounds,
                screen.WorkingArea);
            SetBounds(
                targetBounds.X,
                targetBounds.Y,
                targetBounds.Width,
                targetBounds.Height,
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
        PositionNearTray();
        Show();
        // The first Show applies DPI-aware label heights, so position again with the final size.
        PositionNearTray();
        Activate();

        void PositionNearTray() => Location = PopupPositioning.GetLocationWhenShown(
            Location,
            pinButton.IsPinned,
            cursorLocation,
            Size,
            workingArea,
            ContentInset);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var pen = new Pen(PopupBorderColor)
        {
            Alignment = PenAlignment.Inset
        };
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (activityMonitor is not null)
            {
                activityMonitor.Changed -= ActivityMonitorOnChanged;
                activityMonitor.Dispose();
            }

            animationTimer.Dispose();
            deactivateTimer.Dispose();
            toolTip.Dispose();
            activityOverlay.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs eventArgs)
    {
        if (!Visible)
        {
            activityOverlay.Hide();
            animationTimer.Stop();
            deactivateTimer.Stop();
            StopDragging();
        }
        else
        {
            if (activityMonitor is not null)
            {
                ShowActivity(activityMonitor.Current);
            }

            activityIndicator.Advance(DateTimeOffset.Now);
            RenderAccountStatus();
            PositionActivityOverlay();
            activityOverlay.Opacity = Opacity;
            if (!activityOverlay.Visible)
            {
                activityOverlay.Show(this);
            }

            animationTimer.Start();
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

    private sealed class AllowanceControls
    {
        public AllowanceControls(string title)
        {
            Title = MakeSectionTitle(title);
            Value = MakeValueLabel();
            Reset = MakeMutedLabel("Reset time unavailable");
            Bar = new UsageProgressBar { Value = 0 };
            All = [Title, Value, Reset, Bar];
        }

        public Label Title { get; }
        public Label Value { get; }
        public Label Reset { get; }
        public UsageProgressBar Bar { get; }
        public Control[] All { get; }
        public int ContentBottom => Math.Max(Title.Bottom, Math.Max(Value.Bottom, Reset.Bottom));

        public void Render(UsagePresentation.AllowancePresentation presentation, bool compact)
        {
            Value.Text = presentation.RemainingText;
            Reset.Text = compact ? presentation.CompactResetText : presentation.ResetText;
            Bar.Value = presentation.ProgressValue;
        }

        public void SetVisibility(bool visible, bool compact)
        {
            Title.Visible = visible;
            Value.Visible = visible;
            Reset.Visible = visible;
            Bar.Visible = visible && !compact;
        }

        public void LayoutCompact(int row, bool sharesRefreshRow)
        {
            var top = 86 + (36 * row);
            Title.Location = new Point(ContentInset, top);
            Value.Location = new Point(198, top - 2);
            Value.Size = new Size(76, LabelHeight(Value, 24));
            Reset.Location = new Point(282, top);
            Reset.Size = new Size(sharesRefreshRow ? 82 : 126, LabelHeight(Reset, 24));
        }

        public void LayoutExtended(int section)
        {
            var top = 86 + (90 * section);
            Title.Location = new Point(ContentInset, top);
            Value.Location = new Point(258, top);
            Value.Size = new Size(150, LabelHeight(Value, 24));
            Reset.Location = new Point(ContentInset, top + 31);
            Reset.Size = new Size(ContentWidth, LabelHeight(Reset, 24));
            Bar.Location = new Point(ContentInset, top + 59);
            Bar.Width = ContentWidth;
        }
    }

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
        Location = PopupPositioning.GetLocationWhenDragged(
            proposedLocation,
            Size,
            screen.Bounds,
            screen.WorkingArea);
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

    private void ActivityMonitorOnChanged(object? sender, EventArgs eventArgs)
    {
        if (activityMonitor is null || IsDisposed || Disposing)
        {
            return;
        }

        var activity = activityMonitor.Current;
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() => ShowActivity(activity));
        }
        catch (InvalidOperationException)
        {
            // The form handle can disappear while the application is shutting down.
        }
    }

    private void ShowActivity(CodexSessionActivity activity)
    {
        sessionActivity = activity;
        activityIndicator.ShowSession(activity);
        RenderAccountStatus();
    }

    private void RenderAccountStatus()
    {
        if (displayedPresentation is not null)
        {
            statusLabel.Text = displayedPresentation.StatusText(sessionActivity, activityIndicator.IsActive);
        }
    }

    private void PositionActivityOverlay()
    {
        var titleCenterY = title.Top + (title.Height / 2);
        activityOverlay.SetPopupInset((activityOverlay.Width / 2) - titleCenterY);
        activityOverlay.Location = new Point(
            Left + titleCenterY - (activityOverlay.Width / 2),
            Top + titleCenterY - (activityOverlay.Height / 2));
    }

    private sealed class ActivityOverlayForm : Form
    {
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;
        private const int WsExNoActivate = 0x08000000;
        private const float OutlineRadius = 50;
        private int popupInset;

        public ActivityOverlayForm(UsageActivityIndicator indicator)
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(23, 26, 33);
            ClientSize = indicator.Size + new Size(1, 1);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TransparencyKey = BackColor;
            Controls.Add(indicator);
        }

        public void SetPopupInset(int inset)
        {
            popupInset = inset;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var farEdge = ClientSize.Width - 1;
            var circle = new RectangleF(
                (ClientSize.Width / 2f) - OutlineRadius,
                (ClientSize.Height / 2f) - OutlineRadius,
                OutlineRadius * 2,
                OutlineRadius * 2);

            using var fillPath = new GraphicsPath();
            AddCornerOutline(fillPath, circle, farEdge);
            fillPath.AddLine(popupInset, farEdge, popupInset, farEdge + 2);
            fillPath.AddLine(popupInset, farEdge + 2, farEdge + 2, farEdge + 2);
            fillPath.AddLine(farEdge + 2, farEdge + 2, farEdge + 2, popupInset);
            fillPath.AddLine(farEdge + 2, popupInset, farEdge, popupInset);
            fillPath.CloseFigure();
            using (var background = new SolidBrush(PopupBackColor))
            {
                eventArgs.Graphics.FillPath(background, fillPath);
            }

            using var outlinePath = new GraphicsPath();
            AddCornerOutline(outlinePath, circle, farEdge);
            using var border = new Pen(PopupBorderColor);
            eventArgs.Graphics.DrawPath(border, outlinePath);
        }

        private void AddCornerOutline(GraphicsPath path, RectangleF circle, int farEdge)
        {
            const float startAngle = -60;
            const float sweepAngle = -150;
            var center = new PointF(circle.Left + (circle.Width / 2), circle.Top + (circle.Height / 2));
            var start = PointOnCircle(center, OutlineRadius, startAngle);
            var end = PointOnCircle(center, OutlineRadius, startAngle + sweepAngle);
            path.AddBezier(
                new PointF(farEdge, popupInset),
                new PointF(farEdge - 8, popupInset),
                new PointF(start.X + 9, start.Y + 5),
                start);
            path.AddArc(circle, startAngle, sweepAngle);
            path.AddBezier(
                end,
                new PointF(end.X + 5, end.Y + 9),
                new PointF(popupInset, farEdge - 8),
                new PointF(popupInset, farEdge));
        }

        private static PointF PointOnCircle(PointF center, float radius, float angle)
        {
            var radians = angle * MathF.PI / 180;
            return new PointF(
                center.X + (radius * MathF.Cos(radians)),
                center.Y + (radius * MathF.Sin(radians)));
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }
    }

}
