namespace CodexUsageTray.Tests;

public sealed class ApplicationUpdateSettingsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void AutomaticCheckSettingReadsStoredValueAndDefaultsToDisabled(object? storedValue, bool expected)
    {
        Assert.Equal(expected, ApplicationUpdateSettings.IsAutomaticCheckEnabled(storedValue));
    }
}
