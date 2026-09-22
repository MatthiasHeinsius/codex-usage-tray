using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal enum UsagePace
{
    Low,
    Average,
    High
}

internal enum ResetPhase
{
    Normal,
    Soon,
    Imminent,
    Recent
}

internal sealed class UsageActivityIndicator : Control
{
    private const float RingDegreesPerSecondAtPace = 6;
    private static readonly Color TrackColor = Color.FromArgb(55, 65, 81);
    private static readonly TimeSpan RecentActivityWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IndicatorFadeWindow = TimeSpan.FromSeconds(15);
    private UsagePresentation.ActivityIndicatorPresentation allowance =
        UsagePresentation.ActivityIndicatorPresentation.Unavailable;
    private CodexSessionActivity session = CodexSessionActivity.Empty;
    private DateTimeOffset? recentResetAt;
    private DateTimeOffset? lastFrameAt;
    private bool activityLatched;
    private float ringAngle;
    private float highlightAngle;

    public UsageActivityIndicator()
    {
        AccessibleName = "Codex activity";
        AccessibleDescription = "No recent Codex token activity";
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
        Size = new Size(104, 104);
    }

    internal bool IsActive => activityLatched;
    internal float RingAngle => ringAngle;
    internal float IndicatorAngle => highlightAngle;
    internal float RingSweep => Math.Max(
        4,
        Math.Clamp(allowance.RemainingPercent ?? 100, 0, 100) * 3.6f);

    private bool HasRecentToken(DateTimeOffset now) =>
        session.LastTokenAt is { } lastTokenAt
        && now - lastTokenAt >= TimeSpan.FromSeconds(-2)
        && now - lastTokenAt <= RecentActivityWindow;

    internal UsagePace Pace(DateTimeOffset now)
    {
        if (allowance.RemainingPercent is not { } remaining
            || allowance.ResetsAt is not { } resetsAt
            || allowance.WindowDuration is not { } duration
            || duration <= TimeSpan.Zero)
        {
            return UsagePace.Average;
        }

        var elapsed = duration - (resetsAt - now);
        if (elapsed <= TimeSpan.Zero)
        {
            return UsagePace.Low;
        }

        var ratio = PaceRatio(now);
        return ratio switch
        {
            < .75 => UsagePace.Low,
            > 1.25 => UsagePace.High,
            _ => UsagePace.Average
        };
    }

    internal float RingDegreesPerSecond(DateTimeOffset now) =>
        RingDegreesPerSecondAtPace * (float)Math.Clamp(PaceRatio(now), 0, 4);

    internal float IndicatorStrength(DateTimeOffset now)
    {
        if (session.LastTokenAt is not { } lastTokenAt
            || now - lastTokenAt < TimeSpan.FromSeconds(-2))
        {
            return 0;
        }

        return Math.Clamp(1 - (float)((now - lastTokenAt) / IndicatorFadeWindow), 0, 1);
    }

    private double PaceRatio(DateTimeOffset now)
    {
        if (allowance.RemainingPercent is not { } remaining
            || allowance.ResetsAt is not { } resetsAt
            || allowance.WindowDuration is not { } duration
            || duration <= TimeSpan.Zero)
        {
            return 0;
        }

        var elapsed = duration - (resetsAt - now);
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var elapsedFraction = Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, .01, 1);
        return (100 - Math.Clamp(remaining, 0, 100)) / 100d / elapsedFraction;
    }

    internal ResetPhase Reset(DateTimeOffset now)
    {
        if (recentResetAt is { } resetAt)
        {
            var observedElapsed = now - resetAt;
            if (observedElapsed >= TimeSpan.Zero
                && observedElapsed <= TimeSpan.FromMinutes(5))
            {
                return ResetPhase.Recent;
            }
        }

        if (allowance.ResetsAt is not { } resetsAt)
        {
            return ResetPhase.Normal;
        }

        if (allowance.WindowDuration is { } duration
            && duration > TimeSpan.Zero)
        {
            var windowElapsed = now - (resetsAt - duration);
            if (windowElapsed >= TimeSpan.Zero
                && windowElapsed <= TimeSpan.FromMinutes(5))
            {
                return ResetPhase.Recent;
            }
        }

        var remaining = resetsAt - now;
        if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromMinutes(1))
        {
            return ResetPhase.Imminent;
        }

        return remaining > TimeSpan.Zero && remaining <= TimeSpan.FromMinutes(5)
            ? ResetPhase.Soon
            : ResetPhase.Normal;
    }

    internal void ShowAllowance(UsagePresentation.ActivityIndicatorPresentation presentation)
    {
        allowance = presentation;
        if (presentation.ResetObservedAt is { } resetAt)
        {
            recentResetAt = resetAt;
        }

        Invalidate();
    }

    internal void ShowSession(CodexSessionActivity activity)
    {
        session = activity;
        activityLatched = HasRecentToken(DateTimeOffset.Now);
        Invalidate();
    }

    internal void Advance(DateTimeOffset now)
    {
        var elapsed = lastFrameAt is { } previous
            ? Math.Clamp((now - previous).TotalSeconds, 0, .25)
            : 0;
        lastFrameAt = now;
        activityLatched = HasRecentToken(now);
        var ringSpeed = RingDegreesPerSecond(now);
        ringAngle = Normalize(ringAngle + ((float)elapsed * ringSpeed));
        highlightAngle = Normalize(highlightAngle
            + ((float)elapsed * Math.Max(18, ringSpeed * 6) * IndicatorStrength(now)));

        AccessibleDescription = Describe(now);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var now = DateTimeOffset.Now;
        DrawRing(eventArgs.Graphics, now);
        DrawCreature(eventArgs.Graphics, now);
    }

    private void DrawRing(Graphics graphics, DateTimeOffset now)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(13, 13, 78, 78);
        var remaining = Math.Clamp(allowance.RemainingPercent ?? 100, 0, 100);
        var usageColor = allowance.RemainingPercent is null
            ? Color.FromArgb(100, 116, 139)
            : UsageStatusColor.ForRemainingPercent(remaining);
        var start = -90 + ringAngle;

        using (var shadow = new Pen(Color.FromArgb(70, 7, 11, 18), 10))
        {
            graphics.DrawEllipse(shadow, bounds);
        }

        using (var track = new Pen(TrackColor, 6))
        {
            graphics.DrawEllipse(track, bounds);
        }

        using (var value = new Pen(usageColor, 7))
        {
            graphics.DrawArc(value, bounds, start, RingSweep);
        }

        DrawResetPulse(graphics, bounds, now);

        var strength = IndicatorStrength(now);
        if (strength <= 0)
        {
            return;
        }

        var relativeHighlight = Normalize(highlightAngle - ringAngle);
        var midpoint = Normalize(relativeHighlight + 9);
        var baseColor = midpoint <= remaining * 3.6f ? usageColor : TrackColor;
        var pulse = .5f + (.5f * MathF.Sin((float)(now.ToUnixTimeMilliseconds() / 2100d * Math.PI * 2)));
        var shifted = Blend(baseColor, Color.White, .7f);
        using var glow = new Pen(Color.FromArgb((int)((45 + (45 * pulse)) * strength), shifted), 15 + (3 * pulse))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(glow, bounds, -90 + highlightAngle, 18);

        var alpha = (int)((145 + (100 * pulse)) * strength);
        using var highlight = new Pen(Color.FromArgb(alpha, shifted), 8 + (5 * pulse))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(highlight, bounds, -90 + highlightAngle, 18);
    }

    private void DrawResetPulse(Graphics graphics, RectangleF bounds, DateTimeOffset now)
    {
        var phase = Reset(now);
        if (phase == ResetPhase.Normal)
        {
            return;
        }

        var seconds = now.ToUnixTimeMilliseconds() / 1000d;
        float strength;
        Color color;
        switch (phase)
        {
            case ResetPhase.Soon:
                strength = SmoothPulse(seconds / 2.6);
                color = Color.FromArgb(103, 232, 249);
                break;
            case ResetPhase.Imminent:
                strength = Math.Max(SmoothPulse(seconds / 1.8), SmoothPulse((seconds / 1.8) + .28) * .8f);
                color = Color.FromArgb(251, 191, 36);
                break;
            default:
                strength = MathF.Pow(1 - (float)(seconds % 3.2 / 3.2), 2);
                color = Color.FromArgb(103, 232, 249);
                break;
        }

        using var pulse = new Pen(Color.FromArgb(18 + (int)(100 * strength), color), 6 + (3 * strength));
        graphics.DrawEllipse(pulse, bounds);
    }

    private void DrawCreature(Graphics graphics, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeMilliseconds() / 1000d;
        var active = IsActive;
        var animationRate = FaceRateFor(Pace(now));
        var headStep = active
            ? (int)Math.Round(Math.Sin(seconds * animationRate * 3.1))
            : (seconds % 6.5 is > 4.9 and < 5.8 ? -1 : 0);
        var squish = active && seconds % (2.1 / animationRate) is > 1.35 and < 1.72;
        var blink = seconds % 5.1 is > 4.72 and < 4.91;
        var laugh = seconds % 5.8 is > 4.15 and < 5.02;
        var look = (int)(seconds / 1.7) % 3 - 1;
        var earLift = active && (int)(seconds * animationRate * 3) % 2 == 0 ? -1 : 0;

        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        var state = graphics.Save();
        graphics.TranslateTransform(20, 20 + headStep);
        graphics.ScaleTransform(2, squish ? 1.92f : 2);
        switch (session.Model)
        {
            case CodexModel.Luna:
                DrawLuna(graphics, look, blink, laugh, earLift);
                break;
            case CodexModel.Terra:
                DrawTerra(graphics, look, blink, laugh, earLift);
                break;
            case CodexModel.Astra:
                DrawAstra(graphics, look, blink, laugh, earLift);
                break;
            case CodexModel.Sol:
                DrawSol(graphics, look, blink, laugh, earLift);
                break;
            default:
                DrawUnknown(graphics, look, blink);
                break;
        }

        graphics.Restore(state);
    }

    private static void DrawLuna(Graphics graphics, int look, bool blink, bool laugh, int earLift)
    {
        FillPolygon(graphics, Color.FromArgb(23, 21, 46),
            (5, 4), (10, 4), (10, 8), (22, 8), (22, 4), (27, 4), (27, 10),
            (30, 10), (30, 25), (26, 25), (26, 29), (7, 29), (7, 26), (3, 26), (3, 10), (5, 10));
        FillRect(graphics, Color.FromArgb(118, 87, 174), 6, 5 + earLift, 3, 8 - earLift);
        FillRect(graphics, Color.FromArgb(118, 87, 174), 23, 5 + earLift, 3, 8 - earLift);
        FillPolygon(graphics, Color.FromArgb(200, 177, 239),
            (8, 10), (24, 10), (24, 12), (27, 12), (27, 24), (23, 24), (23, 27),
            (9, 27), (9, 25), (6, 25), (6, 12), (8, 12));
        FillPolygon(graphics, Color.FromArgb(235, 226, 255),
            (9, 12), (18, 12), (18, 14), (24, 14), (24, 16), (8, 16), (8, 24), (6, 24), (6, 14), (9, 14));
        DrawEyes(graphics, Color.FromArgb(46, 36, 72), look, blink, 11, 18, 20, 18, 4, 3);
        DrawMouth(graphics, Color.FromArgb(76, 40, 82), laugh, 15, 22, 5);
        FillPolygon(graphics, Color.FromArgb(155, 114, 204), (3, 17), (1, 17), (1, 22), (4, 22), (4, 24), (7, 24), (7, 21), (5, 21));
        FillPolygon(graphics, Color.FromArgb(155, 114, 204), (28, 17), (31, 17), (31, 23), (28, 23), (28, 25), (25, 25), (25, 21), (27, 21), (27, 19), (28, 19));
    }

    private static void DrawTerra(Graphics graphics, int look, bool blink, bool laugh, int earLift)
    {
        FillPolygon(graphics, Color.FromArgb(23, 48, 43),
            (8, 4), (13, 4), (13, 7), (20, 7), (20, 4), (25, 4), (25, 9),
            (29, 9), (29, 14), (31, 14), (31, 24), (27, 24), (27, 29), (6, 29),
            (6, 26), (2, 26), (2, 14), (5, 14), (5, 9), (8, 9));
        FillRect(graphics, Color.FromArgb(84, 169, 103), 9, 5 + earLift, 3, 6 - earLift);
        FillRect(graphics, Color.FromArgb(84, 169, 103), 21, 5 + earLift, 3, 6 - earLift);
        FillPolygon(graphics, Color.FromArgb(149, 204, 133),
            (7, 10), (26, 10), (26, 13), (29, 13), (29, 24), (25, 24), (25, 27),
            (7, 27), (7, 25), (4, 25), (4, 14), (7, 14));
        FillPolygon(graphics, Color.FromArgb(200, 236, 166),
            (8, 12), (19, 12), (19, 14), (26, 14), (26, 16), (7, 16), (7, 23), (5, 23), (5, 14), (8, 14));
        DrawEyes(graphics, Color.FromArgb(30, 60, 52), look, blink, 10, 16, 21, 16, 5, 5);
        DrawMouth(graphics, Color.FromArgb(49, 83, 74), laugh, 14, 22, 8);
        FillRect(graphics, Color.FromArgb(117, 185, 108), 3, 19, 3, 5);
        FillRect(graphics, Color.FromArgb(117, 185, 108), 27, 19, 3, 5);
    }

    private static void DrawSol(Graphics graphics, int look, bool blink, bool laugh, int earLift)
    {
        FillPolygon(graphics, Color.FromArgb(61, 32, 45),
            (6, 5), (11, 5), (11, 2 + earLift), (15, 2 + earLift), (15, 6), (23, 6),
            (23, 3 + earLift), (27, 3 + earLift), (27, 9), (30, 9), (30, 24), (26, 24),
            (26, 29), (7, 29), (7, 26), (3, 26), (3, 10), (6, 10));
        FillPolygon(graphics, Color.FromArgb(217, 91, 46),
            (8, 5), (12, 5), (12, 10), (15, 10), (15, 7), (24, 7), (24, 5),
            (26, 5), (26, 12), (28, 12), (28, 24), (24, 24), (24, 27), (8, 27),
            (8, 25), (5, 25), (5, 11), (8, 11));
        FillPolygon(graphics, Color.FromArgb(244, 161, 58),
            (9, 11), (24, 11), (24, 13), (27, 13), (27, 23), (23, 23), (23, 26),
            (10, 26), (10, 24), (7, 24), (7, 14), (9, 14));
        FillPolygon(graphics, Color.FromArgb(255, 229, 154),
            (10, 12), (18, 12), (18, 14), (24, 14), (24, 16), (9, 16), (9, 23), (7, 23), (7, 14), (10, 14));
        DrawEyes(graphics, Color.FromArgb(89, 34, 56), look, blink, 11, 17, 20, 16, 4, 3);
        DrawMouth(graphics, Color.FromArgb(100, 36, 59), laugh, 13, 21, 9);
        if (laugh)
        {
            FillRect(graphics, Color.FromArgb(255, 243, 192), 14, 21, 2, 2);
            FillRect(graphics, Color.FromArgb(255, 243, 192), 20, 21, 2, 2);
            FillRect(graphics, Color.FromArgb(240, 114, 131), 16, 26, 4, 1);
        }
    }

    private static void DrawAstra(Graphics graphics, int look, bool blink, bool laugh, int antennaLift)
    {
        FillPolygon(graphics, Color.FromArgb(19, 33, 59),
            (10, 2), (22, 2), (22, 5), (27, 5), (27, 9), (30, 9), (30, 24),
            (26, 24), (26, 29), (7, 29), (7, 26), (3, 26), (3, 9), (6, 9), (6, 5), (10, 5));
        FillPolygon(graphics, Color.FromArgb(77, 131, 189),
            (11, 4), (21, 4), (21, 7), (26, 7), (26, 11), (28, 11), (28, 23),
            (24, 23), (24, 27), (8, 27), (8, 24), (5, 24), (5, 10), (8, 10), (8, 7), (11, 7));
        FillPolygon(graphics, Color.FromArgb(120, 199, 235),
            (9, 9), (24, 9), (24, 12), (27, 12), (27, 22), (23, 22), (23, 25),
            (10, 25), (10, 23), (7, 23), (7, 12), (9, 12));
        FillRect(graphics, Color.FromArgb(35, 54, 83), 8, 13, 18, 9);
        DrawEyes(graphics, Color.FromArgb(184, 255, 241), look, blink, 11, 16, 20, 16, 4, 3);
        DrawMouth(graphics, Color.FromArgb(112, 201, 219), laugh, 15, 21, 6);
        FillPolygon(graphics, Color.FromArgb(239, 250, 255),
            (14, 2 + antennaLift), (18, 2 + antennaLift), (18, 4), (20, 4), (20, 7),
            (18, 7), (18, 9), (14, 9), (14, 7), (12, 7), (12, 4), (14, 4));
        FillRect(graphics, Color.FromArgb(93, 229, 208), 3, 13, 3, 7);
        FillRect(graphics, Color.FromArgb(93, 229, 208), 27, 13, 3, 7);
    }

    private static void DrawUnknown(Graphics graphics, int look, bool blink)
    {
        FillPolygon(graphics, Color.FromArgb(32, 41, 56),
            (7, 4), (25, 4), (25, 7), (29, 7), (29, 28), (25, 28), (25, 31),
            (7, 31), (7, 28), (3, 28), (3, 7), (7, 7));
        FillRect(graphics, Color.FromArgb(148, 163, 184), 7, 8, 18, 17);
        DrawEyes(graphics, Color.FromArgb(32, 41, 56), look, blink, 10, 14, 20, 14, 4, 3);
        FillRect(graphics, Color.FromArgb(32, 41, 56), 14, 19, 6, 3);
        FillRect(graphics, Color.FromArgb(32, 41, 56), 17, 21, 3, 5);
        FillRect(graphics, Color.FromArgb(219, 234, 254), 15, 27, 3, 2);
    }

    private static void DrawEyes(
        Graphics graphics,
        Color color,
        int look,
        bool blink,
        int left,
        int top,
        int right,
        int rightTop,
        int width,
        int height)
    {
        var eyeHeight = blink ? 1 : height;
        var eyeTop = top + (blink ? height / 2 : 0);
        var secondEyeTop = rightTop + (blink ? height / 2 : 0);
        FillRect(graphics, color, left + look, eyeTop, width, eyeHeight);
        FillRect(graphics, color, right + look, secondEyeTop, width, eyeHeight);
        if (!blink)
        {
            FillRect(graphics, Color.White, left + look + 1, eyeTop, 1, 1);
            FillRect(graphics, Color.White, right + look + 1, secondEyeTop, 1, 1);
        }
    }

    private static void DrawMouth(Graphics graphics, Color color, bool laugh, int left, int top, int width)
    {
        if (laugh)
        {
            FillRect(graphics, color, left, top - 1, width, 5);
            FillRect(graphics, Color.FromArgb(241, 114, 131), left + 2, top + 2, Math.Max(2, width - 4), 1);
            return;
        }

        FillRect(graphics, color, left, top, width, 2);
        FillRect(graphics, color, left + 1, top + 2, Math.Max(2, width - 2), 1);
    }

    private string Describe(DateTimeOffset now)
    {
        var activity = IsActive ? $"active at {Pace(now).ToString().ToLowerInvariant()} pace" : "idle";
        var reset = Reset(now) switch
        {
            ResetPhase.Soon => ", reset in under five minutes",
            ResetPhase.Imminent => ", reset in under one minute",
            ResetPhase.Recent => ", recently reset",
            _ => string.Empty
        };
        return $"Codex is {activity}, {session.ModelDisplayName}{reset}.";
    }

    private static float FaceRateFor(UsagePace pace) => pace switch
    {
        UsagePace.Low => .72f,
        UsagePace.High => 1.45f,
        _ => 1
    };

    private static float SmoothPulse(double cycles)
    {
        var value = .5f - (.5f * MathF.Cos((float)(cycles * Math.PI * 2)));
        return value * value * (3 - (2 * value));
    }

    private static float Normalize(float angle)
    {
        angle %= 360;
        return angle < 0 ? angle + 360 : angle;
    }

    private static Color Blend(Color start, Color end, float amount) => Color.FromArgb(
        (int)MathF.Round(start.R + ((end.R - start.R) * amount)),
        (int)MathF.Round(start.G + ((end.G - start.G) * amount)),
        (int)MathF.Round(start.B + ((end.B - start.B) * amount)));

    private static void FillRect(Graphics graphics, Color color, int x, int y, int width, int height)
    {
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, x, y, width, height);
    }

    private static void FillPolygon(Graphics graphics, Color color, params (int X, int Y)[] points)
    {
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, points.Select(point => new Point(point.X, point.Y)).ToArray());
    }
}
