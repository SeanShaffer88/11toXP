using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LunaDesktop.Shell;

internal sealed class WindowTasks : IDisposable
{
    private const uint ForegroundEvent = 0x0003;
    private const uint MinimizeStartEvent = 0x0016;
    private const uint MinimizeEndEvent = 0x0017;
    private const uint CreateEvent = 0x8000;
    private const uint DestroyEvent = 0x8001;
    private const uint HideEvent = 0x8003;
    private const uint NameChangeEvent = 0x800C;
    private const uint CloakEvent = 0x8017;
    private const uint UncloakEvent = 0x8018;
    private const uint Root = 2;
    private const uint RootOwner = 3;
    private const uint Owner = 4;
    private const int Style = -16;
    private const int ExtendedStyle = -20;
    private const int MinimizeBox = 0x00020000;
    private const int MaximizeBox = 0x00010000;
    private const uint SystemCommandMessage = 0x0112;
    private const uint MenuDisabled = 0x0003;
    private const int ToolWindow = 0x80;
    private const int AppWindow = 0x40000;
    private const int NoActivateWindow = 0x08000000;
    private const uint CloakedAttribute = 14;
    private const int MaxTasks = 512;

    private readonly nint barWindow;
    private readonly Func<nint> menuWindow;
    private readonly DispatcherTimer refreshTimer;
    private readonly bool groupWindows;
    private readonly ShellNative.WinEventCallback callback;
    private readonly List<nint> hooks = [];
    private List<TaskWindow> current = [];
    private HashSet<nint> tracked = [];
    private nint lastTaskForeground;
    private bool disposed;

    internal WindowTasks(nint barWindow, Func<nint> menuWindow, Dispatcher dispatcher, bool groupWindows = false)
    {
        this.barWindow = barWindow;
        this.menuWindow = menuWindow;
        this.groupWindows = groupWindows;
        callback = OnWinEvent;
        refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        refreshTimer.Tick += RefreshTick;
    }

    internal event Action<IReadOnlyList<TaskWindow>, nint>? Changed;
    internal nint FullScreenWindow { get; set; }

    internal void Start()
    {
        try
        {
            AddHook(ForegroundEvent, ForegroundEvent);
            AddHook(MinimizeStartEvent, MinimizeEndEvent);
            AddHook(CreateEvent, HideEvent);
            AddHook(NameChangeEvent, NameChangeEvent);
            AddHook(CloakEvent, UncloakEvent);
            Refresh();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AddHook(uint first, uint last)
    {
        // With WINEVENT_OUTOFCONTEXT (0), the installing dispatcher receives
        // events without loading this code into any other process.
        nint hook = ShellNative.SetWinEventHook(first, last, 0, callback, 0, 0, 0);
        if (hook == 0)
            throw new InvalidOperationException("Windows could not register the taskbar window-event listener.");
        hooks.Add(hook);
    }

    private void OnWinEvent(nint hook, uint eventId, nint window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (disposed || window == 0 || objectId != 0 || childId != 0)
            return;

        if (eventId == ForegroundEvent)
            ObserveForeground(window);
        else if (window != FullScreenWindow && eventId == DestroyEvent)
        {
            if (!tracked.Contains(window))
                return;
        }
        else if (window != FullScreenWindow && !tracked.Contains(window) && ShellNative.GetAncestor(window, Root) != window)
            return;

        if (!refreshTimer.IsEnabled)
            refreshTimer.Start();
    }

    private void RefreshTick(object? sender, EventArgs args)
    {
        refreshTimer.Stop();
        Refresh();
    }

    internal void Refresh()
    {
        if (disposed)
            return;

        var previous = current.ToDictionary(task => task.Handle);
        var remaining = new Dictionary<nint, TaskWindow>();
        var discovered = new List<nint>();
        nint menu = menuWindow();
        ShellNative.EnumWindows((window, _) =>
        {
            if (remaining.Count >= MaxTasks || !TryReadTask(window, menu, out string title, out uint process))
                return true;

            bool sameWindow = previous.TryGetValue(window, out TaskWindow? old) && old.Process == process;
            ImageSource? icon = sameWindow ? old!.Icon : ReadIcon(window);
            TaskIdentity? identity = null;
            if (groupWindows)
                identity = sameWindow && old!.Title == title ? old.Identity : TaskIdentity.Read(window, process);
            remaining.Add(window, new TaskWindow(window, process, title, icon, identity));
            discovered.Add(window);
            return true;
        }, 0);

        // Z-order changes on every switch. Keep existing task buttons in
        // creation order so the target under the pointer does not jump.
        var next = new List<TaskWindow>(remaining.Count);
        foreach (TaskWindow old in current)
        {
            if (remaining.Remove(old.Handle, out TaskWindow? item) && item.Process == old.Process)
                next.Add(item);
            else if (item is not null)
                remaining.Add(item.Handle, item);
        }
        foreach (nint window in discovered)
        {
            if (remaining.Remove(window, out TaskWindow? item))
                next.Add(item);
        }

        current = next;
        tracked = next.Select(task => task.Handle).ToHashSet();
        ObserveForeground(ShellNative.GetForegroundWindow());
        Changed?.Invoke(current, lastTaskForeground);
    }

    private bool TryReadTask(nint window, nint menu, out string title, out uint process)
    {
        title = "";
        process = 0;
        if (window == barWindow || window == menu || !ShellNative.IsWindowVisible(window))
            return false;
        int style = ShellNative.GetWindowLongW(window, ExtendedStyle);
        if ((style & (ToolWindow | NoActivateWindow)) != 0
            || ((style & AppWindow) == 0 && ShellNative.GetWindow(window, Owner) != 0))
            return false;
        if (ShellNative.DwmGetWindowAttribute(window, CloakedAttribute, out uint cloaked, sizeof(uint)) == 0
            && cloaked != 0)
            return false;

        var text = new StringBuilder(512);
        if (ShellNative.GetWindowTextW(window, text, text.Capacity) == 0)
            return false;
        title = text.ToString().Trim();
        if (title.Length == 0)
            return false;

        var className = new StringBuilder(96);
        ShellNative.GetClassNameW(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        return ShellNative.GetWindowThreadProcessId(window, out process) != 0 &&
            (process != Environment.ProcessId || title is not ("11toXP Taskbar" or "11toXP Start Menu"));
    }

    private static ImageSource? ReadIcon(nint window)
    {
        nint icon = (nint)ShellNative.GetClassLongPtrW(window, -34); // GCLP_HICONSM
        if (icon == 0)
            icon = (nint)ShellNative.GetClassLongPtrW(window, -14); // GCLP_HICON
        if (icon == 0)
            return null;

        try
        {
            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            // The window may have closed while the borrowed icon was read.
            return null;
        }
    }

    private void ObserveForeground(nint window)
    {
        if (window == barWindow || window == menuWindow())
            return;
        if (tracked.Contains(window))
        {
            lastTaskForeground = window;
            return;
        }
        nint rootOwner = ShellNative.GetAncestor(window, RootOwner);
        lastTaskForeground = tracked.Contains(rootOwner) ? rootOwner : 0;
    }

    internal bool SwitchTo(TaskWindow task)
    {
        if (!IsCurrent(task))
            return false;

        ObserveForeground(ShellNative.GetForegroundWindow());
        if (!ShellNative.IsIconic(task.Handle) && lastTaskForeground == task.Handle)
            return Minimize(task);

        return Activate(task);
    }

    internal static bool Activate(TaskWindow task)
    {
        if (!IsCurrent(task))
            return false;
        if (ShellNative.IsIconic(task.Handle))
            ShellNative.ShowWindowAsync(task.Handle, 9); // SW_RESTORE
        nint target = ShellNative.GetLastActivePopup(task.Handle);
        if (target == 0 || !ShellNative.IsWindowVisible(target))
            target = task.Handle;
        return ShellNative.SetForegroundWindow(target);
    }

    private static bool Minimize(TaskWindow task) =>
        IsCurrent(task) && ShellNative.ShowWindowAsync(task.Handle, 6);

    internal static bool CanExecute(TaskWindow task, TaskWindowCommand command)
    {
        if (!IsCurrent(task) || !ShellNative.IsWindowEnabled(task.Handle))
            return false;

        int style = ShellNative.GetWindowLongW(task.Handle, Style);
        bool minimized = ShellNative.IsIconic(task.Handle);
        bool maximized = ShellNative.IsZoomed(task.Handle);
        return command switch
        {
            TaskWindowCommand.Restore => minimized || maximized,
            TaskWindowCommand.Minimize => !minimized && (style & MinimizeBox) != 0,
            TaskWindowCommand.Maximize => (!maximized || minimized) && (style & MaximizeBox) != 0,
            TaskWindowCommand.Close => CanClose(task.Handle),
            _ => false
        };
    }

    internal static bool Execute(TaskWindow task, TaskWindowCommand command)
    {
        if (!CanExecute(task, command))
            return false;

        var title = new StringBuilder(512);
        return ShellNative.GetWindowTextW(task.Handle, title, title.Capacity) != 0 &&
               title.ToString().Trim() == task.Title &&
               IsCurrent(task) &&
               ShellNative.PostMessageW(task.Handle, SystemCommandMessage, (nint)command, 0);
    }

    private static bool CanClose(nint window)
    {
        nint menu = ShellNative.GetSystemMenu(window, false);
        if (menu == 0)
            return true;
        uint state = ShellNative.GetMenuState(menu, (uint)TaskWindowCommand.Close, 0);
        return state != uint.MaxValue && (state & MenuDisabled) == 0;
    }

    private static bool IsCurrent(TaskWindow task) =>
        ShellNative.IsWindowVisible(task.Handle) &&
        ShellNative.GetWindowThreadProcessId(task.Handle, out uint process) != 0 && process == task.Process;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        refreshTimer.Stop();
        refreshTimer.Tick -= RefreshTick;
        foreach (nint hook in hooks)
            ShellNative.UnhookWinEvent(hook);
        hooks.Clear();
    }
}

internal enum TaskWindowCommand
{
    Minimize = 0xF020,
    Maximize = 0xF030,
    Close = 0xF060,
    Restore = 0xF120
}

internal sealed record TaskWindow(nint Handle, uint Process, string Title, ImageSource? Icon, TaskIdentity? Identity = null);
