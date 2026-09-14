using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace CodexUsageTray.Tests;

internal sealed class TestRegistryKey : IDisposable
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
