using System.Runtime.InteropServices;

namespace CodexUsageTray;

internal sealed class GuidNotifyIcon : IDisposable
{
    // Explorer stores the user's tray visibility choice against this value.
    internal static readonly Guid TrayIconId = new("f5d55d46-5431-46c5-86af-21c872a4fd06");

    private const int CallbackMessage = 0x0400 + 1024;
    private const int LeftButtonDown = 0x0201;
    private const int LeftButtonUp = 0x0202;
    private const int LeftButtonDoubleClick = 0x0203;
    private const int RightButtonUp = 0x0205;
    private const int NullMessage = 0x0000;
    private static readonly int TaskbarCreatedMessage = checked((int)NativeMethods.RegisterWindowMessage("TaskbarCreated"));
    private readonly CallbackWindow window;
    private Icon? icon;
    private string text = string.Empty;
    private bool visible;
    private bool added;
    private bool disposed;

    public GuidNotifyIcon()
    {
        window = new CallbackWindow(this);
    }

    public event MouseEventHandler? MouseDown;

    public event MouseEventHandler? MouseClick;

    public ContextMenuStrip? ContextMenuStrip { get; set; }

    public Icon? Icon
    {
        get => icon;
        set
        {
            ThrowIfDisposed();
            icon = value;
            Update();
        }
    }

    public string Text
    {
        get => text;
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, 127);
            text = value;
            Update();
        }
    }

    public bool Visible
    {
        get => visible;
        set
        {
            ThrowIfDisposed();
            visible = value;
            Update();
        }
    }

    public void ShowBalloonTip(int timeout, string title, string message, ToolTipIcon tipIcon)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(timeout);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (!added)
        {
            return;
        }

        var data = CreateData(window.Handle, icon?.Handle ?? 0, text);
        data.Flags = NotifyIconDataFlags.Info | NotifyIconDataFlags.Guid;
        data.Info = message;
        data.InfoTitle = title;
        data.TimeoutOrVersion = checked((uint)timeout);
        data.InfoFlags = checked((uint)tipIcon);
        _ = NativeMethods.ShellNotifyIcon(NotifyIconMessage.Modify, ref data);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        visible = false;
        Update();
        window.DestroyHandle();
        disposed = true;
    }

    internal static NotifyIconData CreateData(nint windowHandle, nint iconHandle, string tooltip) =>
        new()
        {
            Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
            WindowHandle = windowHandle,
            Flags = NotifyIconDataFlags.Message
                | NotifyIconDataFlags.Icon
                | NotifyIconDataFlags.Tooltip
                | NotifyIconDataFlags.Guid,
            CallbackMessage = CallbackMessage,
            IconHandle = iconHandle,
            Tooltip = tooltip,
            Guid = TrayIconId
        };

    private void Update()
    {
        if (visible && icon is not null)
        {
            EnsureWindowHandle();
            var data = CreateData(window.Handle, icon.Handle, text);
            var message = added ? NotifyIconMessage.Modify : NotifyIconMessage.Add;
            if (NativeMethods.ShellNotifyIcon(message, ref data))
            {
                added = true;
            }
        }
        else if (added)
        {
            var data = CreateData(window.Handle, 0, string.Empty);
            _ = NativeMethods.ShellNotifyIcon(NotifyIconMessage.Delete, ref data);
            added = false;
        }
    }

    private void EnsureWindowHandle()
    {
        if (window.Handle == 0)
        {
            window.CreateHandle(new CreateParams());
        }
    }

    private void ProcessWindowMessage(ref Message message)
    {
        if (message.Msg == TaskbarCreatedMessage)
        {
            added = false;
            Update();
            return;
        }

        if (message.Msg != CallbackMessage)
        {
            window.DefWndProc(ref message);
            return;
        }

        switch (unchecked((int)message.LParam))
        {
            case LeftButtonDown:
                MouseDown?.Invoke(this, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                break;
            case LeftButtonDoubleClick:
                MouseDown?.Invoke(this, new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0));
                break;
            case LeftButtonUp:
                MouseClick?.Invoke(this, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                break;
            case RightButtonUp:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        if (ContextMenuStrip is null)
        {
            return;
        }

        _ = NativeMethods.SetForegroundWindow(window.Handle);
        ContextMenuStrip.Show(Cursor.Position);
        _ = NativeMethods.PostMessage(window.Handle, NullMessage, 0, 0);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class CallbackWindow(GuidNotifyIcon owner) : NativeWindow
    {
        protected override void WndProc(ref Message message)
        {
            owner.ProcessWindowMessage(ref message);
        }

        public new void DefWndProc(ref Message message)
        {
            base.DefWndProc(ref message);
        }
    }

    internal enum NotifyIconDataFlags : uint
    {
        Message = 0x00000001,
        Icon = 0x00000002,
        Tooltip = 0x00000004,
        Info = 0x00000010,
        Guid = 0x00000020
    }

    private enum NotifyIconMessage : uint
    {
        Add = 0x00000000,
        Modify = 0x00000001,
        Delete = 0x00000002
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal uint Size;
        internal nint WindowHandle;
        internal uint Id;
        internal NotifyIconDataFlags Flags;
        internal uint CallbackMessage;
        internal nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Tooltip;

        internal uint State;
        internal uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Info;

        internal uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string InfoTitle;

        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIconHandle;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellNotifyIcon(NotifyIconMessage message, ref NotifyIconData data);

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint windowHandle);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);
    }
}
