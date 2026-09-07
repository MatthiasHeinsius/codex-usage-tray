namespace CodexUsageTray;

internal sealed class UsagePopupForm : Form
{
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
    private UsageSnapshot? displayedSnapshot;
    private bool compactView;

    public event EventHandler? RefreshRequested;
    public event EventHandler? UsagePageRequested;

    internal bool HeaderControlsOverlap =>
        title.Left
        + TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, TextFormatFlags.NoPadding).Width
        + 8
        > usagePageButton.Left;

    public UsagePopupForm()
    {
        Text = "Codex usage";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 27, 34);
        ForeColor = Color.FromArgb(235, 238, 244);
        ClientSize = new Size(440, 416);
        Padding = new Padding(26, 20, 26, 20);
        Font = new Font("Segoe UI", 9.25f);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

        title = new Label
        {
            Text = "Codex usage",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(26, 20)
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
        };
        toolTip.SetToolTip(viewModeButton, viewModeButton.IsCompact ? "Show extended view" : "Show compact view");

        statusLabel = new Label
        {
            Text = "Connecting...",
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoEllipsis = true,
            Location = new Point(27, 55),
            Size = new Size(387, 22)
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
            toolTip.SetToolTip(pinButton, pinButton.IsPinned ? "Unpin popup" : "Keep open and on top");
        };
        toolTip.SetToolTip(pinButton, "Keep open and on top");
        Deactivate += (_, _) =>
        {
            if (!pinButton.IsPinned)
            {
                Hide();
            }
        };

        refreshButton = new RefreshIconButton
        {
            AccessibleName = "Refresh usage",
            AccessibleDescription = "Refresh the Codex usage figures",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(203, 213, 225),
            BackColor = Color.FromArgb(36, 41, 51),
            Location = new Point(380, 362),
            Size = new Size(34, 30),
            Padding = new Padding(0),
            Cursor = Cursors.Hand
        };
        refreshButton.FlatAppearance.BorderColor = Color.FromArgb(61, 68, 82);
        refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        fiveHourTitle = MakeSectionTitle("5-hour limit", 95);
        fiveHourValue = MakeValueLabel(95);
        fiveHourReset = MakeMutedLabel(126);
        fiveHourBar = new UsageProgressBar { Location = new Point(26, 154), Width = 388, Value = 0 };

        weeklyTitle = MakeSectionTitle("Weekly limit", 185);
        weeklyValue = MakeValueLabel(185);
        weeklyReset = MakeMutedLabel(216);
        weeklyBar = new UsageProgressBar { Location = new Point(26, 244), Width = 388, Value = 0 };

        limitsDivider = new Panel
        {
            BackColor = Color.FromArgb(48, 54, 66),
            Location = new Point(26, 273),
            Size = new Size(388, 1)
        };

        todayTitle = MakeMutedLabel("Inference today", 290, 26, 220);
        todayTokens = MakeTokenValue(290, 254, 160);
        lifetimeTitle = MakeMutedLabel("Inference total", 320, 26, 220);
        lifetimeTokens = MakeTokenValue(320, 254, 160);
        inferenceDivider = new Panel
        {
            BackColor = Color.FromArgb(48, 54, 66),
            Location = new Point(26, 350),
            Size = new Size(388, 1)
        };
        updatedLabel = MakeMutedLabel("Not updated yet", 366, 26, 338);
        updatedLabel.Font = new Font("Segoe UI", 8f);
        Controls.AddRange([
            title, usagePageButton, statusLabel, viewModeButton, pinButton, refreshButton,
            fiveHourTitle, fiveHourValue, fiveHourReset, fiveHourBar,
            weeklyTitle, weeklyValue, weeklyReset, weeklyBar,
            limitsDivider, todayTitle, todayTokens, lifetimeTitle, lifetimeTokens, inferenceDivider, updatedLabel
        ]);
        ApplyViewMode(viewModeButton.IsCompact, preserveBottom: false);
    }

    public void SetLoading(bool loading)
    {
        refreshButton.Enabled = !loading;
        if (loading)
        {
            statusLabel.Text = "Reading your Codex account";
        }
    }

    public void ShowSnapshot(UsageSnapshot snapshot)
    {
        displayedSnapshot = snapshot;
        RenderSnapshot(snapshot);
    }

    private void RenderSnapshot(UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        statusLabel.Text = BuildStatus(snapshot);
        fiveHourValue.Text = UsageText.PercentLeft(snapshot.FiveHour);
        fiveHourReset.Text = compactView
            ? UsageText.CompactCountdown(snapshot.FiveHour, now)
            : UsageText.ResetText(snapshot.FiveHour, now);
        fiveHourBar.Value = snapshot.FiveHour?.RemainingPercent ?? 0;
        weeklyValue.Text = UsageText.PercentLeft(snapshot.Weekly);
        weeklyReset.Text = compactView
            ? UsageText.CompactCountdown(snapshot.Weekly, now)
            : UsageText.ResetText(snapshot.Weekly, now);
        weeklyBar.Value = snapshot.Weekly?.RemainingPercent ?? 0;
        todayTokens.Text = UsageText.TokenLabel(snapshot.TodayTokens);
        lifetimeTokens.Text = UsageText.TokenLabel(snapshot.LifetimeTokens);
        updatedLabel.Text = $"Updated {snapshot.RetrievedAt.LocalDateTime:t}";
    }

    private void ApplyViewMode(bool compact, bool preserveBottom)
    {
        var previousBottom = Bottom;
        var targetHeight = compact ? 154 : 416;
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
        refreshButton.Visible = !compact;

        if (compact)
        {
            fiveHourTitle.Location = new Point(26, 73);
            fiveHourValue.Location = new Point(142, 71);
            fiveHourValue.Size = new Size(112, 24);
            fiveHourReset.Location = new Point(270, 73);
            fiveHourReset.Size = new Size(144, 22);

            weeklyTitle.Location = new Point(26, 109);
            weeklyValue.Location = new Point(142, 107);
            weeklyValue.Size = new Size(112, 24);
            weeklyReset.Location = new Point(270, 109);
            weeklyReset.Size = new Size(144, 22);
        }
        else
        {
            fiveHourTitle.Location = new Point(26, 95);
            fiveHourValue.Location = new Point(264, 95);
            fiveHourValue.Size = new Size(150, 24);
            fiveHourReset.Location = new Point(26, 126);
            fiveHourReset.Size = new Size(388, 22);

            weeklyTitle.Location = new Point(26, 185);
            weeklyValue.Location = new Point(264, 185);
            weeklyValue.Size = new Size(150, 24);
            weeklyReset.Location = new Point(26, 216);
            weeklyReset.Size = new Size(388, 22);
        }

        if (preserveBottom && Visible)
        {
            SetBounds(Left, previousBottom - targetHeight, 440, targetHeight, BoundsSpecified.All);
        }
        else
        {
            ClientSize = new Size(440, targetHeight);
        }

        toolTip.SetToolTip(viewModeButton, compact ? "Show extended view" : "Show compact view");
        if (displayedSnapshot is not null)
        {
            RenderSnapshot(displayedSnapshot);
        }

        Invalidate(invalidateChildren: true);
        Update();
    }

    public void ShowError(string message)
    {
        statusLabel.Text = "Could not refresh";
        updatedLabel.Text = message;
    }

    public void ShowNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(Cursor.Position.X - Width + 20, area.Left, area.Right - Width);
        var y = area.Bottom - Height - 8;
        Location = new Point(x, y);
        Show();
        Activate();
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
            toolTip.Dispose();
        }

        base.Dispose(disposing);
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

    private static Label MakeSectionTitle(string text, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
        AutoSize = true,
        Location = new Point(26, y)
    };

    private static Label MakeValueLabel(int y) => new()
    {
        Text = "Unavailable",
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
        Location = new Point(264, y),
        Size = new Size(150, 24)
    };

    private static Label MakeMutedLabel(int y) => MakeMutedLabel("Reset time unavailable", y, 26, 388);

    private static Label MakeMutedLabel(string text, int y, int x, int width) => new()
    {
        Text = text,
        ForeColor = Color.FromArgb(148, 163, 184),
        AutoEllipsis = true,
        Location = new Point(x, y),
        Size = new Size(width, 22)
    };

    private static Label MakeTokenValue(int y, int x, int width) => new()
    {
        Text = "Unavailable",
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
        Location = new Point(x, y),
        Size = new Size(width, 22)
    };

    private static string BuildStatus(UsageSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Plan) && string.IsNullOrWhiteSpace(snapshot.LimitName))
        {
            return "Signed in through Codex";
        }

        var plan = snapshot.Plan is null ? null : char.ToUpperInvariant(snapshot.Plan[0]) + snapshot.Plan[1..];
        return string.Join(" · ", new[] { plan, snapshot.LimitName }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
