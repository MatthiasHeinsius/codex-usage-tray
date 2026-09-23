namespace CodexUsageTray;

internal static class AllowanceDemo
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan RunDuration = TimeSpan.FromSeconds(30);

    public static void Run()
    {
        using var form = new Form
        {
            Text = "Allowance lifecycle demo",
            ClientSize = new Size(360, 225),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            BackColor = Color.FromArgb(24, 27, 34),
            ForeColor = Color.FromArgb(235, 238, 244)
        };
        var indicator = new UsageActivityIndicator { Location = new Point(128, 8) };
        indicator.ShowSession(new CodexSessionActivity(null, CodexModel.Sol, "6"));
        form.Controls.Add(indicator);

        var stage = new Label
        {
            Bounds = new Rectangle(12, 116, 336, 27),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold),
            ForeColor = form.ForeColor
        };
        var remaining = new Label
        {
            Bounds = new Rectangle(12, 148, 336, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = form.ForeColor
        };
        var progress = new UsageProgressBar { Bounds = new Rectangle(24, 179, 312, 7) };
        var clock = new Label
        {
            Bounds = new Rectangle(12, 191, 336, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(148, 163, 184)
        };
        form.Controls.AddRange([stage, remaining, progress, clock]);

        var startedAt = DateTimeOffset.Now;
        void ShowFrame()
        {
            var now = DateTimeOffset.Now;
            var run = (long)((now - startedAt).TotalSeconds / RunDuration.TotalSeconds);
            var runStart = startedAt.AddTicks(run * RunDuration.Ticks);
            var elapsed = now - runStart;
            var (allowance, name) = FrameAt(runStart, elapsed, (int)(run % 2));
            indicator.ShowAllowance(allowance);
            indicator.Advance(now);
            stage.Text = $"Run {(run % 2) + 1}/2 · {name}";
            remaining.Text = $"{allowance.RemainingPercent}% allowance left";
            progress.Value = allowance.RemainingPercent ?? 0;
            var minuteElapsed = TimeSpan.FromTicks((now - startedAt).Ticks % TimeSpan.TicksPerMinute);
            clock.Text = $"Demo {minuteElapsed:mm\\:ss} / 01:00 · repeats";
        }

        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => ShowFrame();
        form.Shown += (_, _) =>
        {
            startedAt = DateTimeOffset.Now;
            ShowFrame();
            timer.Start();
        };
        Application.Run(form);
    }

    internal static (UsagePresentation.ActivityIndicatorPresentation Allowance, string Stage) FrameAt(
        DateTimeOffset runStart,
        TimeSpan elapsed,
        int runIndex)
    {
        var seconds = elapsed.TotalSeconds;
        var fastBurn = runIndex == 0;
        var remainingAtReset = fastBurn ? 0 : 60;
        if (seconds < 8)
        {
            var reset = runStart.AddHours(2.5);
            var burn = fastBurn ? 100 : 40;
            var remaining = Math.Max(remainingAtReset, 100 - (int)(seconds * burn / 8));
            return (new(AllowanceWindowKind.FiveHour, remaining, reset, WindowDuration, null, reset),
                fastBurn ? "Fast burn" : "Slow burn");
        }

        if (seconds < 14)
        {
            return (new(AllowanceWindowKind.FiveHour, remainingAtReset, runStart.AddHours(2.5), WindowDuration, null),
                fastBurn ? "Zero allowance" : "Allowance remains");
        }

        if (seconds < 19)
        {
            return (new(AllowanceWindowKind.FiveHour, remainingAtReset, runStart.AddSeconds(19), WindowDuration, null),
                "Before reset");
        }

        if (seconds < 23)
        {
            return (new(AllowanceWindowKind.FiveHour, 100, runStart.AddHours(5).AddSeconds(19), WindowDuration,
                fastBurn ? runStart.AddSeconds(19) : null),
                fastBurn ? "Reset after zero" : "Reset with 60% left");
        }

        if (seconds < 27)
        {
            var reset = runStart.AddHours(5).AddSeconds(23);
            return (new(AllowanceWindowKind.FiveHour, seconds < 25 ? 100 : 99, reset, WindowDuration, null, reset),
                "Activated");
        }

        var activeReset = runStart.AddHours(5).AddSeconds(27).AddMinutes(-6);
        return (new(AllowanceWindowKind.FiveHour, 99, activeReset, WindowDuration, null, activeReset),
            "Normal ring");
    }
}
