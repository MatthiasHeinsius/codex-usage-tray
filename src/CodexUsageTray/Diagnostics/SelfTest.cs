namespace CodexUsageTray;

internal static class SelfTest
{
    public static int Run()
    {
        using var popup = new UsagePopupForm(initialCompactView: false);
        popup.CreateControl();
        _ = popup.Handle;

        using var icon = TrayIconRenderer.Create(65, 7);
        _ = LegalNotices.ReadAll();

        Console.WriteLine("Self-test passed.");
        return 0;
    }
}
