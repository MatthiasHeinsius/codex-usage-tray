using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace CodexUsageTray.Tests;

public sealed class RegistryApplicationSettingsTests
{
    private static readonly string[] BooleanNames =
        ["CompactPopup", "AutomaticUpdateChecks", "AutoStartUsageWindows", "AllowanceNotifications"];

    [Fact]
    public void MissingSettingsUseDefaultsUntilTheFirstWrite()
    {
        using var registry = new TestRegistryKey();
        var path = registry.Path + @"\missing";
        var settings = new RegistryApplicationSettings(path);

        AssertDefaults(settings);
        using var missing = Registry.CurrentUser.OpenSubKey(path);
        Assert.Null(missing);
        settings.AutomaticUpdateChecksEnabled = true;
        using var created = Registry.CurrentUser.OpenSubKey(path);
        Assert.NotNull(created);
        Assert.Equal(1, created.GetValue("AutomaticUpdateChecks"));
    }

    [Theory]
    [InlineData(null, RegistryValueKind.DWord, false)]
    [InlineData(0, RegistryValueKind.DWord, false)]
    [InlineData(1L, RegistryValueKind.QWord, false)]
    [InlineData("1", RegistryValueKind.String, false)]
    [InlineData(-1, RegistryValueKind.DWord, true)]
    [InlineData(1, RegistryValueKind.DWord, true)]
    [InlineData(2, RegistryValueKind.DWord, true)]
    public void BooleanPreferencesReadPersistedDwords(object? value, RegistryValueKind kind, bool expected)
    {
        using var registry = new TestRegistryKey();
        if (value is not null)
        {
            foreach (var name in BooleanNames)
            {
                registry.Key.SetValue(name, value, kind);
            }
        }

        var settings = new RegistryApplicationSettings(registry.Path);

        Assert.Equal(expected, settings.CompactPopup);
        Assert.Equal(expected, settings.AutomaticUpdateChecksEnabled);
        Assert.Equal(expected, settings.ActivationEnabled);
        Assert.Equal(expected, settings.NotificationsEnabled);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, true, true)]
    public void BooleanPreferencesRoundTripIndependentlyUsingExistingValueNames(
        bool compact, bool automaticUpdates, bool activation, bool notifications)
    {
        using var registry = new TestRegistryKey();
        var settings = new RegistryApplicationSettings(registry.Path)
        {
            CompactPopup = compact,
            AutomaticUpdateChecksEnabled = automaticUpdates,
            ActivationEnabled = activation,
            NotificationsEnabled = notifications
        };

        Assert.Equal(compact ? 1 : 0, registry.Key.GetValue("CompactPopup"));
        Assert.Equal(automaticUpdates ? 1 : 0, registry.Key.GetValue("AutomaticUpdateChecks"));
        Assert.Equal(activation ? 1 : 0, registry.Key.GetValue("AutoStartUsageWindows"));
        Assert.Equal(notifications ? 1 : 0, registry.Key.GetValue("AllowanceNotifications"));
        foreach (var name in BooleanNames)
        {
            Assert.Equal(RegistryValueKind.DWord, registry.Key.GetValueKind(name));
        }

        settings = new RegistryApplicationSettings(registry.Path);
        Assert.Equal(compact, settings.CompactPopup);
        Assert.Equal(automaticUpdates, settings.AutomaticUpdateChecksEnabled);
        Assert.Equal(activation, settings.ActivationEnabled);
        Assert.Equal(notifications, settings.NotificationsEnabled);
    }

    [Fact]
    public void ActivatedResetsRoundTripIndependentlyAsUnixSeconds()
    {
        using var registry = new TestRegistryKey();
        var settings = new RegistryApplicationSettings(registry.Path);
        var fiveHour = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.FromHours(2));
        var weekly = fiveHour.AddDays(4);

        settings.WriteActivatedReset(AllowanceWindowKind.FiveHour, fiveHour);
        settings.WriteActivatedReset(AllowanceWindowKind.Weekly, weekly);

        Assert.Equal(fiveHour.ToUnixTimeSeconds(), registry.Key.GetValue("LastStartedFiveHourReset"));
        Assert.Equal(weekly.ToUnixTimeSeconds(), registry.Key.GetValue("LastStartedWeeklyReset"));
        Assert.Equal(RegistryValueKind.QWord, registry.Key.GetValueKind("LastStartedFiveHourReset"));
        Assert.Equal(RegistryValueKind.QWord, registry.Key.GetValueKind("LastStartedWeeklyReset"));
        settings = new RegistryApplicationSettings(registry.Path);
        Assert.Equal(fiveHour, settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
        Assert.Equal(weekly, settings.ReadActivatedReset(AllowanceWindowKind.Weekly));
    }

    [Theory]
    [InlineData("1", RegistryValueKind.String)]
    [InlineData(long.MinValue, RegistryValueKind.QWord)]
    [InlineData(long.MaxValue, RegistryValueKind.QWord)]
    public void MalformedActivatedResetsAreUnknown(object value, RegistryValueKind kind)
    {
        using var registry = new TestRegistryKey();
        registry.Key.SetValue("LastStartedFiveHourReset", value, kind);
        registry.Key.SetValue("LastStartedWeeklyReset", value, kind);
        var settings = new RegistryApplicationSettings(registry.Path);

        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.Weekly));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ActivatedResetsStillAcceptPersistedDwords(int seconds)
    {
        using var registry = new TestRegistryKey();
        registry.Key.SetValue("LastStartedFiveHourReset", seconds, RegistryValueKind.DWord);
        var settings = new RegistryApplicationSettings(registry.Path);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(seconds), settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
    }

    [Fact]
    public void DeniedReadsDefaultPreferencesButPreserveActivationHistoryFailures()
    {
        using var registry = new TestRegistryKey();
        foreach (var name in BooleanNames)
        {
            registry.Key.SetValue(name, 1, RegistryValueKind.DWord);
        }

        registry.Key.SetValue("LastStartedFiveHourReset", 1L, RegistryValueKind.QWord);
        registry.Key.SetValue("LastStartedWeeklyReset", 1L, RegistryValueKind.QWord);
        registry.Deny(RegistryRights.QueryValues);

        var settings = new RegistryApplicationSettings(registry.Path);
        Assert.False(settings.CompactPopup);
        Assert.False(settings.AutomaticUpdateChecksEnabled);
        Assert.False(settings.ActivationEnabled);
        Assert.False(settings.NotificationsEnabled);
        var failure = Record.Exception(() => settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
        Assert.True(failure is UnauthorizedAccessException or SecurityException);
    }

    [Fact]
    public void DeniedWritesPropagateExceptForThePopupView()
    {
        using var registry = new TestRegistryKey();
        registry.Deny(RegistryRights.SetValue);
        var settings = new RegistryApplicationSettings(registry.Path);

        settings.CompactPopup = true;
        Assert.ThrowsAny<UnauthorizedAccessException>(() => settings.AutomaticUpdateChecksEnabled = true);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => settings.ActivationEnabled = true);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => settings.NotificationsEnabled = true);
        Assert.ThrowsAny<UnauthorizedAccessException>(
            () => settings.WriteActivatedReset(AllowanceWindowKind.FiveHour, DateTimeOffset.UnixEpoch));
        Assert.Empty(registry.Key.GetValueNames());
    }

    private static void AssertDefaults(RegistryApplicationSettings settings)
    {
        Assert.False(settings.CompactPopup);
        Assert.False(settings.AutomaticUpdateChecksEnabled);
        Assert.False(settings.ActivationEnabled);
        Assert.False(settings.NotificationsEnabled);
        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.Weekly));
    }

    private sealed class TestRegistryKey : IDisposable
    {
        private RegistrySecurity? originalSecurity;

        internal TestRegistryKey()
        {
            using var created = Registry.CurrentUser.CreateSubKey(Path);
            Key = Registry.CurrentUser.OpenSubKey(Path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)!;
        }

        internal string Path { get; } = @"Software\CodexUsageTray.Tests-" + Guid.NewGuid().ToString("N");
        internal RegistryKey Key { get; }

        internal void Deny(RegistryRights rights)
        {
            originalSecurity = Key.GetAccessControl();
            var security = Key.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new RegistryAccessRule(identity.User!, rights, AccessControlType.Deny));
            Key.SetAccessControl(security);
        }

        public void Dispose()
        {
            try
            {
                if (originalSecurity is not null)
                {
                    Key.SetAccessControl(originalSecurity);
                }
            }
            finally
            {
                Key.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
            }
        }
    }
}
