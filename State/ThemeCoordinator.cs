using System.IO;
using LunaDesktop.Appearance;
using LunaDesktop.Diagnostics;
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
    AppearanceSnapshot CaptureAppearance(AppearanceComponents components, AppearanceSnapshot? retained);
    void ValidateAppearance(AppearanceSnapshot baseline);
    void ApplyAppearance(AppearanceSnapshot baseline, AppearanceComponents components);
    void RestoreAppearance(AppearanceSnapshot baseline, AppearanceComponents components);
    void StartShell(LunaVariant variant, TaskbarOptions options);
    void StopShell();
}

internal sealed class ThemePlatform(
    string directory, Action openControl, Action showWindowsTaskbar, Action explorerRestarted,
    Action<Exception> shellFaulted, Action<string?> openXpExplorer) : IThemePlatform
{
    private readonly DesktopAppearance appearance = new(directory);
    private LunaShell? shell;
    private LunaVariant shellVariant;
    private TaskbarOptions? shellOptions;

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
    public AppearanceSnapshot CaptureAppearance(AppearanceComponents components, AppearanceSnapshot? retained) =>
        appearance.Capture(components, retained);
    public void ValidateAppearance(AppearanceSnapshot baseline) => appearance.Validate(baseline);
    public void ApplyAppearance(AppearanceSnapshot baseline, AppearanceComponents components) =>
        appearance.Apply(baseline, components);
    public void RestoreAppearance(AppearanceSnapshot baseline, AppearanceComponents components) =>
        appearance.Restore(baseline, components);

    internal bool CanFocusXpTaskbar => shell?.CanFocusStart == true;
    internal bool TryFocusXpTaskbar() => shell?.TryFocusStart() == true;

    public void StartShell(LunaVariant variant, TaskbarOptions options)
    {
        if (shell is not null && shellVariant == variant && shellOptions is { } previous &&
            previous.Grouping == options.Grouping && previous.UseXpExplorer == options.UseXpExplorer &&
            previous.HasSameQuickLaunch(options))
        {
            shell.Repair();
            return;
        }
        StopShell();
        var candidate = new LunaShell(openControl, showWindowsTaskbar, variant, options, openXpExplorer);
        candidate.ExplorerRestarted += explorerRestarted;
        candidate.ShellFaulted += shellFaulted;
        try
        {
            candidate.Start();
            shell = candidate;
            shellVariant = variant;
            shellOptions = options;
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
        shellOptions = null;
    }
}

internal sealed class ThemeCoordinator(ThemeJournal journal, IThemePlatform platform, ThemePreferenceStore? preferencesStore = null)
{
    public ThemeState? State { get; private set; }
    public bool ShellPaused { get; private set; }
    public string ShellStatus { get; private set; } = "Windows taskbar";
    public string AppearanceStatus { get; private set; } = "Windows settings";
    public string WallpaperStatus { get; private set; } = "Unmanaged";
    public string StartupStatus { get; private set; } = "Off";
    public string CompatibilityStatus { get; private set; } = "Ready";
    public RecoveryFailure? LastRecoveryFailure { get; private set; }

    public void Load()
    {
        State = journal.Load();
        if (State is not { } state)
            return;
        state.Preferences = state.Preferences?.CheckedCopy() ??
            throw new InvalidDataException("The appearance options are missing.");
        var managed = ManagedAppearance(state);
        if ((managed & ~AppearanceComponents.All) != 0 ||
            (managed == AppearanceComponents.None) != (state.Appearance is null))
            throw new InvalidDataException("The appearance restore record is invalid.");
        if (state.StartupCommand is { } command && !WindowsSession.IsStartupCommand(command) ||
            state.PreviousStartupCommand is { } previous && !WindowsSession.IsStartupCommand(previous))
            throw new InvalidDataException("The sign-in restore record is invalid.");
        if (state.Wallpaper is { } wallpaper)
        {
            DesktopWallpaper.Validate(wallpaper);
            WallpaperStatus = state.WallpaperRestorePending ? "Restore pending" : "Opted in";
        }
        if (state.Appearance is { } appearance)
        {
            platform.ValidateAppearance(appearance);
            AppearanceStatus = "Saved for restore";
        }
        var registered = (state.StartupCommand is { } current && platform.HasStartup(current)) ||
            (state.PreviousStartupCommand is { } old && platform.HasStartup(old));
        if (!registered)
            StartupStatus = "Off";
        else
            StartupStatus = state.Preferences.StartWithWindows ? "On" : "Removal pending";
        if (state.Phase != ThemePhase.Active)
            CompatibilityStatus = "Restore pending";
    }

    public void Apply(string? image, ThemePreferences? preferences = null)
    {
        if (State is not null)
            throw new InvalidOperationException("An existing restore record needs Repair or Restore.");
        var options = (preferences ?? ThemePreferences.Default).CheckedCopy();
        var next = new ThemeState
        {
            Phase = ThemePhase.Applying,
            StartupCommand = options.StartWithWindows ? platform.StartupCommand() : null,
            Wallpaper = image is null ? null : platform.CaptureWallpaper(image),
            Preferences = options,
            AppearanceManaged = options.Appearance,
            Appearance = options.Appearance == AppearanceComponents.None
                ? null
                : platform.CaptureAppearance(options.Appearance, retained: null)
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
        SavePreferences(options);
    }

    public void Repair(string? image, ThemePreferences? preferences = null)
    {
        var state = ActiveState();
        var options = (preferences ?? state.Preferences).CheckedCopy();
        var previous = state.Preferences;
        var previousWallpaper = state.Wallpaper;
        var previousPending = state.WallpaperRestorePending;
        var previousStartupCommand = state.StartupCommand;
        var startupCommand = options.StartWithWindows ? previousStartupCommand ?? platform.StartupCommand() : previousStartupCommand;
        if (image is not null)
        {
            var wallpaper = state.Wallpaper is { } existing
                ? existing.Select(image)
                : platform.CaptureWallpaper(image);
            DesktopWallpaper.Validate(wallpaper);
            state.Wallpaper = wallpaper;
            state.WallpaperRestorePending = false;
        }
        else if (state.Wallpaper is not null)
        {
            state.WallpaperRestorePending = true;
        }
        state.Preferences = options;
        state.StartupCommand = startupCommand;
        try { journal.Save(state); }
        catch
        {
            state.Preferences = previous;
            state.Wallpaper = previousWallpaper;
            state.WallpaperRestorePending = previousPending;
            state.StartupCommand = previousStartupCommand;
            throw;
        }
        RepairCore(state, verifyBuild: true);
        SavePreferences(options);
    }

    public void Resume()
    {
        if (State is { Preferences.StartWithWindows: false })
            Restore();
        else if (State is { Phase: ThemePhase.Active } state)
            RepairCore(state, verifyBuild: false);
    }

    public void ExplorerRestarted()
    {
        if (ShellPaused || State is not { Phase: ThemePhase.Active, Preferences.UseTaskbar: true } state)
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

    public void RecoverShellFault(Exception fault)
    {
        if (State is not { } state)
            return;
        LastRecoveryFailure = RecoveryFailure.From("XP taskbar", fault);
        ShellPaused = true;
        ShellStatus = "Paused after a taskbar error";
        CompatibilityStatus = "Repair needed";
        try
        {
            try { platform.StopShell(); }
            finally { RestoreAutoHide(state); }
        }
        catch (Exception error)
        {
            LastRecoveryFailure = RecoveryFailure.From("Windows taskbar", error);
            throw;
        }
    }

    public void Restore()
    {
        if (State is not { } state)
            return;
        state.Phase = ThemePhase.Restoring;
        SaveRestoreJournal(() => journal.Save(state));
        CompatibilityStatus = "Restore pending";
        var errors = new List<string>();
        AttemptRestore("XP taskbar", () => platform.StopShell(), () => ShellStatus = "Windows taskbar", errors);
        AttemptRestore("Windows taskbar", () => RestoreAutoHide(state), () => { }, errors);
        if (state.StartupCommand is not null || state.PreviousStartupCommand is not null)
            RunComponent("Sign-in startup", () => ClearStartup(state), () => StartupStatus = "Needs repair", errors);
        if (state.Appearance is { } appearance)
        {
            RunComponent("Appearance", () =>
            {
                platform.RestoreAppearance(appearance, ManagedAppearance(state));
                SaveAppearance(state, null, AppearanceComponents.None);
                AppearanceStatus = "Windows settings";
            }, () => AppearanceStatus = "Restore pending", errors);
        }
        if (state.Wallpaper is { } wallpaper)
        {
            RunComponent("Wallpaper", () =>
            {
                FinishWallpaperRestore(state, wallpaper);
            }, () => WallpaperStatus = "Restore pending", errors);
        }
        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        SaveRestoreJournal(journal.Clear);
        State = null;
        ShellPaused = false;
        CompatibilityStatus = "Ready";
    }

    public void StopShell() => platform.StopShell();

    private ThemeState ActiveState() =>
        State is { Phase: ThemePhase.Active } state ? state :
        throw new InvalidOperationException("Use Restore to finish the interrupted operation.");

    private void SaveRestoreJournal(Action save)
    {
        try { save(); }
        catch (Exception error)
        {
            LastRecoveryFailure = RecoveryFailure.From("Restore journal", error);
            throw;
        }
    }

    private void SavePreferences(ThemePreferences options)
    {
        try { preferencesStore?.Save(options); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InvalidOperationException($"The theme was updated, but 11toXP could not save the options for next time. {error.Message}", error);
        }
    }

    private void RepairCore(ThemeState state, bool verifyBuild)
    {
        var errors = new List<string>();
        ShellPaused = false;
        RunComponent("XP taskbar", () => RefreshShell(state, verifyBuild), () => ShellStatus = "Needs repair", errors);
        RunComponent("Appearance", () => ReconcileAppearance(state), () => AppearanceStatus = "Needs repair", errors);
        if (state.Wallpaper is { } wallpaper)
        {
            RunComponent("Wallpaper", () =>
            {
                if (state.WallpaperRestorePending)
                    FinishWallpaperRestore(state, wallpaper);
                else
                {
                    platform.ApplyWallpaper(wallpaper);
                    WallpaperStatus = "Opted in and applied";
                }
            }, () => WallpaperStatus = state.WallpaperRestorePending ? "Restore pending" : "Needs repair", errors);
        }
        else
        {
            WallpaperStatus = "Unmanaged";
        }
        RunComponent("Sign-in startup", () =>
        {
            if (!state.Preferences.StartWithWindows)
                ClearStartup(state);
            else if (verifyBuild || state.StartupCommand is not null)
                RefreshStartup(state, verifyBuild);
        }, () => StartupStatus = "Needs repair", errors);
        if (errors.Count != 0)
        {
            CompatibilityStatus = "Repair needed";
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    private static AppearanceComponents ManagedAppearance(ThemeState state) =>
        state.AppearanceManaged ?? (state.Appearance is null ? AppearanceComponents.None : AppearanceComponents.All);

    private void ReconcileAppearance(ThemeState state)
    {
        var managed = ManagedAppearance(state);
        var selected = state.Preferences.Appearance;
        var removed = managed & ~selected;
        if (removed != AppearanceComponents.None && state.Appearance is { } previous)
        {
            platform.RestoreAppearance(previous, removed);
            managed &= ~removed;
            var remaining = managed == AppearanceComponents.None ? null : DesktopAppearance.Retain(previous, managed);
            SaveAppearance(state, remaining, managed);
        }
        var added = selected & ~managed;
        if (added != AppearanceComponents.None)
            SaveAppearance(state, platform.CaptureAppearance(added, state.Appearance), managed | added);
        if (state.Appearance is { } appearance)
            platform.ApplyAppearance(appearance, selected);
        AppearanceStatus = selected == AppearanceComponents.None ? "Windows settings" : $"XP-style: {selected}";
    }

    private void SaveAppearance(ThemeState state, AppearanceSnapshot? snapshot, AppearanceComponents managed)
    {
        var previous = state.Appearance;
        var previousManaged = state.AppearanceManaged;
        state.Appearance = snapshot;
        state.AppearanceManaged = managed;
        try { journal.Save(state); }
        catch
        {
            state.Appearance = previous;
            state.AppearanceManaged = previousManaged;
            throw;
        }
    }

    private void FinishWallpaperRestore(ThemeState state, WallpaperPlan wallpaper)
    {
        platform.RestoreWallpaper(wallpaper);
        var previousPending = state.WallpaperRestorePending;
        state.Wallpaper = null;
        state.WallpaperRestorePending = false;
        try { journal.Save(state); }
        catch
        {
            state.Wallpaper = wallpaper;
            state.WallpaperRestorePending = previousPending;
            throw;
        }
        WallpaperStatus = "Unmanaged";
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
            var previous = state.PreviousStartupCommand is { } pending && platform.HasStartup(pending)
                ? pending
                : state.StartupCommand;
            SaveStartup(state, current, previous);
        }
        platform.EnsureStartup(current, state.PreviousStartupCommand);
        if (state.PreviousStartupCommand is not null)
            SaveStartup(state, current, null);
        StartupStatus = "On";
    }

    private void ClearStartup(ThemeState state)
    {
        if (state.StartupCommand is { } command)
            platform.RemoveStartup(command);
        if (state.PreviousStartupCommand is { } previous)
            platform.RemoveStartup(previous);
        if (state.StartupCommand is not null || state.PreviousStartupCommand is not null)
            SaveStartup(state, null, null);
        StartupStatus = "Off";
    }

    private void SaveStartup(ThemeState state, string? command, string? previous)
    {
        var oldCommand = state.StartupCommand;
        var oldPrevious = state.PreviousStartupCommand;
        state.StartupCommand = command;
        state.PreviousStartupCommand = previous;
        try { journal.Save(state); }
        catch
        {
            state.StartupCommand = oldCommand;
            state.PreviousStartupCommand = oldPrevious;
            throw;
        }
    }

    private void RefreshShell(ThemeState state, bool verifyBuild)
    {
        if (!state.Preferences.UseTaskbar)
        {
            platform.StopShell();
            RestoreAutoHide(state);
            ShellStatus = "Windows taskbar";
            CompatibilityStatus = "XP taskbar disabled";
            return;
        }
        try { platform.StartShell(state.Preferences.Variant, state.Preferences.Taskbar); }
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
        try { journal.Save(state); }
        catch
        {
            state.OriginalAutoHide = original;
            throw;
        }
    }

    private void AttemptRestore(string name, Action restore, Action finish, List<string> errors) =>
        RunComponent(name, () =>
        {
            restore();
            finish();
            journal.Save(State!);
        }, () => { }, errors);

    private void RunComponent(string name, Action action, Action failed, List<string> errors)
    {
        try { action(); }
        catch (Exception error)
        {
            LastRecoveryFailure = RecoveryFailure.From(name, error);
            failed();
            errors.Add($"{name}: {error.Message}");
        }
    }
}
