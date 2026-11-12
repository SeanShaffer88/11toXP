using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = LunaDesktop.Shell.ShellNative.Rectangle;

namespace LunaDesktop.Shell;

// Explorer continues to own and handle input for its notification area.
internal sealed class TrayThumbnail : Border
{
    private readonly Window bar;
    private readonly DispatcherTimer timer;
    private nint thumbnail;
    private nint explorer;
    private Rectangle lastSource;
    private Rectangle lastDestination;

    internal TrayThumbnail(Window bar)
    {
        this.bar = bar;
        Width = 0;
        AutomationProperties.SetName(this, "Windows notification icons preview");
        AutomationProperties.SetAutomationId(this, "TrayThumbnail");
        IsHitTestVisible = false;
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        timer.Tick += (_, _) => Update();
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) =>
        {
            timer.Stop();
            ReleaseThumbnail();
        };
    }

    private void Update()
    {
        nint taskbar = FindWindowW("Shell_TrayWnd", null);
        nint tray = taskbar == 0 ? 0 : FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        if (!IsVisible || tray == 0 || !ShellNative.IsWindowVisible(tray) ||
            !ShellNative.GetWindowRect(taskbar, out var taskbarBounds) ||
            !ShellNative.GetWindowRect(tray, out var trayBounds) ||
            !ShellNative.GetWindowRect(new WindowInteropHelper(bar).Handle, out var barBounds))
        {
            HideThumbnail();
            return;
        }

        nint clock = FindWindowExW(tray, 0, "TrayClockWClass", null);
        int right = trayBounds.Right;
        if (clock != 0 && ShellNative.IsWindowVisible(clock) &&
            ShellNative.GetWindowRect(clock, out var clockBounds) &&
            clockBounds.Left > trayBounds.Left && clockBounds.Left < right)
            right = clockBounds.Left;

        var source = new Rectangle(
            Math.Max(trayBounds.Left, taskbarBounds.Left) - taskbarBounds.Left,
            Math.Max(trayBounds.Top, taskbarBounds.Top) - taskbarBounds.Top,
            Math.Min(right, taskbarBounds.Right) - taskbarBounds.Left,
            Math.Min(trayBounds.Bottom, taskbarBounds.Bottom) - taskbarBounds.Top);
        if (source.Width <= 0 || source.Height <= 0 || ActualHeight <= 0)
        {
            HideThumbnail();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double width = Math.Min(source.Width / dpi.DpiScaleX, bar.ActualWidth / 3);
        Width = width;
        if (ActualWidth <= 0)
            return;

        var topLeft = PointToScreen(new Point());
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        int height = Math.Min((int)Math.Round(bottomRight.Y - topLeft.Y), source.Height);
        int top = (int)Math.Round((topLeft.Y + bottomRight.Y - height) / 2) - barBounds.Top;
        var destination = new Rectangle((int)Math.Round(topLeft.X) - barBounds.Left, top,
            (int)Math.Round(bottomRight.X) - barBounds.Left, top + height);
        if (destination.Width <= 0 || destination.Height <= 0)
            return;

        if (thumbnail == 0 || explorer != taskbar)
        {
            ReleaseThumbnail();
            if (DwmRegisterThumbnail(new WindowInteropHelper(bar).Handle, taskbar, out thumbnail) < 0)
            {
                HideThumbnail();
                return;
            }
            explorer = taskbar;
        }
        if (source == lastSource && destination == lastDestination)
            return;

        var properties = new ThumbnailProperties
        {
            Flags = 0x01 | 0x02 | 0x04 | 0x08, // Destination, source, opacity and visibility.
            Destination = destination,
            Source = source,
            Opacity = byte.MaxValue,
            Visible = 1
        };
        if (DwmUpdateThumbnailProperties(thumbnail, ref properties) < 0)
        {
            HideThumbnail();
            return;
        }
        lastSource = source;
        lastDestination = destination;
    }

    private void HideThumbnail()
    {
        Width = 0;
        ReleaseThumbnail();
    }

    private void ReleaseThumbnail()
    {
        if (thumbnail != 0)
            DwmUnregisterThumbnail(thumbnail);
        thumbnail = 0;
        lastSource = default;
        lastDestination = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        internal uint Flags;
        internal Rectangle Destination;
        internal Rectangle Source;
        internal byte Opacity;
        internal int Visible;
        internal int SourceClientAreaOnly;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string className, string? windowName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);
}
