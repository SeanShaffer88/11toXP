using System.IO;
using LunaDesktop.Appearance;
using LunaDesktop.Shell;

namespace LunaDesktop.State;

internal interface IThemePlatform
{
    int Build { get; }
    string StartupCommand();
    bool HasStartup(string command);
    void EnsureStartup(string command, string? previous);
    void RemoveStartup(string command);
    bool? ReadAutoHide();
    bool SetAutoHide(bool enabled);
    WallpaperPlan CaptureWallpaper(string image);
    void ApplyWallpaper(WallpaperPlan plan);
    void RestoreWallpaper(WallpaperPlan plan);
    AppearanceSnapshot? CaptureAppearance();
    void ValidateAppearance(AppearanceSnapshot baseline);
    void ApplyAppearance(AppearanceSnapshot baseline);
    void RestoreAppearance(AppearanceSnapshot baseline);
    void StartShell();
    void StopShell();
}

internal sealed class ThemePlatform(
    string directory, Action openControl, Action showWindowsTaskbar, Action explorerRestarted, Action<Exception> shellFaulted) : IThemePlatform
{
    private readonly DesktopAppearance appearance = new(directory);
    private LunaShell? shell;

    public int Build => Environment.OSVersion.Version.Build;
    public string StartupCommand() => WindowsSession.StartupCommand();
    public bool HasStartup(string command) => WindowsSession.HasStartup(command);
    public void EnsureStartup(string command, string? previous) => WindowsSession.EnsureStartup(command, previous);
    public void RemoveStartup(string command) => WindowsSession.RemoveStartup(command);
    public bool? ReadAutoHide() => WindowsSession.ReadAutoHide();
    public bool SetAutoHide(bool enabled) => WindowsSession.SetAutoHide(enabled);
    public WallpaperPlan CaptureWallpaper(string image) => DesktopWallpaper.Capture(image);
    public void ApplyWallpaper(WallpaperPlan plan) => DesktopWallpaper.Apply(plan);
    public void RestoreWallpaper(WallpaperPlan plan) => DesktopWallpaper.Restore(plan);
    public AppearanceSnapshot CaptureAppearance() => appearance.Capture();
    public void ValidateAppearance(AppearanceSnapshot baseline) => appearance.Validate(baseline);
    public void ApplyAppearance(AppearanceSnapshot baseline) => appearance.Apply(baseline);
    public void RestoreAppearance(AppearanceSnapshot baseline) => appearance.Restore(baseline);

    public void StartShell()
    {
        if (shell is not null)
        {
            shell.Repair();
            return;
        }
        var candidate = new LunaShell(openControl, showWindowsTaskbar);
        candidate.ExplorerRestarted += explorerRestarted;
        candidate.ShellFaulted += shellFaulted;
        try
        {
            candidate.Start();
            shell = candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public void StopShell()
    {
        if (shell is null)
            return;
        shell.ExplorerRestarted -= explorerRestarted;
        shell.ShellFaulted -= shellFaulted;
        shell.Dispose();
        shell = null;
    }
}

internal sealed class ThemeCoordinator(ThemeJournal journal, IThemePlatform platform)
{
    public ThemeState? State { get; private set; }
    public bool ShellPaused { get; private set; }
    public string ShellStatus { get; private set; } = "Windows taskbar";
    public string AppearanceStatus { get; private set; } = "Windows settings";
    public string WallpaperStatus { get; private set; } = "Unmanaged";
    public string StartupStatus { get; private set; } = "Off";
    public string CompatibilityStatus { get; private set; } = "Ready";

    public void Load()
    {
        State = journal.Load();
        if (State is not { } state)
            return;
        if (state.StartupCommand is { } command && !WindowsSession.IsStartupCommand(command) ||
            state.PreviousStartupCommand is { } previous && !WindowsSession.IsStartupCommand(previous))
            throw new InvalidDataException("The sign-in restore record is invalid.");
        if (state.Wallpaper is { } wallpaper)
        {
            DesktopWallpaper.Validate(wallpaper);
            WallpaperStatus = "Opted in";
        }
        if (state.Appearance is { } appearance)
        {
            platform.ValidateAppearance(appearance);
            AppearanceStatus = "Saved for restore";
        }
        StartupStatus = state.StartupCommand is not null && platform.HasStartup(state.StartupCommand) ? "On" : "Off";
        if (state.Phase != ThemePhase.Active)
            CompatibilityStatus = "Restore pending";
    }

    public void Apply(string? image)
    {
        if (State is not null)
            throw new InvalidOperationException("An existing restore record needs Repair or Restore.");
        var next = new ThemeState
        {
            Phase = ThemePhase.Applying,
            StartupCommand = platform.StartupCommand(),
            Wallpaper = image is null ? null : platform.CaptureWallpaper(image),
            Appearance = platform.CaptureAppearance()
        };
        journal.Save(next);
        State = next;
        try
        {
            RepairCore(next, verifyBuild: true);
            next.Phase = ThemePhase.Active;
            journal.Save(next);
        }
        catch (Exception applyError)
        {
            try { Restore(); }
            catch (Exception restoreError)
            {
                throw new InvalidOperationException(
                    $"Apply failed and Restore needs attention. {applyError.Message} {restoreError.Message}", restoreError);
            }
            throw new InvalidOperationException($"Apply failed. Previous settings were restored. {applyError.Message}", applyError);
        }
    }

    public void Repair(string? image)
    {
        var state = ActiveState();
        if (image is not null)
        {
            var wallpaper = state.Wallpaper is { } existing
                ? existing.Select(image)
                : platform.CaptureWallpaper(image);
            DesktopWallpaper.Validate(wallpaper);
            state.Wallpaper = wallpaper;
            journal.Save(state);
        }
        else if (state.Wallpaper is { } wallpaper)
        {
            platform.RestoreWallpaper(wallpaper);
            state.Wallpaper = null;
            journal.Save(state);
        }
        RepairCore(state, verifyBuild: true);
    }

    public void Resume()
    {
        if (State is { Phase: ThemePhase.Active } state)
            RepairCore(state, verifyBuild: false);
    }

    public void ExplorerRestarted()
    {
        if (ShellPaused || State is not { Phase: ThemePhase.Active } state)
            return;
        RefreshShell(state, verifyBuild: false);
    }

    public void ShowWindowsTaskbar()
    {
        var state = ActiveState();
        platform.StopShell();
        ShellPaused = true;
        ShellStatus = "Paused";
        RestoreAutoHide(state);
        CompatibilityStatus = "Repair resumes the XP taskbar";
    }

    public void RecoverShellFault()
    {
        if (State is not { } state)
            return;
        ShellPaused = true;
        ShellStatus = "Paused after a taskbar error";
        CompatibilityStatus = "Repair needed";
        try { platform.StopShell(); }
        finally { RestoreAutoHide(state); }
    }

    public void Restore()
    {
        if (State is not { } state)
            return;
        state.Phase = ThemePhase.Restoring;
        journal.Save(state);
        CompatibilityStatus = "Restore pending";
        var errors = new List<string>();
        AttemptRestore("XP taskbar", () => platform.StopShell(), () => ShellStatus = "Windows taskbar", errors);
        AttemptRestore("Windows taskbar", () => RestoreAutoHide(state), () => { }, errors);
        if (state.StartupCommand is { } command)
        {
            AttemptRestore("Sign-in startup", () =>
            {
                platform.RemoveStartup(command);
                if (state.PreviousStartupCommand is { } previous)
                    platform.RemoveStartup(previous);
            }, () =>
            {
                state.StartupCommand = null;
                state.PreviousStartupCommand = null;
                StartupStatus = "Off";
            }, errors);
        }
        if (state.Appearance is { } appearance)
        {
            AttemptRestore("Appearance", () => platform.RestoreAppearance(appearance), () =>
            {
                state.Appearance = null;
                AppearanceStatus = "Windows settings";
            }, errors);
        }
        if (state.Wallpaper is { } wallpaper)
        {
            AttemptRestore("Wallpaper", () => platform.RestoreWallpaper(wallpaper), () =>
            {
                state.Wallpaper = null;
                WallpaperStatus = "Unmanaged";
            }, errors);
        }
        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        journal.Clear();
        State = null;
        ShellPaused = false;
        CompatibilityStatus = "Ready";
    }

    public void StopShell() => platform.StopShell();

    private ThemeState ActiveState() =>
        State is { Phase: ThemePhase.Active } state ? state :
        throw new InvalidOperationException("Use Restore to finish the interrupted operation.");

    private void RepairCore(ThemeState state, bool verifyBuild)
    {
        var errors = new List<string>();
        ShellPaused = false;
        RunComponent("XP taskbar", () => RefreshShell(state, verifyBuild), () => ShellStatus = "Needs repair", errors);
        if (state.Appearance is { } appearance)
        {
            RunComponent("Appearance", () =>
            {
                platform.ApplyAppearance(appearance);
                AppearanceStatus = "XP-style";
            }, () => AppearanceStatus = "Needs repair", errors);
        }
        if (state.Wallpaper is { } wallpaper)
        {
            RunComponent("Wallpaper", () =>
            {
                platform.ApplyWallpaper(wallpaper);
                WallpaperStatus = "Opted in and applied";
            }, () => WallpaperStatus = "Needs repair", errors);
        }
        else
        {
            WallpaperStatus = "Unmanaged";
        }
        if (state.StartupCommand is not null)
        {
            RunComponent("Sign-in startup", () => RefreshStartup(state, verifyBuild), () => StartupStatus = "Needs repair", errors);
        }
        if (errors.Count != 0)
        {
            CompatibilityStatus = "Repair needed";
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    private void RefreshStartup(ThemeState state, bool allowMove)
    {
        var current = platform.StartupCommand();
        if (!string.Equals(state.StartupCommand, current, StringComparison.OrdinalIgnoreCase))
        {
            if (!allowMove)
            {
                StartupStatus = "App moved. Repair updates sign-in.";
                return;
            }
            if (state.PreviousStartupCommand is not null && platform.HasStartup(state.PreviousStartupCommand))
            {
                state.StartupCommand = state.PreviousStartupCommand;
                state.PreviousStartupCommand = null;
            }
            state.PreviousStartupCommand = state.StartupCommand;
            state.StartupCommand = current;
            journal.Save(state);
        }
        platform.EnsureStartup(current, state.PreviousStartupCommand);
        if (state.PreviousStartupCommand is not null)
        {
            state.PreviousStartupCommand = null;
            journal.Save(state);
        }
        StartupStatus = "On";
    }

    private void RefreshShell(ThemeState state, bool verifyBuild)
    {
        try { platform.StartShell(); }
        catch
        {
            RestoreAutoHide(state);
            throw;
        }
        ShellStatus = "XP taskbar active";
        var current = platform.ReadAutoHide();
        if (!verifyBuild && state.AutoHideBuild != platform.Build)
        {
            if (state.OriginalAutoHide is not null)
                RestoreAutoHide(state);
            CompatibilityStatus = state.AutoHideBuild == 0
                ? "Auto-hide unverified. Repair checks compatibility."
                : "Windows build changed. Repair verifies auto-hide.";
        }
        else if (current == true)
        {
            SaveAutoHideBuild(state, platform.Build);
            CompatibilityStatus = $"Auto-hide verified on build {platform.Build}";
        }
        else if (current == false)
        {
            if (!verifyBuild && state.OriginalAutoHide is null)
            {
                CompatibilityStatus = "Windows taskbar visible. Repair verifies auto-hide.";
                return;
            }
            if (state.OriginalAutoHide is null)
            {
                state.OriginalAutoHide = false;
                journal.Save(state);
            }
            if (platform.SetAutoHide(true))
            {
                SaveAutoHideBuild(state, platform.Build);
                CompatibilityStatus = $"Auto-hide verified on build {platform.Build}";
            }
            else
            {
                RestoreAutoHide(state);
                SaveAutoHideBuild(state, 0);
                CompatibilityStatus = "Windows kept its taskbar visible";
            }
        }
        else
        {
            CompatibilityStatus = "Windows taskbar auto-hide unavailable";
        }
    }

    private void SaveAutoHideBuild(ThemeState state, int build)
    {
        if (state.AutoHideBuild == build)
            return;
        state.AutoHideBuild = build;
        journal.Save(state);
    }

    private void RestoreAutoHide(ThemeState state)
    {
        if (state.OriginalAutoHide is not { } original)
            return;
        if (platform.ReadAutoHide() != original && !platform.SetAutoHide(original))
            throw new InvalidOperationException("Windows did not restore taskbar auto-hide. Try again after Explorer is ready.");
        state.OriginalAutoHide = null;
        journal.Save(state);
    }

    private void AttemptRestore(string name, Action restore, Action finish, List<string> errors) =>
        RunComponent(name, () =>
        {
            restore();
            finish();
            journal.Save(State!);
        }, () => { }, errors);

    private static void RunComponent(string name, Action action, Action failed, List<string> errors)
    {
        try { action(); }
        catch (Exception error)
        {
            failed();
            errors.Add($"{name}: {error.Message}");
        }
    }
}
