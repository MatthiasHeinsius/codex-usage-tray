namespace CodexUsageTray.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void FailedLegacyCleanupRollsBackTheNewShortcut()
    {
        var shortcutRolledBack = false;

        Assert.Throws<UnauthorizedAccessException>(() =>
            StartupRegistration.DeleteLegacyOrRollbackShortcut(
                () => throw new UnauthorizedAccessException("Synthetic registry failure."),
                () => shortcutRolledBack = true));

        Assert.True(shortcutRolledBack);
    }
}
