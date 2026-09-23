using System.Runtime.InteropServices;

namespace CodexUsageTray.Tests;

public sealed class GuidNotifyIconTests(ITestOutputHelper output)
{
    [Fact]
    public void ShellDataUsesThePermanentTrayIconGuid()
    {
        var data = GuidNotifyIcon.CreateData(123, 456, "tooltip");

        Assert.Equal(new Guid("f5d55d46-5431-46c5-86af-21c872a4fd06"), data.Guid);
        Assert.True(data.Flags.HasFlag(GuidNotifyIcon.NotifyIconDataFlags.Guid));
    }

    [Theory]
    [InlineData(0x0201, "down", 1)]
    [InlineData(0x0203, "down", 2)]
    [InlineData(0x0202, "click", 1)]
    public Task NativeMouseMessagesReachTheTrayEvents(int message, string expectedEvent, int expectedClicks) =>
        StaTest.RunAsync(() =>
        {
            var previousWindows = ThreadWindows();
            var iconId = Guid.NewGuid();
            using var icon = new GuidNotifyIcon(iconId) { Icon = SystemIcons.Application, Visible = true };
            var handle = Assert.Single(ThreadWindows().Except(previousWindows));
            var events = new List<(string Event, MouseButtons Button, int Clicks)>();
            icon.MouseDown += (_, args) => events.Add(("down", args.Button, args.Clicks));
            icon.MouseClick += (_, args) => events.Add(("click", args.Button, args.Clicks));
            var data = GuidNotifyIcon.CreateData(handle, 0, string.Empty, iconId);

            _ = NativeMethods.SendMessage(handle, data.CallbackMessage, 0, message);

            Assert.Equal((expectedEvent, MouseButtons.Left, expectedClicks), Assert.Single(events));
        });

    [Fact]
    public Task DisposalDestroysTheNativeCallbackWindowAndRejectsFurtherUpdates() =>
        StaTest.RunAsync(() =>
        {
            var previousWindows = ThreadWindows();
            using var icon = new GuidNotifyIcon(Guid.NewGuid()) { Icon = SystemIcons.Application, Visible = true };
            var handle = Assert.Single(ThreadWindows().Except(previousWindows));

            icon.Dispose();

            Assert.False(NativeMethods.IsWindow(handle));
            Assert.Throws<ObjectDisposedException>(() => icon.Visible = true);
            Assert.Throws<ObjectDisposedException>(() => icon.Text = "disposed");
        });

    [Fact]
    public Task TrayRegistrationRecoversWhenTheTaskbarIsRecreated() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var previousWindows = ThreadWindows();
            var iconId = Guid.NewGuid();
            using var icon = new GuidNotifyIcon(iconId) { Icon = SystemIcons.Application, Visible = true };
            var handle = Assert.Single(ThreadWindows().Except(previousWindows));
            var data = GuidNotifyIcon.CreateData(handle, 0, string.Empty, iconId);
            var taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            Assert.NotEqual(0u, taskbarCreated);

            // Headless runners have no Explorer. They still exercise native message dispatch and cleanup.
            var hasTaskbar = NativeMethods.FindWindow("Shell_TrayWnd", null) != 0;
            output.WriteLine(hasTaskbar
                ? "Explorer is available; verifying registration, recovery, and removal with the real shell."
                : "Explorer is unavailable; verifying native recovery-message dispatch and window cleanup only.");
            await AssertRegisteredAsync(iconId, hasTaskbar, cancellationToken);
            if (hasTaskbar)
            {
                // Simulate Explorer losing this icon without affecting any other application.
                Assert.True(NativeMethods.ShellNotifyIcon(2, ref data));
                await AssertRegisteredAsync(iconId, false, cancellationToken);
            }

            _ = NativeMethods.SendMessage(handle, taskbarCreated, 0, 0);
            await AssertRegisteredAsync(iconId, hasTaskbar, cancellationToken);

            icon.Visible = false;
            await AssertRegisteredAsync(iconId, false, cancellationToken);
            icon.Visible = true;
            await AssertRegisteredAsync(iconId, hasTaskbar, cancellationToken);
            icon.Dispose();
            await AssertRegisteredAsync(iconId, false, cancellationToken);
            Assert.False(NativeMethods.IsWindow(handle));
        });

    private static async Task AssertRegisteredAsync(Guid iconId, bool expected, CancellationToken cancellationToken)
    {
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = checked((uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>()),
            Guid = iconId
        };
        var deadline = Environment.TickCount64 + 5_000;
        while ((NativeMethods.ShellNotifyIconGetRect(ref identifier, out _) == 0) != expected)
        {
            Assert.True(Environment.TickCount64 < deadline, $"Tray registration did not become {expected}.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static HashSet<nint> ThreadWindows()
    {
        var windows = new HashSet<nint>();
        Assert.True(NativeMethods.EnumThreadWindows(NativeMethods.GetCurrentThreadId(), (handle, _) =>
        {
            if (NativeWindow.FromHandle(handle) is not null)
            {
                windows.Add(handle);
            }
            return true;
        }, 0));
        return windows;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NotifyIconIdentifier
        {
            internal uint Size;
            internal nint Window;
            internal uint Id;
            internal Guid Guid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool EnumWindowsCallback(nint handle, nint parameter);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, nint parameter);

        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        internal static extern nint SendMessage(nint handle, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint handle);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect", ExactSpelling = true)]
        internal static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rectangle);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellNotifyIcon(uint message, ref GuidNotifyIcon.NotifyIconData data);

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern nint FindWindow(string className, string? windowName);
    }
}
