using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LunaDesktop.Shell;

/// <summary>Owns the visible XP-style shell surfaces for one display.</summary>
public sealed class LunaShell : IDisposable
{
    private const double ResizeGripHeight = 7;
    private const int StateChangedNotification = 0;
    private const int PositionChangedNotification = 1;
    private const int FullScreenNotification = 2;
    private const int ArrangeNotification = 3;

    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Action openControl;
    private readonly Action? showWindowsTaskbar;
    private readonly Action? toggleTrayReveal;
    private readonly Func<bool>? isTrayRevealed;
    private readonly Dictionary<TaskbarKey, TaskButton> taskButtons = [];
    private readonly LunaVariant variant;
    private readonly TaskbarOptions options;
    private readonly string? displayId;
    private readonly Action<TaskbarButtonSize>? requestResize;
    private readonly Action<string?>? openXpExplorer;
    private readonly bool showTrayThumbnail;
    private readonly DispatcherTimer clockTimer;
    private readonly uint callbackMessage;
    private readonly uint taskbarCreatedMessage;
    private Window? bar;
    private StartMenu? menu;
    private HwndSource? source;
    private WindowTasks? tasks;
    private LunaVisuals visuals;
    private ContextMenu? openContextMenu;
    private StackPanel? taskPanel;
    private ScrollViewer? taskScroller;
    private StackPanel? quickLaunch;
    private int inlineQuickLaunchLimit;
    private Button? overflow;
    private Button? clock;
    private Button? trayRevealButton;
    private Button? start;
    private ShellNative.Rectangle? reservation;
    private ShellDisplay? display;
    private bool registered;
    private bool positionQueued;
    private bool restartQueued;
    private bool positioning;
    private bool fullScreen;
    private nint fullScreenWindow;
    private bool fullScreenFromBounds;
    private bool arranging;
    private bool disposed;

    public LunaShell(Action openControl, Action? showWindowsTaskbar = null)
        : this(openControl, showWindowsTaskbar, LunaVariant.Blue, TaskbarOptions.Default, null)
    {
    }

    internal LunaShell(Action openControl, Action? showWindowsTaskbar, LunaVariant variant,
        TaskbarOptions options, Action<string?>? openXpExplorer, Action? toggleTrayReveal = null,
        Func<bool>? isTrayRevealed = null, string? displayId = null, Action<TaskbarButtonSize>? requestResize = null,
        bool showTrayThumbnail = false)
    {
        this.openControl = openControl ?? throw new ArgumentNullException(nameof(openControl));
        this.showWindowsTaskbar = showWindowsTaskbar;
        this.toggleTrayReveal = toggleTrayReveal;
        this.isTrayRevealed = isTrayRevealed;
        this.variant = variant;
        this.options = options.CheckedCopy();
        this.openXpExplorer = openXpExplorer;
        this.displayId = displayId;
        this.requestResize = requestResize;
        this.showTrayThumbnail = showTrayThumbnail;
        visuals = new LunaVisuals(variant);
        callbackMessage = ShellNative.RegisterWindowMessageW("11toXP.AppBar.7A7229A8");
        taskbarCreatedMessage = ShellNative.RegisterWindowMessageW("TaskbarCreated");
        if (callbackMessage == 0 || taskbarCreatedMessage == 0)
            throw new InvalidOperationException("Windows could not register the taskbar notification messages.");
        clockTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        clockTimer.Tick += ClockTick;
    }

    public event Action? ExplorerRestarted;
    public event Action<Exception>? ShellFaulted;

    private bool SmallButtons => options.ButtonSize == TaskbarButtonSize.Small;
    private bool CanResize => !options.IsLocked && requestResize is not null;
    private double BarHeight => (SmallButtons ? 30 : 38) + (CanResize ? ResizeGripHeight : 0);
    private double MinimumTaskWidth => options.ShowLabels ? SmallButtons ? 68 : 86 : SmallButtons ? 32 : 38;
    private double MaximumTaskWidth => options.ShowLabels ? SmallButtons ? 135 : 163 : MinimumTaskWidth;

    internal bool CanFocusStart => registered && !fullScreen && !arranging &&
        bar is { IsVisible: true } && start is { IsVisible: true, IsEnabled: true };

    internal bool TryFocusStart()
    {
        dispatcher.VerifyAccess();
        if (!CanFocusStart || bar is not { } window || start is not { } button)
            return false;
        if (!window.IsActive && !window.Activate())
            return false;
        return button.Focus();
    }

    public void Start()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (bar is not null)
        {
            Repair();
            return;
        }

        try
        {
            bar = new Window
            {
                Title = "11toXP Taskbar",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Width = 600,
                Height = BarHeight,
                Left = 0,
                Top = 0,
                Opacity = 0,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            bar.Closing += OnBarClosing;
            bar.SizeChanged += OnBarSizeChanged;
            bar.PreviewKeyDown += OnBarPreviewKeyDown;
            BuildBar();
            nint handle = new WindowInteropHelper(bar).EnsureHandle();
            source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException("Windows could not create the 11toXP taskbar window.");
            source.AddHook(OnWindowMessage);
            RegisterBar();
            LayoutBar();
            if (reservation is not null)
            {
                bar.Show();
                LayoutBar();
            }
            bar.Opacity = 1;

            tasks = new WindowTasks(handle, () => menu is null ? 0 : new WindowInteropHelper(menu).Handle, dispatcher,
                options.Grouping == TaskGrouping.Always) { FullScreenWindow = fullScreenWindow };
            tasks.Changed += UpdateTasks;
            tasks.Start();
            UpdateClock();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Repair()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (bar is null)
        {
            Start();
            return;
        }

        try
        {
            if (!registered)
                RegisterBar();
            LayoutBar();
            if (!bar.IsVisible && !arranging && reservation is not null)
                bar.Show();
            RefreshPalette();
            UpdateTrayRevealButton();
            tasks?.Refresh();
            UpdateClock();
        }
        catch
        {
            RemoveReservation();
            bar.Hide();
            throw;
        }
    }

    private void RegisterBar()
    {
        nint handle = source?.Handle ?? 0;
        if (handle == 0)
            throw new InvalidOperationException("The 11toXP taskbar window is unavailable.");

        var data = ShellNative.AppBarData.For(handle);
        data.Callback = callbackMessage;
        if (ShellNative.SHAppBarMessage(ShellNative.AppBarNew, ref data) == 0)
            throw new InvalidOperationException("Windows did not register the 11toXP taskbar.");
        registered = true;
        reservation = null;
    }

    private void RemoveReservation()
    {
        if (!registered)
            return;
        registered = false;
        reservation = null;
        if (source is not null)
        {
            var data = ShellNative.AppBarData.For(source.Handle);
            ShellNative.SHAppBarMessage(ShellNative.AppBarRemove, ref data);
        }
    }

    private void LayoutBar()
    {
        if (!registered || source is null || positioning)
            return;

        positioning = true;
        try
        {
            nint handle = source.Handle;
            var monitor = GetTargetDisplay();
            if (monitor is null)
            {
                RemoveReservation();
                CloseMenu();
                bar?.Hide();
                return;
            }
            if (ShellNative.MonitorFromWindow(handle, ShellNative.MonitorDefaultToNearest) != monitor.Handle)
            {
                // Move the HWND onto the target before reading its per-monitor DPI.
                MoveWindow(handle, new ShellNative.Rectangle(monitor.Bounds.Left, monitor.Bounds.Top,
                    monitor.Bounds.Left + 1, monitor.Bounds.Top + 1));
                reservation = null;
            }
            int height = (int)Math.Round(BarHeight * DpiScale(handle));
            var data = ShellNative.AppBarData.For(handle);
            data.Edge = ShellNative.BottomEdge;
            data.Area = monitor.Bounds;
            data.Area.Top = data.Area.Bottom - height;
            ShellNative.SHAppBarMessage(ShellNative.AppBarQueryPosition, ref data);
            data.Area.Top = data.Area.Bottom - height;
            if (data.Area != reservation)
            {
                ShellNative.SHAppBarMessage(ShellNative.AppBarSetPosition, ref data);
                if (data.Area.Width <= 0 || data.Area.Height <= 0
                    || data.Area.Left < monitor.Bounds.Left || data.Area.Right > monitor.Bounds.Right
                    || data.Area.Top < monitor.Bounds.Top || data.Area.Bottom > monitor.Bounds.Bottom)
                    throw new InvalidOperationException("Windows did not return a usable taskbar area on the selected display.");
                reservation = data.Area;
            }

            ShellNative.Rectangle area = reservation.Value;
            if (ShellNative.GetWindowRect(handle, out ShellNative.Rectangle actual) && actual == area)
                return;
            MoveWindow(handle, area);
            ShellNative.SHAppBarMessage(ShellNative.AppBarWindowPositionChanged, ref data);
        }
        finally
        {
            positioning = false;
        }
    }

    private ShellDisplay? GetTargetDisplay()
    {
        if (display?.Refresh() is { } cached && (displayId is not null || cached.IsPrimary))
            return display = cached;
        var displays = ShellDisplay.Enumerate();
        return display = displayId is null
            ? displays.FirstOrDefault(candidate => candidate.IsPrimary)
            : displays.FirstOrDefault(candidate => string.Equals(candidate.Id, displayId, StringComparison.OrdinalIgnoreCase));
    }

    private static void MoveWindow(nint handle, ShellNative.Rectangle area)
    {
        if (!ShellNative.SetWindowPos(handle, 0, area.Left, area.Top, area.Width, area.Height,
                ShellNative.NoZOrder | ShellNative.NoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The 11toXP shell window could not be positioned.");
    }

    private static double DpiScale(nint window)
    {
        uint dpi = ShellNative.GetDpiForWindow(window);
        return dpi == 0 ? 1 : dpi / 96d;
    }

    private nint OnWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed)
            return 0;
        if ((uint)message == taskbarCreatedMessage)
            QueueLayout(restart: true);
        else if ((uint)message == callbackMessage)
            OnAppBarNotification(window, (int)wParam, lParam != 0);
        else if (message == ShellNative.DisplayChangeMessage)
        {
            display = null;
            QueueLayout();
        }
        else if (message == ShellNative.DpiChangedMessage)
            QueueLayout();
        else if (message == ShellNative.TimeChangeMessage)
            dispatcher.BeginInvoke(UpdateClock);
        else if (message is ShellNative.ThemeChangedMessage or ShellNative.SettingChangeMessage)
            dispatcher.BeginInvoke(RefreshPalette);
        else if (registered && (message is ShellNative.ActivateMessage or ShellNative.WindowPositionChangedMessage))
        {
            if (message == ShellNative.ActivateMessage)
            {
                var data = ShellNative.AppBarData.For(window);
                data.Parameter = (wParam.ToInt64() & 0xFFFF) == 0 ? 0 : 1;
                ShellNative.SHAppBarMessage(ShellNative.AppBarActivate, ref data);
            }
            else if (!positioning && (!ShellNative.GetWindowRect(window, out ShellNative.Rectangle actual)
                                      || actual != reservation))
            {
                var data = ShellNative.AppBarData.For(window);
                ShellNative.SHAppBarMessage(ShellNative.AppBarWindowPositionChanged, ref data);
                QueueLayout();
            }
        }
        return 0;
    }

    private void OnAppBarNotification(nint window, int notification, bool active)
    {
        switch (notification)
        {
            case StateChangedNotification:
            case PositionChangedNotification:
                if (!positioning)
                    QueueLayout();
                break;
            case FullScreenNotification:
                nint foreground = active ? ShellNative.GetForegroundWindow() : 0;
                bool onThisDisplay = active && (foreground == 0 || display is null ||
                    ShellNative.MonitorFromWindow(foreground, ShellNative.MonitorDefaultToNearest) == display.Handle);
                SetFullScreen(window, onThisDisplay, foreground, fullScreenFromBounds && foreground == fullScreenWindow);
                break;
            case ArrangeNotification:
                arranging = active;
                if (active)
                    CloseMenu();
                dispatcher.BeginInvoke(() =>
                {
                    if (bar is null || !registered)
                        return;
                    if (arranging)
                        bar.Hide();
                    else if (reservation is not null)
                        bar.Show();
                });
                break;
        }
    }

    private void SetFullScreen(nint barHandle, bool active, nint foreground, bool fromBounds = false)
    {
        fullScreen = active;
        TrackFullScreenWindow(active ? foreground : 0, active && fromBounds);
        if (active)
            CloseMenu();
        ShellNative.SetWindowPos(barHandle, active ? ShellNative.Bottom : ShellNative.Topmost,
            0, 0, 0, 0, ShellNative.NoMove | ShellNative.NoSize | ShellNative.NoActivate);
    }

    private void TrackFullScreenWindow(nint window, bool fromBounds)
    {
        fullScreenWindow = window;
        fullScreenFromBounds = fromBounds;
        if (tasks is not null)
            tasks.FullScreenWindow = window;
    }

    private void ReconcileFullScreenForeground(IReadOnlyList<TaskWindow> windows)
    {
        if (source is null || display is null)
            return;
        nint foreground = ShellNative.GetForegroundWindow();
        bool visible = foreground != 0 && ShellNative.IsWindowVisible(foreground) && !ShellNative.IsIconic(foreground);
        bool isTask = visible && windows.Any(task => task.Handle == foreground);
        if (!fullScreen)
        {
            // Some fullscreen apps miss the Explorer notification. Limit detection to actual task
            // windows so the desktop and shell surfaces cannot accidentally lower the bar.
            if (isTask && CoversDisplay(foreground, display.Bounds))
                SetFullScreen(source.Handle, true, foreground, fromBounds: true);
            return;
        }
        if (visible && foreground == fullScreenWindow &&
            (fullScreenFromBounds ? CoversDisplay(foreground, display.Bounds) :
                ShellNative.MonitorFromWindow(foreground, ShellNative.MonitorDefaultToNearest) == display.Handle))
            return;

        // Explorer can omit the fullscreen-exit notification when the application closes.
        // A replacement fullscreen foreground on this display should still keep the bar lowered.
        if (isTask && CoversDisplay(foreground, display.Bounds))
            TrackFullScreenWindow(foreground, fromBounds: true);
        else
            SetFullScreen(source.Handle, false, 0);
    }

    private static bool CoversDisplay(nint window, ShellNative.Rectangle displayBounds)
    {
        if (ShellNative.DwmGetWindowAttribute(window, ShellNative.DwmExtendedFrameBounds,
                out ShellNative.Rectangle bounds, (uint)Marshal.SizeOf<ShellNative.Rectangle>()) != 0 &&
            !ShellNative.GetWindowRect(window, out bounds))
            return false;
        return CoversDisplay(bounds, displayBounds);
    }

    internal static bool CoversDisplay(ShellNative.Rectangle bounds, ShellNative.Rectangle displayBounds)
    {
        const int edgeTolerance = 2;
        return bounds.Width > 0 && bounds.Height > 0 &&
            bounds.Left <= displayBounds.Left + edgeTolerance && bounds.Top <= displayBounds.Top + edgeTolerance &&
            bounds.Right >= displayBounds.Right - edgeTolerance && bounds.Bottom >= displayBounds.Bottom - edgeTolerance;
    }

    private void QueueLayout(bool restart = false)
    {
        restartQueued |= restart;
        if (positionQueued)
            return;
        positionQueued = true;
        dispatcher.InvokeAsync(ApplyQueuedLayout, DispatcherPriority.Background);
    }

    private void ApplyQueuedLayout()
    {
        positionQueued = false;
        bool restart = restartQueued;
        restartQueued = false;
        if (disposed || bar is null)
            return;
        try
        {
            if (restart)
            {
                RemoveReservation();
                tasks?.Refresh();
            }
            if (!registered && GetTargetDisplay() is not null)
                RegisterBar();
            ShellNative.Rectangle? previous = reservation;
            LayoutBar();
            if (!arranging && !bar.IsVisible && reservation is not null)
                bar.Show();
            if (restart || previous != reservation)
                CloseMenu();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            RemoveReservation();
            CloseMenu();
            bar.Hide();
            if (!restart)
                ShellFaulted?.Invoke(ex);
        }
        if (restart)
            ExplorerRestarted?.Invoke();
    }

    private void BuildBar()
    {
        if (bar is null)
            return;

        taskButtons.Clear();
        bar.Background = visuals.Bar;
        var layout = new Grid { Background = visuals.Bar, ContextMenu = CreateBarContextMenu() };
        if (CanResize)
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ResizeGripHeight) });
        layout.RowDefinitions.Add(new RowDefinition());
        foreach (GridLength width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star),
                                            GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var startLabel = new StackPanel { Orientation = Orientation.Horizontal };
        startLabel.Children.Add(LunaVisuals.AppGlyph(visuals.HighContrast));
        startLabel.Children.Add(new TextBlock
        {
            Text = "Start",
            FontSize = 19,
            FontStyle = FontStyles.Italic,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        start = visuals.NewButton("Start", startLabel, visuals.StartButtonStyle, visuals.Start, visuals.TaskEdge);
        start.MinWidth = 94;
        start.Click += (_, _) => OpenMenu();
        AddColumn(layout, start, 0);

        quickLaunch = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 4, 2) };
        inlineQuickLaunchLimit = -1;
        UpdateQuickLaunch(bar.ActualWidth);
        AddColumn(layout, quickLaunch, 1);

        taskPanel = new StackPanel { Orientation = Orientation.Horizontal };
        taskScroller = new ScrollViewer
        {
            Content = taskPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(4, 3, 1, 3),
            Focusable = false
        };
        taskScroller.ScrollChanged += (_, args) =>
        {
            if (args.ViewportWidthChange != 0)
                ResizeTaskButtons();
            UpdateOverflow();
        };
        taskScroller.PreviewMouseWheel += (_, args) =>
        {
            if (taskScroller.ScrollableWidth == 0)
                return;
            taskScroller.ScrollToHorizontalOffset(taskScroller.HorizontalOffset - Math.Sign(args.Delta) * 96);
            args.Handled = true;
        };
        AddColumn(layout, taskScroller, 2);

        overflow = visuals.NewButton("More Open Windows", "›", visuals.TaskButtonStyle, Brushes.Transparent);
        overflow.Width = 27;
        overflow.FontSize = 19;
        overflow.Visibility = Visibility.Collapsed;
        overflow.ToolTip = "More Open Windows";
        overflow.Click += (_, _) => AdvanceTasks();
        AddColumn(layout, overflow, 3);

        if (toggleTrayReveal is not null)
        {
            trayRevealButton = visuals.NewButton("", "", visuals.TaskButtonStyle, visuals.Tray);
            trayRevealButton.Margin = new Thickness(2, 0, 0, 0);
            AutomationProperties.SetAutomationId(trayRevealButton, "TrayRevealButton");
            trayRevealButton.Click += (_, _) =>
            {
                RunFromBar(toggleTrayReveal);
                UpdateTrayRevealButton();
            };
            UpdateTrayRevealButton();
            AddColumn(layout, trayRevealButton, 4);
        }

        if (showTrayThumbnail)
            AddColumn(layout, new TrayThumbnail(bar), 5);

        clock = visuals.NewButton("Date and Time", "", visuals.TaskButtonStyle, visuals.Tray);
        clock.MinWidth = 88;
        clock.Margin = new Thickness(2, 0, 0, 0);
        clock.Click += (_, _) => RunFromBar(() => StartMenu.LaunchFile("ms-settings:dateandtime"));
        AddColumn(layout, clock, 6);

        if (CanResize)
            AddResizeGrip(layout);
        bar.Content = layout;
    }

    private void AddResizeGrip(Grid layout)
    {
        var grip = new Thumb
        {
            Focusable = true,
            Cursor = Cursors.SizeNS,
            Background = visuals.Tray,
            ToolTip = "Resize XP taskbar. Drag up or down, or use the Up and Down arrow keys."
        };
        AutomationProperties.SetAutomationId(grip, "TaskbarResizeGrip");
        AutomationProperties.SetName(grip, "Resize XP Taskbar");
        AutomationProperties.SetHelpText(grip, "Press Up for large buttons or Down for small buttons.");
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        line.SetValue(Border.BorderBrushProperty, visuals.TaskEdge);
        line.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = line };
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.HighlightBrush));
        template.Triggers.Add(focused);
        grip.Template = template;
        grip.DragCompleted += (_, args) =>
        {
            if (!args.Canceled && Math.Abs(args.VerticalChange) >= 5)
                RequestResize(args.VerticalChange < 0 ? TaskbarButtonSize.Large : TaskbarButtonSize.Small);
        };
        grip.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Up or Key.Down))
                return;
            args.Handled = true;
            RequestResize(args.Key == Key.Up ? TaskbarButtonSize.Large : TaskbarButtonSize.Small);
        };
        Grid.SetColumnSpan(grip, layout.ColumnDefinitions.Count);
        layout.Children.Add(grip);
    }

    private void RequestResize(TaskbarButtonSize size)
    {
        if (size != options.ButtonSize && requestResize is not null)
            RunFromBar(() => requestResize(size));
    }

    private void UpdateTrayRevealButton()
    {
        if (trayRevealButton is not { } button)
            return;
        var label = isTrayRevealed?.Invoke() == true ? "Hide Windows Tray" : "Show Windows Tray";
        button.Content = label;
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
    }

    private static void AddColumn(Grid grid, UIElement child, int column)
    {
        Grid.SetColumn(child, column);
        Grid.SetRow(child, grid.RowDefinitions.Count - 1);
        grid.Children.Add(child);
    }

    private void UpdateQuickLaunch(double width)
    {
        int inlineLimit = width >= 450 ? 3 : 0;
        if (quickLaunch is null || inlineLimit == inlineQuickLaunchLimit)
            return;

        inlineQuickLaunchLimit = inlineLimit;
        var commands = options.EffectiveQuickLaunch
            .Where(entry => entry.BuiltIn != QuickLaunchItem.XpExplorer || openXpExplorer is not null)
            .Select(QuickLaunchCommand).ToArray();
        quickLaunch.Children.Clear();
        quickLaunch.Visibility = commands.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (label, geometry, color, action) in commands.Take(inlineLimit))
            quickLaunch.Children.Add(CreateQuickLaunchButton(label, geometry, color, () => RunFromBar(action)));
        if (commands.Length <= inlineLimit)
            return;

        var context = CreateContextMenu();
        AutomationProperties.SetName(context, "Quick Launch");
        foreach (var command in commands.Skip(inlineLimit))
        {
            var item = MenuCommand(command.Label.Replace("_", "__", StringComparison.Ordinal),
                () => RunFromBar(command.Action));
            AutomationProperties.SetName(item, command.Label);
            context.Items.Add(item);
        }
        Button more = CreateQuickLaunchButton("More Quick Launch",
            "M 2,4 L 7,10 2,16 5,16 10,10 5,4 Z M 11,4 L 16,10 11,16 14,16 19,10 14,4 Z",
            0xE8F4FF, () => context.IsOpen = true);
        context.PlacementTarget = more;
        context.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        quickLaunch.Children.Add(more);
    }

    private (string Label, string Geometry, uint Color, Action Action) QuickLaunchCommand(QuickLaunchEntry entry) =>
        entry.BuiltIn switch
        {
            QuickLaunchItem.FileExplorer => ("Windows File Explorer", "M 1,5 L 8,5 10,8 19,8 19,19 1,19 Z",
                0xFFD46D, () => StartMenu.LaunchExplorer()),
            QuickLaunchItem.Settings => (entry.Label,
                "M 2,3 L 18,3 18,19 2,19 Z M 6,7 L 6,15 9,15 9,7 Z M 12,7 L 12,15 15,15 15,7 Z",
                0x9FD6F1, () => StartMenu.LaunchFile("ms-settings:")),
            QuickLaunchItem.XpExplorer => (entry.Label,
                "M 1,5 L 8,5 10,8 19,8 19,19 1,19 Z M 4,11 L 16,11 16,15 4,15 Z",
                0xF4BD68, () => openXpExplorer!(null)),
            _ => (entry.Label, "M 3,1 L 13,1 17,5 17,19 3,19 Z M 6,13 L 11,8 11,12 14,12 14,6 8,6 8,9 4,13 Z",
                0xE8F4FF, () => StartMenu.LaunchShortcut(entry))
        };

    private Button CreateQuickLaunchButton(string label, string geometry, uint color, Action click)
    {
        Brush fill = visuals.HighContrast ? SystemColors.ControlTextBrush : LunaVisuals.Solid(color);
        Button button = visuals.NewButton(label, LunaVisuals.ShortcutGlyph(geometry, fill, visuals.TaskEdge),
            visuals.TaskButtonStyle, Brushes.Transparent);
        button.ToolTip = label;
        button.Width = 31;
        button.Click += (_, _) => click();
        return button;
    }

    private ContextMenu CreateBarContextMenu()
    {
        ContextMenu context = CreateContextMenu();
        AutomationProperties.SetName(context, "Taskbar Menu");
        context.Items.Add(MenuCommand("Taskbar Settings…", () => RunFromBar(openControl)));
        if (openXpExplorer is not null)
            context.Items.Add(MenuCommand("Open XP Explorer", () => RunFromBar(() => openXpExplorer(null))));
        if (showWindowsTaskbar is not null)
        {
            context.Items.Add(new Separator());
            context.Items.Add(MenuCommand("Show Windows Taskbar", () => RunFromBar(showWindowsTaskbar)));
        }
        return context;
    }

    private ContextMenu CreateContextMenu()
    {
        var context = new ContextMenu
        {
            FontFamily = new FontFamily("Tahoma"),
            FontSize = 13,
            Background = visuals.HighContrast ? SystemColors.MenuBrush : LunaVisuals.Solid(0xF5F3EA),
            Foreground = visuals.HighContrast ? SystemColors.MenuTextBrush : visuals.MenuText,
            BorderBrush = visuals.MenuEdge,
            BorderThickness = new Thickness(1)
        };
        context.Opened += (_, _) =>
        {
            if (openContextMenu is { } previous && previous != context)
                previous.IsOpen = false;
            openContextMenu = context;
        };
        context.Closed += (_, _) =>
        {
            if (openContextMenu == context)
                openContextMenu = null;
        };
        return context;
    }

    private static MenuItem MenuCommand(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }

    private void FillTaskMenu(ContextMenu context, TaskbarEntry entry, bool includeActions)
    {
        context.Items.Clear();
        AutomationProperties.SetName(context, entry.IsGroup ? "Open Windows" : "Window Menu");
        foreach (TaskWindow window in entry.Windows)
        {
            if (!includeActions)
            {
                context.Items.Add(MenuCommand(window.Title, () => CompleteWindowAction(WindowTasks.Activate(window))));
                continue;
            }

            var target = entry.IsGroup ? new MenuItem { Header = window.Title } : null;
            if (target is not null)
                AutomationProperties.SetAutomationId(target, "TaskWindowGroupMember");
            ItemCollection commands = target?.Items ?? context.Items;
            var switchTo = MenuCommand("Switch To", () => CompleteWindowAction(WindowTasks.Activate(window)));
            AutomationProperties.SetAutomationId(switchTo, "TaskWindowSwitchTo");
            commands.Add(switchTo);
            commands.Add(WindowCommand(window, TaskWindowCommand.Restore));
            commands.Add(WindowCommand(window, TaskWindowCommand.Minimize));
            commands.Add(WindowCommand(window, TaskWindowCommand.Maximize));
            commands.Add(new Separator());
            commands.Add(WindowCommand(window, TaskWindowCommand.Close));
            if (target is not null)
                context.Items.Add(target);
        }
    }

    private MenuItem WindowCommand(TaskWindow window, TaskWindowCommand command)
    {
        var item = MenuCommand(command.ToString(), () => CompleteWindowAction(WindowTasks.Execute(window, command)));
        item.IsEnabled = WindowTasks.CanExecute(window, command);
        AutomationProperties.SetAutomationId(item, $"TaskWindow{command}");
        return item;
    }

    private void CompleteWindowAction(bool succeeded)
    {
        if (succeeded)
            return;
        System.Media.SystemSounds.Beep.Play();
        tasks?.Refresh();
    }

    private void UpdateTasks(IReadOnlyList<TaskWindow> windows, nint active)
    {
        ReconcileFullScreenForeground(windows);
        if (taskPanel is null)
            return;
        TaskbarEntry[] entries = TaskbarEntry.Build(windows, options.Grouping);
        HashSet<TaskbarKey> desired = entries.Select(entry => entry.Key).ToHashSet();
        foreach (TaskbarKey old in taskButtons.Keys.Where(key => !desired.Contains(key)).ToArray())
        {
            if (taskButtons[old].Button.ContextMenu is { IsOpen: true } menu)
                menu.IsOpen = false;
            taskPanel.Children.Remove(taskButtons[old].Button);
            taskButtons.Remove(old);
        }
        foreach (TaskbarEntry entry in entries)
        {
            if (!taskButtons.TryGetValue(entry.Key, out TaskButton? current))
            {
                current = CreateTaskButton(entry);
                taskButtons[entry.Key] = current;
                taskPanel.Children.Add(current.Button);
            }
            current.Entry = entry;
            current.Icon.Source = entry.Windows[0].Icon;
            current.Icon.Visibility = current.Icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
            current.FallbackIcon.Visibility = current.Icon.Source is null ? Visibility.Visible : Visibility.Collapsed;
            current.Arrow.Visibility = entry.IsGroup ? Visibility.Visible : Visibility.Collapsed;
            string label = entry.IsGroup ? $"{entry.Label} ({entry.Windows.Length})" : entry.Label;
            if (current.Title.Text != label)
            {
                current.Title.Text = label;
                current.Button.ToolTip = label;
                AutomationProperties.SetName(current.Button, label);
            }
            bool isActive = entry.Windows.Any(task => task.Handle == active);
            current.Button.Background = isActive ? visuals.ActiveTask : visuals.Task;
            current.Button.Style = isActive ? visuals.ActiveTaskButtonStyle : visuals.TaskButtonStyle;
        }
        ResizeTaskButtons();
    }

    private TaskButton CreateTaskButton(TaskbarEntry entry)
    {
        var content = new DockPanel { LastChildFill = true };
        double iconSize = SmallButtons ? 14 : 16;
        var icon = new Image { Width = iconSize, Height = iconSize };
        UIElement fallback = LunaVisuals.ShortcutGlyph("M 1,2 L 19,2 19,17 1,17 Z M 3,6 L 3,15 17,15 17,6 Z",
            visuals.HighContrast ? SystemColors.ControlTextBrush : visuals.BarText);
        var glyph = new Grid();
        glyph.Children.Add(icon);
        glyph.Children.Add(fallback);
        var iconCell = new Border
        {
            Width = iconSize + 1,
            Margin = new Thickness(0, 0, options.ShowLabels ? 5 : 0, 0),
            Child = glyph
        };
        content.Children.Add(iconCell);
        var arrow = new TextBlock { Text = "▾", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        DockPanel.SetDock(arrow, Dock.Right);
        content.Children.Add(arrow);
        var title = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = options.ShowLabels ? Visibility.Visible : Visibility.Collapsed
        };
        content.Children.Add(title);

        Button button = visuals.NewButton(entry.Label, content, visuals.TaskButtonStyle, visuals.Task, visuals.TaskEdge);
        button.HorizontalContentAlignment = options.ShowLabels ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        button.Width = MaximumTaskWidth;
        button.Height = SmallButtons ? 22 : 29;
        if (!options.ShowLabels)
            button.Padding = new Thickness(2, 1, 2, 1);
        else if (SmallButtons)
            button.Padding = new Thickness(3, 1, 3, 1);
        button.Margin = new Thickness(0, 0, 2, 0);
        var item = new TaskButton(button, title, arrow, icon, fallback, entry);
        ContextMenu actions = CreateContextMenu();
        button.ContextMenu = actions;
        button.ContextMenuOpening += (_, _) => FillTaskMenu(actions, item.Entry, includeActions: true);
        button.Click += (_, _) =>
        {
            if (item.Entry.IsGroup)
            {
                ContextMenu choices = CreateContextMenu();
                choices.PlacementTarget = button;
                choices.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                FillTaskMenu(choices, item.Entry, includeActions: false);
                choices.IsOpen = true;
            }
            else if (tasks is not null)
                CompleteWindowAction(tasks.SwitchTo(item.Entry.Windows[0]));
        };
        return item;
    }

    private void ResizeTaskButtons()
    {
        if (taskScroller is null || taskButtons.Count == 0 || taskScroller.ViewportWidth <= 0)
            return;
        double width = Math.Clamp(Math.Floor(taskScroller.ViewportWidth / taskButtons.Count) - 2,
            MinimumTaskWidth, MaximumTaskWidth);
        foreach (TaskButton item in taskButtons.Values)
            item.Button.Width = width;
    }

    private void UpdateOverflow()
    {
        if (overflow is null || taskScroller is null)
            return;
        bool needsScroll = taskScroller.ScrollableWidth > 1;
        bool canHide = taskScroller.ExtentWidth <= taskScroller.ViewportWidth + overflow.Width;
        if (overflow.Visibility == Visibility.Collapsed && needsScroll)
            overflow.Visibility = Visibility.Visible;
        else if (overflow.Visibility == Visibility.Visible && canHide)
            overflow.Visibility = Visibility.Collapsed;
    }

    private void AdvanceTasks()
    {
        if (taskScroller is null)
            return;
        double next = taskScroller.HorizontalOffset >= taskScroller.ScrollableWidth - 1
            ? 0
            : taskScroller.HorizontalOffset + Math.Max(MinimumTaskWidth, taskScroller.ViewportWidth * .8);
        taskScroller.ScrollToHorizontalOffset(next);
    }

    private void OpenMenu()
    {
        if (menu is not null)
        {
            CloseMenu();
            return;
        }
        if (fullScreen || bar is null || source is null || reservation is not ShellNative.Rectangle area)
            return;

        RunFromBar(() =>
        {
            var monitor = GetTargetDisplay();
            if (monitor is null)
                return;
            double scale = DpiScale(source.Handle);
            int pixelWidth = Math.Min((int)Math.Round(397 * scale), monitor.Bounds.Right - area.Left);
            double menuHeight = StartMenu.PreferredHeight(options.StartMenuPins?.Length ?? 0);
            int pixelHeight = Math.Min((int)Math.Round(menuHeight * scale), area.Top - monitor.Bounds.Top);
            if (pixelWidth <= 0 || pixelHeight <= 0)
                return;
            var opened = new StartMenu(bar, visuals, openControl, showWindowsTaskbar, openXpExplorer, options.UseXpExplorer,
                options.StartMenuPins)
            {
                Width = pixelWidth / scale,
                Height = pixelHeight / scale,
                Opacity = 0
            };
            var bounds = new ShellNative.Rectangle(area.Left, area.Top - pixelHeight, area.Left + pixelWidth, area.Top);
            opened.Closed += (_, _) =>
            {
                if (ReferenceEquals(menu, opened))
                    menu = null;
            };
            opened.EscapeDismissed += () => TryFocusStart();
            menu = opened;
            try
            {
                nint handle = new WindowInteropHelper(opened).EnsureHandle();
                MoveWindow(handle, bounds);
                opened.Show();
                if (!ShellNative.GetWindowRect(handle, out ShellNative.Rectangle actual) || actual != bounds)
                    MoveWindow(handle, bounds);
                opened.Opacity = 1;
            }
            catch
            {
                CloseMenu();
                throw;
            }
        });
    }

    private void CloseMenu()
    {
        StartMenu? opened = menu;
        menu = null;
        opened?.Dismiss();
        if (openContextMenu is { } context)
            context.IsOpen = false;
    }

    private void OnBarPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || menu is not null || openContextMenu is { IsOpen: true })
            return;
        e.Handled = true;
        RunFromBar(openControl);
    }

    private void RefreshPalette()
    {
        if (disposed || bar is null || visuals.HighContrast == SystemParameters.HighContrast)
            return;
        CloseMenu();
        visuals = new LunaVisuals(variant);
        BuildBar();
        tasks?.Refresh();
        UpdateClock();
    }

    private void ClockTick(object? sender, EventArgs args) => UpdateClock();

    private void UpdateClock()
    {
        clockTimer.Stop();
        if (disposed || clock is null)
            return;
        DateTime now = DateTime.Now;
        clock.Content = now.ToString("t", CultureInfo.CurrentCulture);
        clock.ToolTip = now.ToString("D", CultureInfo.CurrentCulture);
        AutomationProperties.SetName(clock, $"Date and Time, {clock.Content}");
        clockTimer.Interval = TimeSpan.FromMilliseconds(60020 - now.Second * 1000 - now.Millisecond);
        clockTimer.Start();
    }

    private void OnBarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.WidthChanged)
            UpdateQuickLaunch(args.NewSize.Width);
        ResizeTaskButtons();
    }

    private void OnBarClosing(object? sender, CancelEventArgs args)
    {
        if (disposed)
            return;
        args.Cancel = true;
        RunFromBar(openControl);
    }

    private void RunFromBar(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or InvalidDataException or
                                  UnauthorizedAccessException or SecurityException)
        {
            if (bar is not null)
                MessageBox.Show(bar, ex.Message, "11toXP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed)
            return;
        disposed = true;
        clockTimer.Stop();
        clockTimer.Tick -= ClockTick;
        CloseMenu();
        tasks?.Dispose();
        tasks = null;
        RemoveReservation();
        source?.RemoveHook(OnWindowMessage);
        source = null;
        bar?.Close();
        bar = null;
        taskButtons.Clear();
    }

    private sealed class TaskButton(Button button, TextBlock title, TextBlock arrow, Image icon,
        UIElement fallbackIcon, TaskbarEntry entry)
    {
        internal Button Button { get; } = button;
        internal TextBlock Title { get; } = title;
        internal TextBlock Arrow { get; } = arrow;
        internal Image Icon { get; } = icon;
        internal UIElement FallbackIcon { get; } = fallbackIcon;
        internal TaskbarEntry Entry { get; set; } = entry;
    }
}
