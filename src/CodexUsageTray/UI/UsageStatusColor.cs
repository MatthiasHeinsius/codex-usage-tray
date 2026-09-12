namespace CodexUsageTray;

internal static class UsageStatusColor
{
    private static readonly Color Green = Color.FromArgb(16, 185, 129);
    private static readonly Color Yellow = Color.FromArgb(234, 179, 8);
    private static readonly Color Orange = Color.FromArgb(245, 158, 11);
    private static readonly Color Red = Color.FromArgb(239, 68, 68);

    public static Color ForRemainingPercent(int remainingPercent)
    {
        var remaining = Math.Clamp(remainingPercent, 0, 100);
        return remaining switch
        {
            <= 10 => Red,
            <= 25 => Blend(Red, Orange, (remaining - 10) / 15f),
            <= 50 => Blend(Orange, Yellow, (remaining - 25) / 25f),
            _ => Blend(Yellow, Green, (remaining - 50) / 50f)
        };
    }

    private static Color Blend(Color start, Color end, float amount) => Color.FromArgb(
        Blend(start.R, end.R, amount),
        Blend(start.G, end.G, amount),
        Blend(start.B, end.B, amount));

    private static int Blend(byte start, byte end, float amount) =>
        (int)MathF.Round(start + ((end - start) * amount));
}
