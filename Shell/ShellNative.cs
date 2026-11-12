using System.Runtime.InteropServices;
using System.Text;

namespace LunaDesktop.Shell;

internal static class ShellNative
{
    internal const uint AppBarNew = 0;
    internal const uint AppBarRemove = 1;
    internal const uint AppBarQueryPosition = 2;
    internal const uint AppBarSetPosition = 3;
    internal const uint AppBarActivate = 6;
    internal const uint AppBarWindowPositionChanged = 9;
    internal const uint BottomEdge = 3;

    internal const int ActivateMessage = 0x0006;
    internal const int SettingChangeMessage = 0x001A;
    internal const int TimeChangeMessage = 0x001E;
    internal const int WindowPositionChangedMessage = 0x0047;
    internal const int DisplayChangeMessage = 0x007E;
    internal const int DpiChangedMessage = 0x02E0;
    internal const int ThemeChangedMessage = 0x031A;

    internal const uint NoSize = 0x0001;
    internal const uint NoMove = 0x0002;
    internal const uint NoZOrder = 0x0004;
    internal const uint NoActivate = 0x0010;
    internal static readonly nint Topmost = -1;
    internal static readonly nint Bottom = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal record struct Rectangle(int Left, int Top, int Right, int Bottom)
    {
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        private uint size;
        internal nint Window;
        internal uint Callback;
        internal uint Edge;
        internal Rectangle Area;
        internal nint Parameter;

        internal static AppBarData For(nint window) => new()
        {
            size = (uint)Marshal.SizeOf<AppBarData>(),
            Window = window
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        private uint size;
        internal Rectangle Area;
        internal Rectangle WorkArea;
        internal uint Flags;

        internal static MonitorInfo Create() => new() { size = (uint)Marshal.SizeOf<MonitorInfo>() };
    }

    internal delegate bool EnumWindowsCallback(nint window, nint parameter);

    internal delegate void WinEventCallback(nint hook, uint eventId, nint window,
        int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("shell32.dll")]
    internal static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RegisterWindowMessageW(string name);

    // POINT consists of two adjacent signed 32-bit coordinates passed by value.
    // A zero value denotes (0, 0), which is always on the primary monitor.
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(long point, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint order, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(uint first, uint last, nint module,
        WinEventCallback callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    internal static extern nint GetLastActivePopup(nint window);

    // The style field is a 32-bit LONG on both supported architectures.
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetWindowLongW(nint window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int GetClassNameW(nint window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nuint GetClassLongPtrW(nint window, int index);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
}
