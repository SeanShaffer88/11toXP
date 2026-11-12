using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LunaDesktop.State;

internal sealed record MonitorWallpaper(string Monitor, string Original);
internal sealed record WallpaperPlan(string Requested, MonitorWallpaper[] Monitors, string[]? EarlierRequests = null)
{
    public WallpaperPlan Select(string image)
    {
        if (string.Equals(Requested, image, StringComparison.OrdinalIgnoreCase))
            return this;
        var earlier = (EarlierRequests ?? []).Append(Requested).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (earlier.Length > 32)
            throw new InvalidOperationException("Restore the saved picture before managing a different image.");
        return this with { Requested = image, EarlierRequests = earlier };
    }

    public bool OwnsImage(string image) => string.Equals(Requested, image, StringComparison.OrdinalIgnoreCase) ||
        (EarlierRequests?.Contains(image, StringComparer.OrdinalIgnoreCase) ?? false);
}

internal static class DesktopWallpaper
{
    private const string ModeError = "Choose Picture in Windows Personalization before changing the wallpaper here.";

    public static WallpaperPlan Capture(string requested)
    {
        ValidateImage(requested);
        return WithDesktop(desktop =>
        {
            EnsurePictureMode(desktop);
            var monitors = ReadMonitors(desktop);
            var original = monitors.Select(monitor => new MonitorWallpaper(monitor, ReadImage(desktop, monitor))).ToArray();
            foreach (var monitor in original)
                ValidateImage(monitor.Original);
            return new WallpaperPlan(requested, original);
        });
    }

    public static void Validate(WallpaperPlan plan)
    {
        if (!IsLocalImagePath(plan.Requested) ||
            plan.EarlierRequests is { } earlier && (earlier.Length > 32 || earlier.Any(path => !IsLocalImagePath(path))) ||
            plan.Monitors is not { Length: > 0 and <= 32 } ||
            plan.Monitors.Any(monitor => monitor is null || string.IsNullOrWhiteSpace(monitor.Monitor) ||
                monitor.Monitor.Length > 1024 || !IsLocalImagePath(monitor.Original)) ||
            plan.Monitors.Select(monitor => monitor.Monitor).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Monitors.Length)
            throw new InvalidDataException("The wallpaper restore record is invalid.");
    }

    public static void Apply(WallpaperPlan plan)
    {
        Validate(plan);
        ValidateImage(plan.Requested);
        WithDesktop(desktop =>
        {
            EnsurePictureMode(desktop);
            EnsureSameMonitors(desktop, plan);
            foreach (var monitor in plan.Monitors)
                SetImage(desktop, monitor.Monitor, plan.Requested);
            return true;
        });
    }

    public static void Restore(WallpaperPlan plan)
    {
        Validate(plan);
        WithDesktop(desktop =>
        {
            if (!IsPictureMode(desktop))
                return true;
            var connected = ReadMonitors(desktop).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var disconnected = false;
            foreach (var monitor in plan.Monitors)
            {
                if (!connected.Contains(monitor.Monitor))
                {
                    disconnected = true;
                    continue;
                }
                try
                {
                    var current = ReadImage(desktop, monitor.Monitor);
                    if (!plan.OwnsImage(current) || string.Equals(current, monitor.Original, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ValidateImage(monitor.Original);
                    SetImage(desktop, monitor.Monitor, monitor.Original);
                }
                catch (Exception error) when (error is COMException or InvalidOperationException or IOException)
                {
                    errors.Add(error.Message);
                }
            }
            if (disconnected)
                errors.Add("Reconnect the original displays to finish restoring their wallpapers.");
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Distinct()));
            return true;
        });
    }

    private static void EnsurePictureMode(IDesktopWallpaper desktop)
    {
        if (!IsPictureMode(desktop))
            throw new InvalidOperationException(ModeError);
    }

    private static bool IsPictureMode(IDesktopWallpaper desktop)
    {
        desktop.GetStatus(out uint status);
        if ((status & 4) != 0 || System.Windows.SystemParameters.HighContrast)
            throw new InvalidOperationException("Wallpaper is temporarily unavailable in this session or contrast theme.");
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
        var backgroundType = key?.GetValue("BackgroundType");
        return status == 1 && (backgroundType is null or 0);
    }

    private static string[] ReadMonitors(IDesktopWallpaper desktop)
    {
        desktop.GetMonitorDevicePathCount(out uint count);
        if (count is 0 or > 32)
            throw new InvalidOperationException("Windows did not return a supported set of monitors.");
        var monitors = new string[count];
        for (uint index = 0; index < count; index++)
        {
            desktop.GetMonitorDevicePathAt(index, out var monitor);
            var result = desktop.GetMonitorRECT(monitor, out _);
            Marshal.ThrowExceptionForHR(result);
            if (result == 0)
                monitors[index] = monitor;
        }
        var connected = monitors.Where(monitor => monitor is not null).ToArray();
        if (connected.Length == 0)
            throw new InvalidOperationException("Windows did not return any connected wallpaper displays.");
        return connected;
    }

    private static void EnsureSameMonitors(IDesktopWallpaper desktop, WallpaperPlan plan)
    {
        var connected = ReadMonitors(desktop).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!connected.SetEquals(plan.Monitors.Select(monitor => monitor.Monitor)))
            throw new InvalidOperationException("Reconnect the displays that were connected when the wallpaper was first applied.");
    }

    private static string ReadImage(IDesktopWallpaper desktop, string monitor)
    {
        desktop.GetWallpaper(monitor, out string image);
        return image ?? string.Empty;
    }

    private static void SetImage(IDesktopWallpaper desktop, string monitor, string path)
    {
        if (string.Equals(ReadImage(desktop, monitor), path, StringComparison.OrdinalIgnoreCase))
            return;
        desktop.SetWallpaper(monitor, path);
        if (!string.Equals(ReadImage(desktop, monitor), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows did not retain the selected wallpaper.");
    }

    private static bool IsLocalImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.AsSpan(2).Contains(':'))
            return false;
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateImage(string path)
    {
        if (!IsLocalImagePath(path) || !File.Exists(path))
            throw new InvalidOperationException("Both wallpaper images must be available as local JPG, PNG, or BMP files.");
        if (path.Contains("ContentDeliveryManager", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Client.CBS", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("DesktopSpotlight", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(ModeError);
    }

    private static T WithDesktop<T>(Func<IDesktopWallpaper, T> operation)
    {
        var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"), throwOnError: true)!;
        var desktop = (IDesktopWallpaper)Activator.CreateInstance(type)!;
        try { return operation(desktop); }
        finally { Marshal.FinalReleaseComObject(desktop); }
    }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
        void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);
        void GetMonitorDevicePathCount(out uint count);
        [PreserveSig]
        int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out DisplayRect displayRect);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(uint position);
        void GetPosition(out uint position);
        void SetSlideshow(nint items);
        void GetSlideshow(out nint items);
        void SetSlideshowOptions(uint options, uint tick);
        void GetSlideshowOptions(out uint options, out uint tick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorId, uint direction);
        void GetStatus(out uint state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayRect { public int Left, Top, Right, Bottom; }
}
