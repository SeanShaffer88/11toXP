using System.IO;
using System.Security.Principal;
using System.Windows;
using LunaDesktop.Browser;
using LunaDesktop.State;

namespace LunaDesktop;

public partial class App : Application
{
    private Mutex? processLock;
    private bool ownsLock;
    private EventWaitHandle? showRequest;
    private RegisteredWaitHandle? showRegistration;
    private ThemeCoordinator? coordinator;
    private ControlWindow? control;
    private ExplorerWindow? explorer;
    private string? startupError;
    private string? directory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            MessageBox.Show("11toXP requires Windows 11.", "11toXP", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }
        if (e.Args is ["--preview"])
        {
            control = new ControlWindow(this, preview: true);
            control.Show();
            return;
        }
        if (e.Args is ["--self-test"])
        {
            Shutdown(ThemeChecks.Run());
            return;
        }
        if (e.Args.Length > 0 && e.Args is not ["--background"] and not ["--restore"])
        {
            Shutdown(2);
            return;
        }

        var restoreOnly = e.Args is ["--restore"];
        var background = e.Args is ["--background"];
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value ?? throw new InvalidOperationException("Windows did not identify the current user.");
            var eventName = $@"Local\11toXP.Show.{user}";
            processLock = new Mutex(false, $@"Global\11toXP.State.{user}");
            try { ownsLock = processLock.WaitOne(0); }
            catch (AbandonedMutexException) { ownsLock = true; }
            if (!ownsLock)
            {
                if (background)
                {
                    Shutdown();
                    return;
                }
                if (!restoreOnly && EventWaitHandle.TryOpenExisting(eventName, out var request))
                {
                    using (request)
                        request.Set();
                    Shutdown();
                    return;
                }
                Shutdown(3);
                return;
            }
            showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            showRegistration = ThreadPool.RegisterWaitForSingleObject(
                showRequest, (_, _) => Dispatcher.BeginInvoke(ShowControl), null, Timeout.Infinite, executeOnlyOnce: false);
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "11toXP");
            var platform = new ThemePlatform(directory, ShowControl, ShowWindowsTaskbar, ExplorerRestarted, ShellFaulted,
                path => OpenXpExplorer(coordinator?.State?.Preferences.Variant ?? LunaVariant.Blue, path));
            coordinator = new ThemeCoordinator(new ThemeJournal(directory), platform);
            coordinator.Load();
        }
        catch (Exception error)
        {
            startupError = error.Message;
            coordinator = null;
            if (restoreOnly)
            {
                Shutdown(1);
                return;
            }
            ShowControl();
            return;
        }

        if (restoreOnly)
        {
            try
            {
                coordinator.Restore();
                Shutdown();
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }
        if (coordinator.State is { Phase: ThemePhase.Active })
        {
            try { coordinator.Resume(); }
            catch (Exception error) { startupError = error.Message; }
        }
        if (!background || startupError is not null || coordinator.State is { Phase: not ThemePhase.Active })
            ShowControl();
        else if (coordinator.State is null)
            Shutdown();
    }

    internal ThemeCoordinator? Coordinator => coordinator;
    internal string? StartupError => startupError;
    internal string? StateDirectory => directory;
    internal void ClearStartupError() => startupError = null;

    internal void OpenXpExplorer(LunaVariant variant, string? path = null)
    {
        if (explorer is null)
        {
            explorer = new ExplorerWindow(variant, directory);
            explorer.Closed += (_, _) => explorer = null;
            explorer.Show();
            explorer.NavigateTo(path);
        }
        else
        {
            explorer.SetVariant(variant);
            if (path is not null)
                explorer.NavigateTo(path);
            if (explorer.WindowState == WindowState.Minimized)
                explorer.WindowState = WindowState.Normal;
        }
        explorer.Activate();
    }

    internal void UpdateXpExplorerVariant(LunaVariant variant) => explorer?.SetVariant(variant);

    internal void CloseControl(bool preview)
    {
        if (preview || coordinator?.State is null)
        {
            Shutdown();
            return;
        }
        control?.Hide();
    }

    private void ShowControl()
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        control ??= new ControlWindow(this, preview: false);
        control.Show();
        if (control.WindowState == WindowState.Minimized)
            control.WindowState = WindowState.Normal;
        control.Activate();
    }

    private void ShowWindowsTaskbar()
    {
        ShowControl();
        control?.PerformOperation(() => coordinator?.ShowWindowsTaskbar(), "Windows taskbar shown. Repair resumes the XP taskbar.");
    }

    private void ExplorerRestarted()
    {
        try
        {
            coordinator?.ExplorerRestarted();
            control?.RefreshStatus();
        }
        catch (Exception error) { ShellFaulted(error); }
    }

    private void ShellFaulted(Exception fault)
    {
        startupError = fault.Message;
        try { coordinator?.RecoverShellFault(); }
        catch (Exception error) { startupError += " " + error.Message; }
        ShowControl();
        control?.RefreshStatus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { coordinator?.StopShell(); }
        finally
        {
            showRegistration?.Unregister(null);
            showRequest?.Dispose();
            if (ownsLock)
                processLock?.ReleaseMutex();
            processLock?.Dispose();
            base.OnExit(e);
        }
    }
}
