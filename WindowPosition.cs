using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using LunaDesktop.State;

namespace LunaDesktop;

internal static class WindowPosition
{
    public static void Restore(Window window, string directory)
    {
        try
        {
            var path = Path.Combine(directory, "window.json");
            StateFiles.Guard(directory, directory: true);
            StateFiles.Guard(path, directory: false);
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024)
                return;
            var saved = JsonSerializer.Deserialize<Bounds>(stream);
            if (saved is null || saved.Width is < 100 or > 10000 || saved.Height is < 100 or > 10000 ||
                saved.Left is < -100000 or > 100000 || saved.Top is < -100000 or > 100000)
                return;
            var candidate = new Rect
            {
                Left = saved.Left, Top = saved.Top, Right = saved.Left + saved.Width, Bottom = saved.Top + saved.Height
            };
            var monitor = MonitorFromRect(ref candidate, 2);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
                return;
            var work = info.Work;
            var width = Math.Min(saved.Width, work.Right - work.Left);
            var height = Math.Min(saved.Height, work.Bottom - work.Top);
            if (width <= 0 || height <= 0)
                return;
            var x = Math.Clamp(saved.Left, work.Left, work.Right - width);
            var y = Math.Clamp(saved.Top, work.Top, work.Bottom - height);
            SetWindowPos(new WindowInteropHelper(window).Handle, 0, x, y, width, height, 0x14);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
    }

    public static void Save(Window window, string directory)
    {
        if (window.WindowState != WindowState.Normal || !GetWindowRect(new WindowInteropHelper(window).Handle, out Rect rect))
            return;
        try
        {
            var bounds = new Bounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            StateFiles.WriteAtomically(directory, Path.Combine(directory, "window.json"),
                stream => JsonSerializer.Serialize(stream, bounds));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private sealed record Bounds(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect bounds);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromRect(ref Rect bounds, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
