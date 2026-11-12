using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rectangle = LunaDesktop.Shell.ShellNative.Rectangle;

namespace LunaDesktop.Shell;

// Explorer continues to own and handle input for its notification area.
internal sealed class TrayThumbnail : Border
{
    private const uint CreateEvent = 0x8000;
    private const uint ReorderEvent = 0x8004;
    private const uint StateChangeEvent = 0x800A;
    private const uint ValueChangeEvent = 0x800E;
    private const int ExtendedStyle = -20;
    private const int TopmostStyle = 0x00000008;
    private const int DwmCompositionChangedMessage = 0x031E;
    private static readonly uint TaskbarCreatedMessage = ShellNative.RegisterWindowMessageW("TaskbarCreated");

    private readonly Window bar;
    private readonly Image image = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    private readonly ShellNative.WinEventCallback onNativeEvent;
    private readonly DispatcherTimer refreshTimer;
    private HwndSource? barSource;
    private CancellationTokenSource? lifetime;
    private DispatcherOperation? pending;
    private TrayIconCapture.Windows native;
    private TrayIconCapture.Source? currentSource;
    private TrayIconCapture.Frame? currentFrame;
    private nint lifecycleHook;
    private nint stateHook;
    private uint nativeProcess;
    private int sourceGeneration;
    private CancellationTokenSource? capturingSession;
    private bool refreshAgain;

    internal TrayThumbnail(Window bar)
    {
        this.bar = bar;
        onNativeEvent = OnNativeEvent;
        refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        refreshTimer.Tick += (_, _) => QueueUpdate();
        Child = image;
        Width = 0;
        AutomationProperties.SetName(this, "Mirrored Windows notification icons");
        AutomationProperties.SetAutomationId(this, "TrayThumbnail");
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                ClearSource();
            QueueUpdate();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (PresentationSource.FromVisual(bar) is not HwndSource source)
            return;
        barSource = source;
        lifetime = new CancellationTokenSource();
        source.AddHook(OnBarMessage);
        bar.SizeChanged += OnBarSizeChanged;
        bar.LocationChanged += OnBarLocationChanged;
        QueueUpdate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        bar.SizeChanged -= OnBarSizeChanged;
        bar.LocationChanged -= OnBarLocationChanged;
        if (barSource is { IsDisposed: false } source)
            source.RemoveHook(OnBarMessage);
        barSource = null;
        pending?.Abort();
        pending = null;
        var session = lifetime;
        lifetime = null;
        session?.Cancel();
        if (capturingSession != session)
            session?.Dispose();
        refreshAgain = false;
        WatchSource(default);
        ClearSource();
    }

    private void OnBarSizeChanged(object sender, SizeChangedEventArgs args) => QueueUpdate();
    private void OnBarLocationChanged(object? sender, EventArgs args) => QueueUpdate();

    private nint OnBarMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == TaskbarCreatedMessage || message == DwmCompositionChangedMessage)
        {
            ClearSource();
            QueueUpdate();
        }
        else if (message is ShellNative.DpiChangedMessage or ShellNative.SettingChangeMessage or
            ShellNative.WindowPositionChangedMessage)
        {
            QueueUpdate();
        }
        return 0;
    }

    private void QueueUpdate()
    {
        if (barSource is null || pending is not null)
            return;
        pending = Dispatcher.InvokeAsync(() =>
        {
            pending = null;
            if (barSource is { IsDisposed: false } source && lifetime is { } session)
                Update(source.Handle, session);
        }, DispatcherPriority.Background);
    }

    private void WatchSource(TrayIconCapture.Windows windows)
    {
        native = windows;
        uint process = 0;
        if (native.Taskbar != 0)
            ShellNative.GetWindowThreadProcessId(native.Taskbar, out process);
        if (process == nativeProcess)
            return;

        if (lifecycleHook != 0)
            ShellNative.UnhookWinEvent(lifecycleHook);
        if (stateHook != 0)
            ShellNative.UnhookWinEvent(stateHook);
        lifecycleHook = stateHook = 0;
        nativeProcess = process;
        if (process == 0)
            return;

        lifecycleHook = ShellNative.SetWinEventHook(CreateEvent, ReorderEvent, 0, onNativeEvent, process, 0, 0);
        stateHook = ShellNative.SetWinEventHook(StateChangeEvent, ValueChangeEvent, 0, onNativeEvent, process, 0, 0);
    }

    private void OnNativeEvent(nint hook, uint eventId, nint window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (native.Taskbar != 0 && window != 0 && objectId is 0 or -4 &&
            (window == native.Taskbar || window == native.Tray || window == native.Clock ||
                ShellNative.GetAncestor(window, 2) == native.Taskbar))
            QueueUpdate();
    }

    private async void Update(nint destinationWindow, CancellationTokenSource session)
    {
        var windows = TrayIconCapture.FindWindows();
        WatchSource(windows);
        if (!IsVisible || (ShellNative.GetWindowLongW(destinationWindow, ExtendedStyle) & TopmostStyle) == 0 ||
            TrayIconCapture.Describe(windows) is not { } source || !IsOnScreen(source.Client, destinationWindow))
        {
            ClearSource();
            return;
        }

        if (currentSource != source)
        {
            ClearSource();
            currentSource = source;
        }
        if (capturingSession is not null)
        {
            refreshAgain = true;
            return;
        }

        capturingSession = session;
        int generation = sourceGeneration;
        try
        {
            TrayIconCapture.Frame? frame = await TrayIconCapture.CaptureAsync(source, session.Token);
            if (lifetime == session && sourceGeneration == generation && IsVisible)
                ShowFrame(frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
        {
            if (lifetime == session && sourceGeneration == generation)
                ShowFrame(null);
        }
        finally
        {
            capturingSession = null;
            if (lifetime != session)
                session.Dispose();
            if (refreshAgain)
            {
                refreshAgain = false;
                QueueUpdate();
            }
        }
    }

    private static bool IsOnScreen(Rectangle bounds, nint window)
    {
        nint monitor = ShellNative.MonitorFromWindow(window, ShellNative.MonitorDefaultToNearest);
        var info = ShellNative.MonitorInfoEx.Create();
        return monitor != 0 && ShellNative.GetMonitorInfoW(monitor, ref info) &&
            bounds.Left >= info.Area.Left && bounds.Right <= info.Area.Right &&
            bounds.Top >= info.Area.Top && bounds.Bottom <= info.Area.Bottom;
    }

    private void ClearSource()
    {
        sourceGeneration++;
        currentSource = null;
        refreshTimer.Stop();
        ShowFrame(null);
    }

    private void ShowFrame(TrayIconCapture.Frame? frame)
    {
        if (frame is null)
        {
            refreshTimer.Stop();
            currentFrame = null;
            image.Source = null;
            Width = 0;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Min(frame.Width / dpi.DpiScaleX, bar.ActualWidth / 3);
        image.Height = frame.Height / dpi.DpiScaleY;
        // Some icon-only changes do not emit a WinEvent. Sample only while the mirror is visible.
        refreshTimer.Start();
        if (currentFrame is { } previous && previous.Width == frame.Width && previous.Height == frame.Height &&
            previous.Pixels.AsSpan().SequenceEqual(frame.Pixels))
            return;

        image.Source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null,
            frame.Pixels, frame.Width * sizeof(int));
        currentFrame = frame;
    }
}
