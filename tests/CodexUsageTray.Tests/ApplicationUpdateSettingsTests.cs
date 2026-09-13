namespace CodexUsageTray.Tests;

public sealed class ApplicationUpdateSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1L)]
    [InlineData("1")]
    public void AutomaticCheckSettingIsDisabledForMissingZeroOrNonDwordValues(object? storedValue)
    {
        Assert.False(ApplicationUpdateSettings.IsAutomaticCheckEnabled(storedValue));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    public void AutomaticCheckSettingIsEnabledForAnyNonzeroDword(int storedValue)
    {
        Assert.True(ApplicationUpdateSettings.IsAutomaticCheckEnabled(storedValue));
    }
}
