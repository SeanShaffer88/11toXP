using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LunaDesktop.Appearance;
using LunaDesktop.Diagnostics;
using LunaDesktop.Shell;

namespace LunaDesktop.State;

internal static class ThemeChecks
{
    private const string Image = @"C:\Selected.jpg";
    private static ThemePreferences XpTaskbarOnly => ThemePreferences.Default with { KeepWindowsTrayVisible = false };

    public static int Run()
    {
        try
        {
            WithSession(OrdinaryLifecycle);
            WithSession(OptInLifecycle);
            WithSession(PartialApply);
            WithSession(InterruptedRestore);
            WithSession(BuildChanged);
            WithSession(AlreadyAutoHides);
            WithSession(WindowsTrayDefault);
            WithSession(WindowsTrayModeChanges);
            WithSession(WindowsTrayUnavailable);
            WithSession(TemporaryTrayRevealLifecycle);
            WithSession(TemporaryTrayRevealFailures);
            WithSession(TemporaryTrayRevealRecovery);
            WithSession(TaskbarPreviewIsolation);
            WithSession(TaskbarPreviewTrayVisibility);
            WithSession(TaskbarPreviewTrayRecovery);
            TransparentTrayMask();
            WithSession(TaskbarPreviewFailure);
            WithSession(TaskbarPreviewRollbackFallback);
            WithSession(DisplaySelectionRecovery);
            DisplaySelectionMapping();
            WithSession(MovedExecutable);
            WithSession(SignInCollision);
            WithSession(SessionOnlyLifecycle);
            WithSession(InterruptedStartupRegistration);
            WithSession(InterruptedStartupRemoval);
            WithSession(DiagnosticPrivacy);
            DiagnosticPreservesFiles();
            WithSession(ComponentLifecycle);
            WithSession(InterruptedComponentChange);
            WithSession(InterruptedWallpaperOptOut);
            LegacyJournal();
            SavedPreferences();
            WithSession(PinnedStartMenuLifecycle);
            AppearanceProfilePersistence();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void OrdinaryLifecycle(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Resume();
        Require(platform.ShellCalls == 0, "Sign-in cannot enable an unapplied theme.");
        app.Apply(null, XpTaskbarOnly);
        Require(platform.ShellWasStartedBeforeAutoHide, "Start the custom taskbar before hiding the stock taskbar.");
        app.Repair(null);
        app.ShowWindowsTaskbar();
        Require(!platform.Shell && !platform.AutoHide, "Show Windows taskbar.");
        app.Repair(null);
        var resumed = new ThemeCoordinator(journal, platform);
        resumed.Load();
        resumed.Resume();
        resumed.ExplorerRestarted();
        resumed.Restore();
        Require(platform.WallpaperCalls == 0, "Default lifecycle must not call the wallpaper adapter.");
        Require(!platform.Shell && !platform.AutoHide && platform.Registered is null && journal.Load() is null,
            "Restore native taskbar and remove startup and journal.");
    }

    private static void OptInLifecycle(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(Image);
        Require(platform.Captures == 1 && platform.Wallpaper == Image, "Explicit opt-in records and sets the picture.");
        app.Repair(Image);
        Require(platform.Captures == 1, "Repair preserves the initial wallpaper snapshot.");
        app.Repair(null);
        var afterOptOut = platform.WallpaperCalls;
        Require(platform.Wallpaper == FakePlatform.Original && journal.Load()?.Wallpaper is null, "Opt-out restores the wallpaper.");
        app.Repair(null);
        app.Restore();
        Require(platform.WallpaperCalls == afterOptOut, "Later operations leave wallpaper alone.");
    }

    private static void PartialApply(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        platform.FailWallpaperApply = true;
        ExpectFailure(() => app.Apply(Image));
        Require(platform.Wallpaper == FakePlatform.Original && journal.Load() is null &&
            platform.Registered is null && !platform.AutoHide && !platform.Shell, "Partial Apply rolls back.");
    }

    private static void InterruptedRestore(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(Image);
        platform.FailWallpaperRestore = true;
        ExpectFailure(app.Restore);
        Require(journal.Load() is { Phase: ThemePhase.Restoring, Wallpaper: not null } &&
            platform.Registered is null && !platform.Shell, "Interrupted Restore records remaining ownership and removes startup.");
        var resumed = new ThemeCoordinator(journal, platform);
        resumed.Load();
        resumed.Resume();
        Require(!platform.Shell, "Interrupted Restore must not re-enable the shell.");
        platform.FailWallpaperRestore = false;
        resumed.Restore();
        Require(journal.Load() is null && platform.Wallpaper == FakePlatform.Original, "Restore is retryable.");
    }

    private static void BuildChanged(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null, XpTaskbarOnly);
        platform.Build++;
        app.Resume();
        Require(platform.Shell && !platform.AutoHide, "Keep stock taskbar visible on an unverified build.");
        app.Repair(null);
        Require(platform.AutoHide && journal.Load()?.AutoHideBuild == platform.Build, "Explicit Repair verifies auto-hide.");
        app.Restore();
    }

    private static void MovedExecutable(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null);
        var oldCommand = platform.Registered;
        platform.CurrentCommand = "\"C:\\New Path\\11toXP.exe\" --background";
        app.Resume();
        Require(platform.Registered == oldCommand, "Sign-in only migrates the executable path on explicit Repair.");
        app.Repair(null);
        Require(platform.Registered == platform.CurrentCommand && journal.Load()?.PreviousStartupCommand is null,
            "Repair migrates the owned startup command and checkpoints it.");
        app.Restore();
        Require(platform.Registered is null, "Restore removes the migrated startup command.");
    }

    private static void AlreadyAutoHides(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        platform.SetAutoHide(true);
        app.Apply(null, XpTaskbarOnly);
        Require(journal.Load()?.OriginalAutoHide is null, "An already enabled auto-hide preference is not owned.");
        platform.SetAutoHide(false);
        app.Resume();
        Require(!platform.AutoHide, "Automatic repair preserves a preference we never managed.");
        app.Restore();
        Require(!platform.AutoHide, "Restore preserves a user's subsequent auto-hide change.");
    }

    private static void WindowsTrayDefault(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null);
        Require(platform.Shell && !platform.AutoHide && platform.AutoHideWrites == 0 &&
            journal.Load() is { Preferences.KeepWindowsTrayVisible: true, OriginalAutoHide: null },
            "The default keeps an already visible Windows taskbar untouched.");
        app.Restore();

        platform.SetAutoHide(true);
        platform.BeforeSetAutoHide = enabled =>
        {
            if (!enabled)
                Require(journal.Load()?.OriginalAutoHide == true, "Persist the original auto-hide setting before exposing the tray.");
        };
        app.Apply(null);
        Require(platform.Shell && !platform.AutoHide && journal.Load()?.OriginalAutoHide == true,
            "The default exposes the Windows tray even when auto-hide was previously enabled.");
        app.ShowWindowsTaskbar();
        Require(!platform.Shell && platform.AutoHide && journal.Load()?.OriginalAutoHide is null,
            "Pausing the XP taskbar restores the previous native auto-hide setting.");
        app.Repair(null);
        app.RecoverShellFault(new InvalidOperationException("Injected shell fault."));
        Require(!platform.Shell && platform.AutoHide && journal.Load()?.OriginalAutoHide is null,
            "Recovering a shell fault restores the previous native auto-hide setting.");
        app.Repair(null);
        app.Restore();
        Require(!platform.Shell && platform.AutoHide && journal.Load() is null,
            "Restoring an active tray-visible profile returns to the previously auto-hidden taskbar.");
    }

    private static void WindowsTrayModeChanges(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null, XpTaskbarOnly);
        Require(platform.AutoHide && journal.Load()?.OriginalAutoHide == false, "XP-only mode owns the change.");
        app.Repair(null, ThemePreferences.Default);
        Require(!platform.AutoHide && journal.Load()?.OriginalAutoHide is null,
            "Switching to the visible tray returns to the original visible setting and releases ownership.");
        app.Restore();

        platform.SetAutoHide(true);
        app.Apply(null);
        app.Repair(null, XpTaskbarOnly);
        Require(platform.AutoHide && journal.Load()?.OriginalAutoHide is null,
            "Switching to XP-only returns to the original hidden setting and releases ownership.");
        app.Repair(null, ThemePreferences.Default);
        Require(!platform.AutoHide && journal.Load()?.OriginalAutoHide == true,
            "Switching back to the visible tray records the unchanged original baseline.");
        app.Restore();
        Require(platform.AutoHide, "Mode changes preserve the user's original setting.");
    }

    private static void WindowsTrayUnavailable(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        platform.SetAutoHide(true);
        platform.FailingAutoHideTarget = false;
        app.Apply(null);
        Require(platform.AutoHide && journal.Load()?.OriginalAutoHide == true &&
            app.CompatibilityStatus.Contains("could not"), "An unverified tray write retains ownership and reports compatibility.");
        platform.FailingAutoHideTarget = null;
        app.Repair(null);
        Require(!platform.AutoHide, "Repair retries keeping the Windows tray visible.");
        var priorWrites = platform.AutoHideWrites;
        platform.AutoHideAvailable = false;
        app.Repair(null);
        Require(platform.AutoHideWrites == priorWrites && journal.Load()?.OriginalAutoHide == true,
            "Repair does not write an unknown auto-hide setting or discard its baseline.");
        ExpectFailure(app.Restore);
        Require(platform.AutoHideWrites == priorWrites && journal.Load()?.OriginalAutoHide == true,
            "Restore also leaves unknown auto-hide untouched and retains recovery state.");
        platform.AutoHideAvailable = true;
        app.Restore();
        Require(platform.AutoHide && journal.Load() is null, "Restore retries when the native taskbar is available again.");
    }

    private static void TemporaryTrayRevealLifecycle(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null);
        ExpectFailure(app.ToggleTrayReveal);
        Require(!platform.TrayRevealAllowed && platform.AutoHideWrites == 0,
            "Temporary reveal is available only in XP-only mode.");
        app.Repair(null, XpTaskbarOnly);
        int shellCalls = platform.ShellCalls;
        int appearanceCalls = platform.AppearanceApplies.Count;
        string? startup = platform.Registered;
        app.ToggleTrayReveal();
        Require(platform.TrayRevealAllowed && app.TrayRevealed && platform.Shell && !platform.AutoHide &&
            journal.Load() is { OriginalAutoHide: false, Preferences.KeepWindowsTrayVisible: false },
            "Show keeps the XP shell and retains recovery for the temporary tray.");
        app.ToggleTrayReveal();
        Require(!app.TrayRevealed && platform.AutoHide && journal.Load()?.OriginalAutoHide == false &&
            platform.ShellCalls == shellCalls && platform.AppearanceApplies.Count == appearanceCalls &&
            platform.WallpaperCalls == 0 && platform.Registered == startup,
            "Hide restores XP-only mode without restarting the XP shell or applying unrelated changes.");

        app.ToggleTrayReveal();
        app.Repair(null);
        Require(!app.TrayRevealed && platform.AutoHide, "Repair ends a temporary reveal.");
        app.ToggleTrayReveal();
        app.ShowWindowsTaskbar();
        ExpectFailure(app.ToggleTrayReveal);
        Require(!app.TrayRevealed && !platform.AutoHide && !platform.Shell,
            "Pausing clears the temporary override and preserves the original native setting.");
        app.Restore();
    }

    private static void TemporaryTrayRevealFailures(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        platform.SetAutoHide(true);
        app.Apply(null, XpTaskbarOnly);
        platform.BeforeSetAutoHide = enabled =>
        {
            if (!enabled)
                Require(journal.Load()?.OriginalAutoHide == true, "Checkpoint original auto-hide before reveal.");
        };
        platform.AutoHideAvailable = false;
        int writes = platform.AutoHideWrites;
        ExpectFailure(app.ToggleTrayReveal);
        Require(platform.AutoHideWrites == writes && journal.Load()?.OriginalAutoHide is null,
            "An unavailable native setting cannot be written or claimed.");
        platform.AutoHideAvailable = true;
        platform.FailingAutoHideTarget = false;
        ExpectFailure(app.ToggleTrayReveal);
        Require(!app.TrayRevealed && platform.AutoHide && journal.Load()?.OriginalAutoHide == true,
            "An unconfirmed show retains recovery and can be retried.");
        platform.FailingAutoHideTarget = null;
        app.ToggleTrayReveal();
        platform.FailingAutoHideTarget = true;
        ExpectFailure(app.ToggleTrayReveal);
        Require(app.TrayRevealed && !platform.AutoHide && journal.Load()?.OriginalAutoHide == true,
            "An unconfirmed hide retains the reveal and its restore baseline.");
        platform.FailingAutoHideTarget = null;
        app.ToggleTrayReveal();
        Require(!app.TrayRevealed && platform.AutoHide && journal.Load()?.OriginalAutoHide is null,
            "A verified hide releases ownership when auto-hide was originally on.");
        platform.AutoHideReadbackUnconfirmed = true;
        app.ToggleTrayReveal();
        Require(app.TrayRevealed && !platform.AutoHide, "A fresh read can confirm a previously unconfirmed show.");
        app.ToggleTrayReveal();
        Require(!app.TrayRevealed && platform.AutoHide, "A fresh read can confirm a previously unconfirmed hide.");
        platform.BeforeSetAutoHide = _ => platform.AutoHideAvailable = false;
        ExpectFailure(app.ToggleTrayReveal);
        Require(!app.TrayRevealed && !platform.AutoHide && journal.Load()?.OriginalAutoHide == true,
            "A native change without available readback is recoverable.");
        platform.BeforeSetAutoHide = null;
        platform.AutoHideAvailable = true;
        app.ToggleTrayReveal();
        Require(app.TrayRevealed && !platform.AutoHide, "Retry recognizes an already visible tray with retained ownership.");
        app.ToggleTrayReveal();
        platform.SetAutoHide(false);
        writes = platform.AutoHideWrites;
        ExpectFailure(app.ToggleTrayReveal);
        Require(!app.TrayRevealed && platform.AutoHideWrites == writes && journal.Load()?.OriginalAutoHide is null,
            "An already visible tray without retained ownership cannot be adopted.");
        app.Restore();
    }

    private static void TemporaryTrayRevealRecovery(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null, XpTaskbarOnly);
        app.ToggleTrayReveal();
        platform.SetAutoHide(true);
        app.ExplorerRestarted();
        Require(app.TrayRevealed && !platform.AutoHide && journal.Load()?.OriginalAutoHide == false,
            "Explorer restart reasserts the temporary reveal and retains ownership.");

        var reopened = new ThemeCoordinator(journal, platform);
        reopened.Load();
        reopened.Resume();
        Require(!reopened.TrayRevealed && platform.AutoHide,
            "A new process resumes XP-only mode after an interrupted reveal.");
        platform.Build++;
        reopened.Resume();
        int writes = platform.AutoHideWrites;
        ExpectFailure(reopened.ToggleTrayReveal);
        Require(!platform.AutoHide && platform.AutoHideWrites == writes,
            "Temporary reveal cannot bypass the Windows build compatibility check.");
        reopened.Restore();

        platform.SetAutoHide(true);
        reopened.Apply(null, XpTaskbarOnly);
        reopened.ToggleTrayReveal();
        reopened.Restore();
        Require(!reopened.TrayRevealed && platform.AutoHide && !platform.Shell && journal.Load() is null,
            "Restore during a reveal returns to the originally auto-hidden taskbar.");
    }

    private static void TaskbarPreviewIsolation(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        var first = XpTaskbarOnly with { Taskbar = XpTaskbarOnly.Taskbar with { IsLocked = false } };
        Require(app.NeedsTaskbarPreview(first) && !app.NeedsTaskbarPreview(first with { UseTaskbar = false }),
            "Only an initial Apply with a taskbar needs a preview.");
        app.BeginTaskbarPreview(first);
        Require(app.IsTaskbarPreviewActive && platform.Shell && !platform.TrayRevealAllowed && journal.Load() is null &&
            platform.AutoHideWrites == 0 && platform.StartupCommandReads == 0 && platform.AppearanceCaptures.Count == 0 &&
            platform.WallpaperCalls == 0, "Preview changes only appbar windows.");
        ExpectFailure(app.ShowWindowsTaskbar);
        ExpectFailure(app.ToggleTrayReveal);
        app.CancelTaskbarPreview();
        Require(!app.IsTaskbarPreviewActive && !platform.Shell && journal.Load() is null,
            "Cancelling the first preview leaves the original desktop and no journal.");

        app.BeginTaskbarPreview(first);
        app.CommitTaskbarPreview(() => app.Apply(null, first));
        Require(!app.IsTaskbarPreviewActive && platform.AutoHide && journal.Load()?.Preferences.Taskbar.IsLocked == false,
            "Keep commits the original Apply path.");
        Require(!app.NeedsTaskbarPreview(first with { StartWithWindows = false }) &&
            app.NeedsTaskbarPreview(first with { UseTaskbar = false }), "Only shell changes need confirmation.");

        app.ToggleTrayReveal();
        int writes = platform.AutoHideWrites;
        int appearanceApplies = platform.AppearanceApplies.Count;
        int startupReads = platform.StartupCommandReads;
        var smaller = first with { Taskbar = first.Taskbar with { ButtonSize = TaskbarButtonSize.Small } };
        app.BeginTaskbarPreview(smaller);
        app.ExplorerRestarted();
        Require(platform.Options?.ButtonSize == TaskbarButtonSize.Small &&
            journal.Load()?.Preferences.Taskbar.ButtonSize == TaskbarButtonSize.Large,
            "Explorer refresh during preview keeps its candidate separate from the journal.");
        app.CancelTaskbarPreview();
        Require(platform.Options?.ButtonSize == TaskbarButtonSize.Large && app.TrayRevealed &&
            platform.AutoHideWrites == writes && !platform.AutoHide, "Cancel restores layout and retains tray reveal without writing native state.");
        app.BeginTaskbarPreview(smaller);
        app.CommitTaskbarPreview(() => app.KeepTaskbarResize(TaskbarButtonSize.Small));
        Require(journal.Load()?.Preferences.Taskbar.ButtonSize == TaskbarButtonSize.Small &&
            platform.AppearanceApplies.Count == appearanceApplies && platform.StartupCommandReads == startupReads &&
            platform.AutoHideWrites == writes && platform.WallpaperCalls == 0, "The resize grip commits only taskbar layout.");
        app.Restore();
    }

    private static void TaskbarPreviewTrayVisibility(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null, XpTaskbarOnly);
        var candidate = XpTaskbarOnly with { KeepWindowsTrayVisible = true };
        platform.BeforeSetAutoHide = enabled =>
        {
            if (!enabled && app.IsTaskbarPreviewActive)
                Require(journal.HasPreviewTrayRollback(), "The preview records rollback before revealing the tray.");
        };
        app.BeginTaskbarPreview(candidate);
        Require(!platform.AutoHide && platform.Shell && platform.TrayThumbnailShown && journal.HasPreviewTrayRollback() &&
            journal.Load() is { OriginalAutoHide: false, Preferences.KeepWindowsTrayVisible: false },
            "Preview reveals the native tray and retains the committed XP-only state.");

        app.CancelTaskbarPreview();
        Require(platform.AutoHide && platform.Shell && !platform.TrayThumbnailShown && !journal.HasPreviewTrayRollback() &&
            journal.Load()?.Preferences.KeepWindowsTrayVisible == false,
            "Undo and timeout restore the previous native tray state and layout.");

        app.BeginTaskbarPreview(candidate);
        app.ExplorerRestarted();
        Require(!app.IsTaskbarPreviewActive && platform.AutoHide && platform.Shell && !journal.HasPreviewTrayRollback(),
            "Explorer recovery ends the tray preview and reconciles the committed layout.");

        app.BeginTaskbarPreview(candidate);
        app.CommitTaskbarPreview(() => app.Repair(null, candidate));
        Require(!platform.AutoHide && platform.TrayThumbnailShown,
            "Keeping the visible tray layout mirrors the tray after the countdown.");
        app.BeginTaskbarPreview(XpTaskbarOnly);
        Require(!platform.TrayThumbnailShown, "The XP-only candidate does not mirror the native tray.");
        app.CancelTaskbarPreview();
        Require(!platform.AutoHide && platform.TrayThumbnailShown,
            "Undo restores the committed visible tray and its thumbnail.");
        app.Repair(null, XpTaskbarOnly);
        Require(platform.AutoHide && !platform.TrayThumbnailShown,
            "The committed XP-only layout does not mirror the native tray.");

        app.BeginTaskbarPreview(candidate);
        platform.ConnectedDisplays = 2;
        app.DisplayTopologyChanged();
        Require(!app.IsTaskbarPreviewActive && !journal.HasPreviewTrayRollback() && !platform.AutoHide &&
            journal.Load() is { OriginalAutoHide: null, Preferences.KeepWindowsTrayVisible: false },
            "Topology changes undo the preview and release the committed global auto-hide override.");

        platform.SetAutoHide(true);
        int writes = platform.AutoHideWrites;
        app.BeginTaskbarPreview(candidate);
        Require(platform.AutoHide && !platform.TrayThumbnailShown && !journal.HasPreviewTrayRollback() && platform.AutoHideWrites == writes,
            "Preview does not change the global tray setting with multiple connected displays.");
        app.CancelTaskbarPreview();
        app.Repair(null, candidate);
        Require(platform.AutoHide && !platform.TrayThumbnailShown,
            "The committed layout leaves native tray visibility and mirroring alone with multiple displays.");
        platform.ConnectedDisplays = 1;
        app.DisplayTopologyChanged();
        Require(!platform.AutoHide && platform.TrayThumbnailShown,
            "Returning to one display restores the committed native tray mirror.");
        app.Restore();
    }

    private static void TaskbarPreviewTrayRecovery(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        var candidate = XpTaskbarOnly with { KeepWindowsTrayVisible = true };
        app.BeginTaskbarPreview(candidate);
        Require(!journal.HasPreviewTrayRollback(), "An already visible tray needs no rollback record.");
        platform.SetAutoHide(true);
        app.ExplorerRestarted();
        Require(!app.IsTaskbarPreviewActive && !platform.Shell && !journal.HasPreviewTrayRollback(),
            "Explorer recovery never starts new native ownership midway through a preview.");

        platform.FailingAutoHideTarget = false;
        ExpectFailure(() => app.BeginTaskbarPreview(candidate));
        Require(platform.AutoHide && !platform.Shell && !app.IsTaskbarPreviewActive &&
            !journal.HasPreviewTrayRollback() && journal.Load() is null,
            "A failed reveal clears its completed rollback and does not create committed state.");
        platform.FailingAutoHideTarget = null;

        app.BeginTaskbarPreview(candidate);
        platform.StopShell();
        var reopened = new ThemeCoordinator(journal, platform);
        reopened.Load();
        platform.FailingAutoHideTarget = true;
        ExpectFailure(reopened.Resume);
        Require(journal.HasPreviewTrayRollback() && !platform.Shell && journal.Load() is null,
            "Unfinished preview recovery survives a failed startup attempt without applying a layout.");
        platform.FailingAutoHideTarget = null;
        reopened.Restore();
        Require(platform.AutoHide && !journal.HasPreviewTrayRollback() && journal.Load() is null,
            "Restore can recover the native tray even without committed theme state.");

        reopened.BeginTaskbarPreview(candidate);
        reopened.StopShell();
        Require(platform.AutoHide && !platform.Shell && !journal.HasPreviewTrayRollback(),
            "Normal exit restores the pre-preview tray setting.");

        reopened.BeginTaskbarPreview(candidate);
        reopened.CommitTaskbarPreview(() => reopened.Apply(null, candidate));
        Require(!platform.AutoHide && !journal.HasPreviewTrayRollback() && journal.Load()?.OriginalAutoHide == true,
            "Keep captures the original native setting before committing the visible tray.");
        reopened.Restore();
        Require(platform.AutoHide && journal.Load() is null, "A later Restore returns the original auto-hide setting.");
    }

    private static void TransparentTrayMask()
    {
        const int width = 52, height = 32;
        const int icon = unchecked((int)0xFF10A5D1);
        foreach (int gray in new[] { 0x29, 0xE7 })
        {
            int background = unchecked((int)0xFF000000) | gray * 0x010101;
            var pixels = new int[width * height];
            Array.Fill(pixels, background);
            Require(TrayIconCapture.Extract(pixels, width, height) is null,
                "A blank tray frame does not produce a mirror.");

            for (int row = 0; row < height; row++)
                pixels[row * width + 1] = icon;
            for (int col = 0; col < width; col++)
                pixels[1 * width + col] = icon;
            Require(TrayIconCapture.Extract(pixels, width, height) is null,
                "The vertical divider and horizontal tray edges do not survive cropping.");

            for (int col = 18; col < 22; col++)
                pixels[16 * width + col] = icon;
            pixels[16 * width + 22] = unchecked((int)0xFF000000) | (gray + 16) * 0x010101;
            var frame = TrayIconCapture.Extract(pixels, width, height);
            Require(frame is { Width: 46, Height: 24 } &&
                frame.Pixels.Count(pixel => pixel != 0) == 5 &&
                frame.Pixels.Count(pixel => pixel == icon) == 4 &&
                frame.Pixels.Count(pixel => (uint)pixel >> 24 is > 0 and < 255) == 1,
                "Flat dark and light tray backgrounds become transparent with solid and antialiased icon pixels.");
        }
        Require(TrayIconCapture.Extract(new int[width * height], width - 1, height) is null &&
            TrayIconCapture.Extract([], int.MaxValue, int.MaxValue) is null,
            "Invalid tray dimensions are rejected before mask allocation.");
    }

    private static void TaskbarPreviewFailure(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null, XpTaskbarOnly);
        var candidate = XpTaskbarOnly with { Variant = LunaVariant.OliveGreen };
        platform.FailingShellVariant = candidate.Variant;
        ExpectFailure(() => app.BeginTaskbarPreview(candidate));
        Require(!app.IsTaskbarPreviewActive && platform.Shell && platform.Variant == XpTaskbarOnly.Variant,
            "A failed preview restores the previous bar.");
        platform.FailingShellVariant = null;
        app.BeginTaskbarPreview(candidate);
        ExpectFailure(() => app.CommitTaskbarPreview(() => throw new InvalidOperationException("Injected commit failure.")));
        Require(!app.IsTaskbarPreviewActive && platform.Variant == XpTaskbarOnly.Variant &&
            journal.Load()?.Preferences.Variant == XpTaskbarOnly.Variant, "Commit failure restores authoritative journal layout.");
        app.ShowWindowsTaskbar();
        app.BeginTaskbarPreview(candidate);
        app.CancelTaskbarPreview();
        Require(app.ShellPaused && !platform.Shell && !platform.AutoHide, "Cancel preserves a paused committed shell.");
        app.Restore();
    }

    private static void TaskbarPreviewRollbackFallback(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null, XpTaskbarOnly);
        var candidate = XpTaskbarOnly with { Variant = LunaVariant.OliveGreen };
        app.BeginTaskbarPreview(candidate);
        platform.FailingShellVariant = XpTaskbarOnly.Variant;
        ExpectFailure(app.CancelTaskbarPreview);
        Require(!app.IsTaskbarPreviewActive && app.ShellPaused && !platform.Shell && !platform.TrayRevealAllowed &&
            !platform.AutoHide && journal.Load() is { OriginalAutoHide: null, Preferences.Variant: LunaVariant.Blue },
            "A failed rollback pauses all XP bars and releases owned auto-hide without changing committed options.");
        int shellCalls = platform.ShellCalls;
        app.ExplorerRestarted();
        ExpectFailure(app.ToggleTrayReveal);
        Require(platform.ShellCalls == shellCalls, "An Explorer refresh or stale tray command cannot resurrect a failed bar.");

        platform.FailingShellVariant = null;
        app.Repair(null);
        app.BeginTaskbarPreview(candidate);
        platform.FailingShellVariant = XpTaskbarOnly.Variant;
        platform.FailingAutoHideTarget = false;
        ExpectFailure(() => app.CommitTaskbarPreview(() => throw new InvalidOperationException("Injected commit failure.")));
        Require(!app.IsTaskbarPreviewActive && app.ShellPaused && !platform.Shell && !platform.TrayRevealAllowed &&
            platform.AutoHide && journal.Load() is { OriginalAutoHide: false, Preferences.Variant: LunaVariant.Blue } &&
            app.LastRecoveryFailure is { Component: "windowsTaskbar" },
            "If Windows refuses the fallback, retain the owned baseline and record a recovery failure.");
        platform.FailingAutoHideTarget = null;
        app.Restore();
        Require(!platform.AutoHide && journal.Load() is null, "Restore retries the pending fallback baseline.");
    }

    private static void DisplaySelectionRecovery(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(null, XpTaskbarOnly);
        Require(platform.AutoHide, "XP-only mode hides the native taskbar on the one selected display.");
        var selection = XpTaskbarOnly with
        {
            Taskbar = XpTaskbarOnly.Taskbar with { DisplayIds = [@"DISPLAY\UnitTest"] }
        };
        app.BeginTaskbarPreview(selection);
        platform.ConnectedDisplays = 2;
        app.DisplayTopologyChanged();
        Require(!app.IsTaskbarPreviewActive && platform.Shell && !platform.AutoHide &&
            journal.Load()?.Preferences.Taskbar.DisplayIds is null, "Display topology changes cancel the candidate before reconciling the committed layout.");
        app.Repair(null, selection);
        Require(platform.Shell && !platform.AutoHide && !platform.TrayRevealAllowed &&
            journal.Load()?.OriginalAutoHide is null, "Multi-monitor mode releases owned native auto-hide.");
        int writes = platform.AutoHideWrites;
        platform.HasMatchingDisplay = false;
        app.ExplorerRestarted();
        Require(!platform.Shell && platform.AutoHideWrites == writes &&
            journal.Load()?.Preferences.Taskbar.DisplayIds?.Single() == @"DISPLAY\UnitTest",
            "A disconnected selection is retained without starting an XP taskbar on a different display.");
        platform.HasMatchingDisplay = true;
        app.ExplorerRestarted();
        Require(platform.Shell && !platform.TrayRevealAllowed && platform.AutoHideWrites == writes,
            "Reconnection shows the selected bar without changing Windows auto-hide on other displays.");
        app.Restore();
    }

    private static void DisplaySelectionMapping()
    {
        var left = new ShellDisplay("display-left", "Left display", false,
            new(-1920, -120, 0, 960), new(-1920, -120, 0, 920));
        var primary = new ShellDisplay("display-primary", "Primary display", true,
            new(0, 0, 2560, 1440), new(0, 0, 2560, 1400));
        var right = new ShellDisplay("display-right", "Right display", false,
            new(2560, 100, 4480, 1180), new(2560, 100, 4480, 1140));
        ShellDisplay[] enumerated = [right, left, primary];
        Require(ShellDisplay.Resolve(TaskbarOptions.Default, enumerated).SequenceEqual([primary]),
            "The default follows the primary display regardless of enumeration order.");

        var selected = TaskbarOptions.Default with { DisplayIds = ["DISPLAY-LEFT", "display-right"] };
        var matched = ShellDisplay.Resolve(selected, enumerated);
        Require(matched.Length == 2 && matched.Contains(left) && matched.Contains(right) && !matched.Contains(primary) &&
            matched.Single(display => display.Id == left.Id).Bounds == new ShellNative.Rectangle(-1920, -120, 0, 960),
            "Explicit multi-select matches device IDs and preserves a negative monitor origin.");
        Require(ShellDisplay.Resolve(selected, [primary, right, left]).ToHashSet().SetEquals(matched),
            "Reordering connected displays does not change the explicit target set.");

        var disconnected = TaskbarOptions.Default with { DisplayIds = ["display-disconnected"] };
        Require(ShellDisplay.Resolve(disconnected, enumerated).Length == 0,
            "A disconnected explicit choice must not fall back to the primary display.");
    }

    private static void SignInCollision(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        platform.Registered = "Another command";
        ExpectFailure(() => app.Apply(null));
        Require(platform.Registered == "Another command" && journal.Load() is null, "Preserve an unrelated startup command.");
    }

    private static void SessionOnlyLifecycle(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        const string unrelated = "Another command";
        var options = ThemePreferences.Default with { StartWithWindows = false };
        platform.Registered = unrelated;
        app.Apply(null, options);
        Require(platform.StartupCommandReads == 0 && platform.Registered == unrelated &&
            journal.Load() is { StartupCommand: null, Preferences.StartWithWindows: false },
            "A session-only Apply leaves startup alone.");
        ExpectFailure(() => app.Repair(null, options with { StartWithWindows = true }));
        Require(platform.Registered == unrelated, "Enabling sign-in preserves an unrelated command.");
        platform.Registered = null;
        app.Repair(null, options with { StartWithWindows = true });
        Require(platform.Registered == platform.CurrentCommand && journal.Load()?.StartupCommand == platform.CurrentCommand,
            "Repair re-enables and records the owned startup command.");
        app.Repair(null, options);
        Require(platform.Registered is null &&
            journal.Load() is { StartupCommand: null, PreviousStartupCommand: null, Preferences.StartWithWindows: false },
            "Disabling sign-in removes and clears the owned command.");
        var priorStarts = platform.ShellCalls;
        var reopened = new ThemeCoordinator(journal, platform);
        reopened.Load();
        reopened.Resume();
        Require(platform.ShellCalls == priorStarts && !platform.Shell && !platform.AutoHide && journal.Load() is null,
            "A cold launch restores a session-only journal without restarting the shell.");
    }

    private static void InterruptedStartupRemoval(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(Image);
        var recorded = journal.Load()!;
        var previous = "\"C:\\Previous\\11toXP.exe\" --background";
        recorded.PreviousStartupCommand = previous;
        journal.Save(recorded);
        app.Load();
        platform.StartupRemovalFailure = previous;
        ExpectFailure(() => app.Repair(Image, ThemePreferences.Default with { StartWithWindows = false }));
        Require(platform.Registered is null && journal.Load() is
            { StartupCommand: not null, PreviousStartupCommand: not null, Preferences.StartWithWindows: false },
            "Interrupted startup removal retains the desired setting and both journaled commands.");
        var reopened = new ThemeCoordinator(journal, platform);
        reopened.Load();
        ExpectFailure(reopened.Resume);
        Require(journal.Load() is { Phase: ThemePhase.Restoring, PreviousStartupCommand: not null },
            "Recovery checkpoints partial restoration.");
        platform.StartupRemovalFailure = null;
        platform.Registered = "Unrelated replacement";
        reopened.Resume();
        Require(platform.Registered == "Unrelated replacement" && platform.Wallpaper == FakePlatform.Original &&
            journal.Load() is null, "Retry completes recovery and preserves an unrelated startup entry.");
    }

    private static void InterruptedStartupRegistration(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        var options = ThemePreferences.Default with { StartWithWindows = false };
        app.Apply(null, options);
        platform.FailStartupRegistration = true;
        ExpectFailure(() => app.Repair(null, options with { StartWithWindows = true }));
        Require(platform.Registered is null && journal.Load() is
            { StartupCommand: not null, Preferences.StartWithWindows: true },
            "Re-enabling sign-in records the command and preference before attempting registration.");
        platform.FailStartupRegistration = false;
        var reopened = new ThemeCoordinator(journal, platform);
        reopened.Load();
        reopened.Resume();
        Require(platform.Registered == platform.CurrentCommand, "Resume retries recorded startup registration.");
        reopened.Restore();
    }

    private static void DiagnosticPrivacy(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        const string privatePath = @"C:\Users\PrivateUser\private-wallpaper.jpg";
        app.Apply(privatePath, ThemePreferences.Default with { StartWithWindows = false });
        platform.WallpaperRestoreError = privatePath;
        ExpectFailure(app.Restore);
        using var output = new MemoryStream();
        DiagnosticReport.Write(output, 26100, 1.5, 1.25, app, startupFailed: true);
        var json = Encoding.UTF8.GetString(output.ToArray());
        using var report = JsonDocument.Parse(json);
        var root = report.RootElement;
        Require(root.EnumerateObject().Select(property => property.Name).SequenceEqual(
                ["windowsBuild", "displayScaling", "enabledComponents", "lastRecoveryError"]) &&
            root.GetProperty("windowsBuild").GetInt32() == 26100 &&
            root.GetProperty("displayScaling").GetProperty("x").GetDouble() == 1.5 &&
            root.GetProperty("enabledComponents").GetProperty("wallpaper").GetBoolean() &&
            !root.GetProperty("enabledComponents").GetProperty("startWithWindows").GetBoolean() &&
            root.GetProperty("lastRecoveryError").GetProperty("component").GetString() == "wallpaper" &&
            root.GetProperty("lastRecoveryError").GetProperty("kind").GetString() == "operationFailed" &&
            !json.Contains("PrivateUser") && !json.Contains("private-wallpaper"),
            "Diagnostics report allowlisted values without copying private wallpaper or exception data.");
    }

    private static void DiagnosticPreservesFiles()
    {
        var directory = Directory.CreateTempSubdirectory("11toXP-diagnostics-");
        var destination = Path.Combine(directory.FullName, "theme-state.json");
        var report = Path.Combine(directory.FullName, "diagnostics.json");
        try
        {
            byte[] original = [0, 1, 42, 255];
            File.WriteAllBytes(destination, original);
            try
            {
                DiagnosticReport.WriteNew(destination, 26100, 1, 1, null, startupFailed: false);
                throw new InvalidOperationException("An export replaced an existing file.");
            }
            catch (IOException) { }
            Require(File.ReadAllBytes(destination).SequenceEqual(original) && directory.GetFiles().Length == 1,
                "A diagnostic export preserves the existing destination and cleans its staging file.");

            DiagnosticReport.WriteNew(report, 26100, 1, 1, null, startupFailed: false);
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            Require(json.RootElement.GetProperty("windowsBuild").GetInt32() == 26100 && directory.GetFiles().Length == 2,
                "A diagnostic export commits only the new JSON file.");
        }
        finally
        {
            File.Delete(destination);
            File.Delete(report);
            directory.Delete();
        }
    }

    private static void ComponentLifecycle(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        var initial = ThemePreferences.Default with { UseTaskbar = false, Appearance = AppearanceComponents.Icons };
        app.Apply(null, initial);
        Require(platform.AppearanceCaptures.SequenceEqual([AppearanceComponents.Icons]) &&
            platform.AppearanceApplies.SequenceEqual([AppearanceComponents.Icons]) &&
            platform.ShellCalls == 0 && platform.AutoHideReads == 0,
            "Disabled components must be outside capture, apply, and native taskbar operations.");

        var shortcuts = new[] { QuickLaunchItem.XpExplorer, QuickLaunchItem.Settings };
        var selected = new ThemePreferences(LunaVariant.Silver, true, AppearanceComponents.Icons | AppearanceComponents.Sounds,
            new TaskbarOptions(TaskGrouping.Always, shortcuts, true)) { KeepWindowsTrayVisible = false };
        app.Repair(null, selected);
        shortcuts[0] = QuickLaunchItem.FileExplorer;
        Require(platform.AppearanceCaptures.SequenceEqual([AppearanceComponents.Icons, AppearanceComponents.Sounds]) &&
            platform.Shell && platform.AutoHide && platform.Variant == LunaVariant.Silver,
            "Repair captures newly enabled components and keeps existing baselines.");
        Require(app.State?.Preferences.Taskbar.QuickLaunch[0] == QuickLaunchItem.XpExplorer,
            "Journal preferences copy Quick Launch choices defensively.");

        var resumed = new ThemeCoordinator(journal, platform);
        resumed.Load();
        resumed.Resume();
        Require(resumed.State?.Preferences.Taskbar is { Grouping: TaskGrouping.Always, UseXpExplorer: true } options &&
            options.QuickLaunch.SequenceEqual([QuickLaunchItem.XpExplorer, QuickLaunchItem.Settings]) &&
            platform.AppearanceCaptures.Count == 2,
            "Resume preserves preferences and captured baselines.");
        resumed.Repair(null, selected with { UseTaskbar = false, Appearance = AppearanceComponents.Sounds });
        Require(platform.AppearanceRestores.SequenceEqual([AppearanceComponents.Icons]) &&
            journal.Load()?.AppearanceManaged == AppearanceComponents.Sounds && !platform.AutoHide && !platform.Shell,
            "Disabling a component restores its baseline without restoring other components.");
        resumed.Restore();
        Require(platform.AppearanceRestores.SequenceEqual([AppearanceComponents.Icons, AppearanceComponents.Sounds]) &&
            platform.WallpaperCalls == 0 && journal.Load() is null,
            "Restore only visits remaining ownership and leaves wallpaper alone.");
    }

    private static void InterruptedComponentChange(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        var initial = ThemePreferences.Default with { Appearance = AppearanceComponents.Icons };
        app.Apply(null, initial);
        platform.FailAppearanceRestore = true;
        ExpectFailure(() => app.Repair(null, initial with { Appearance = AppearanceComponents.None }));
        Require(journal.Load() is { Preferences.Appearance: AppearanceComponents.None, AppearanceManaged: AppearanceComponents.Icons },
            "A failed component restore preserves the requested preference and pending ownership.");
        var resumed = new ThemeCoordinator(journal, platform);
        resumed.Load();
        platform.FailAppearanceRestore = false;
        resumed.Resume();
        Require(platform.AppearanceApplies.SequenceEqual([AppearanceComponents.Icons]) &&
            journal.Load() is { Appearance: null, AppearanceManaged: AppearanceComponents.None },
            "Resume retries a pending restore without reapplying the disabled component.");
        resumed.Restore();
    }

    private static void InterruptedWallpaperOptOut(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        app.Apply(Image);
        platform.FailWallpaperRestore = true;
        ExpectFailure(() => app.Repair(null));
        Require(journal.Load() is { WallpaperRestorePending: true, Wallpaper: not null },
            "Wallpaper opt-out is journaled before restoration.");
        var resumed = new ThemeCoordinator(journal, platform);
        resumed.Load();
        Require(resumed.State?.WallpaperSelection is null, "A pending wallpaper opt-out reopens unchecked.");
        platform.FailWallpaperRestore = false;
        resumed.Resume();
        Require(platform.Wallpaper == FakePlatform.Original && journal.Load() is { Wallpaper: null, WallpaperRestorePending: false },
            "Resume restores a previously opted-out wallpaper.");
        resumed.Restore();
    }

    private static void LegacyJournal()
    {
        var directory = Directory.CreateTempSubdirectory("11toXP-legacy-");
        var journal = new ThemeJournal(directory.FullName);
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "theme-state.json"),
                """{"Schema":1,"Phase":1,"Appearance":{"Version":1,"Values":[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]}}""");
            var platform = new FakePlatform();
            var app = new ThemeCoordinator(journal, platform);
            app.Load();
            app.Resume();
            Require(app.State?.Preferences.StartWithWindows == true && platform.Variant == LunaVariant.Blue &&
                platform.StartupCommandReads == 0 && platform.AppearanceCaptures.Count == 0 &&
                platform.AppearanceApplies.SequenceEqual([AppearanceComponents.All]),
                "Legacy active journals keep their original behavior and backup.");
            app.Restore();
            Require(platform.AppearanceRestores.SequenceEqual([AppearanceComponents.All]) && journal.Load() is null,
                "Legacy ownership remains restorable.");

            app.Apply(null, XpTaskbarOnly);
            var path = Path.Combine(directory.FullName, "theme-state.json");
            var record = JsonNode.Parse(File.ReadAllText(path))!;
            record["Preferences"]!.AsObject().Remove("StartWithWindows");
            record["Preferences"]!.AsObject().Remove("KeepWindowsTrayVisible");
            File.WriteAllText(path, record.ToJsonString());
            platform.Registered = null;
            var reopened = new ThemeCoordinator(journal, platform);
            reopened.Load();
            reopened.Resume();
            Require(reopened.State?.Preferences.StartWithWindows == true && platform.Registered == platform.CurrentCommand,
                "An existing preferences object without the startup setting keeps sign-in enabled.");
            Require(reopened.State?.Preferences.KeepWindowsTrayVisible == true && !platform.AutoHide &&
                journal.Load()?.OriginalAutoHide is null,
                "A legacy profile restores its previously visible Windows taskbar and releases its auto-hide ownership.");
            reopened.Restore();
        }
        finally
        {
            journal.Clear();
            directory.Delete();
        }
    }

    private static void SavedPreferences()
    {
        var directory = Directory.CreateTempSubdirectory("11toXP-preferences-");
        var journal = new ThemeJournal(directory.FullName);
        var store = new ThemePreferenceStore(directory.FullName);
        var path = Path.Combine(directory.FullName, "preferences.json");
        try
        {
            var platform = new FakePlatform();
            var app = new ThemeCoordinator(journal, platform, store);
            var options = ThemePreferences.Default with
            {
                StartWithWindows = false,
                Taskbar = TaskbarOptions.Default with
                {
                    OrderedQuickLaunch = [new(null, @"C:\Tools\Pinned.lnk"), new(QuickLaunchItem.FileExplorer, null)]
                }
            };
            app.Apply(Image, options);
            app.Restore();
            var saved = new ThemePreferenceStore(directory.FullName).Load();
            Require(!saved.StartWithWindows && saved.Taskbar.HasSameQuickLaunch(options.Taskbar) &&
                !File.ReadAllText(path).Contains("Selected.jpg") && journal.Load() is null,
                "Startup choice and Quick Launch order survive Restore without retaining wallpaper.");
            app.Apply(null, saved);
            File.WriteAllText(path, "malformed");
            Require(store.Load().StartWithWindows, "A malformed preference file falls back to defaults.");
            var reopened = new ThemeCoordinator(journal, platform, store);
            reopened.Load();
            reopened.Resume();
            Require(journal.Load() is null && !platform.Shell, "A malformed preference file does not block journal recovery.");
        }
        finally
        {
            journal.Clear();
            File.Delete(path);
            directory.Delete();
        }
    }

    private static void PinnedStartMenuLifecycle(ThemeCoordinator app, ThemeJournal journal, FakePlatform platform)
    {
        var empty = TaskbarOptions.Default;
        Require(empty.HasSameLayout(empty with { StartMenuPins = [] }),
            "Older taskbar settings without pins match an empty pin list.");
        var first = new QuickLaunchEntry(null, @"C:\Tools\First.lnk");
        var second = new QuickLaunchEntry(null, @"C:\Tools\Second.lnk");
        var pins = new[] { first, second };
        var options = empty with { StartMenuPins = pins };
        var copy = options.CheckedCopy();
        Array.Reverse(pins);
        Require(copy.StartMenuPins!.SequenceEqual([first, second]) && !copy.HasSameLayout(options) &&
            copy.HasSameQuickLaunch(options), "Pin order is independent of Quick Launch and cloned on validation.");
        Require(copy.HasSameLayout(copy with
        {
            StartMenuPins = [first with { ShortcutPath = @"c:\tools\FIRST.LNK" }, second]
        }), "Equivalent pin paths compare case-insensitively.");
        ExpectInvalidData(() => (options with { StartMenuPins = [first, first] }).CheckedCopy());
        ExpectInvalidData(() => (options with { StartMenuPins = [new(null, @"\\Server\Tools\Remote.lnk")] }).CheckedCopy());
        ExpectInvalidData(() => (options with { StartMenuPins = [new(QuickLaunchItem.Settings, null)] }).CheckedCopy());
        ExpectInvalidData(() => (options with
        {
            StartMenuPins = Enumerable.Range(0, TaskbarOptions.MaximumStartMenuPins + 1)
                .Select(index => new QuickLaunchEntry(null, $@"C:\Tools\Pin{index}.lnk")).ToArray()
        }).CheckedCopy());

        var baseline = ThemePreferences.Default with { StartWithWindows = false };
        var candidate = baseline with { Taskbar = copy };
        app.Apply(null, baseline);
        Require(app.NeedsTaskbarPreview(candidate), "Adding Start menu pins goes through the layout countdown.");
        app.BeginTaskbarPreview(candidate);
        Require(platform.Options!.HasSameLayout(copy), "The preview shows candidate pins.");
        app.CancelTaskbarPreview();
        Require(journal.Load()!.Preferences.Taskbar.HasSameLayout(empty) && platform.Options!.HasSameLayout(empty),
            "Undo retains the previously committed Start menu.");
        app.BeginTaskbarPreview(candidate);
        app.CommitTaskbarPreview(() => app.Repair(null, candidate));
        Require(journal.Load()!.Preferences.Taskbar.HasSameLayout(copy) && platform.Options!.HasSameLayout(copy) &&
            platform.WallpaperCalls == 0 && platform.Registered is null,
            "Keeping the preview saves pin order without changing wallpaper or startup.");
        app.Restore();
    }

    private static void AppearanceProfilePersistence()
    {
        var directory = Directory.CreateTempSubdirectory("11toXP-profiles-");
        var path = Path.Combine(directory.FullName, "appearance-profiles.json");
        try
        {
            var store = new AppearanceProfileStore(directory.FullName);
            Require(store.Load().Length == 0 && !File.Exists(path), "Reading an empty profile store creates no state.");
            var pins = new QuickLaunchEntry[] { new(null, @"C:\Tools\First.lnk") };
            var selected = ThemePreferences.Default with
            {
                Variant = LunaVariant.Silver,
                Appearance = AppearanceComponents.Cursors,
                KeepWindowsTrayVisible = false,
                Taskbar = TaskbarOptions.Default with { StartMenuPins = pins, ButtonSize = TaskbarButtonSize.Small }
            };
            var profile = AppearanceProfile.Capture("  Evening work  ", selected);
            pins[0] = new QuickLaunchEntry(null, @"C:\Tools\Other.lnk");
            store.Save([profile]);
            var loaded = new AppearanceProfileStore(directory.FullName).Load().Single();
            var applied = loaded.ToPreferences(startWithWindows: false);
            var json = File.ReadAllText(path);
            Require(loaded.Name == "Evening work" && applied is
                { Variant: LunaVariant.Silver, Appearance: AppearanceComponents.Cursors,
                  StartWithWindows: false, KeepWindowsTrayVisible: false } &&
                applied.Taskbar.ButtonSize == TaskbarButtonSize.Small &&
                applied.Taskbar.StartMenuPins!.Single().ShortcutPath == @"C:\Tools\First.lnk" &&
                !json.Contains("StartWithWindows", StringComparison.Ordinal) &&
                !json.Contains("Wallpaper", StringComparison.Ordinal),
                "Profiles clone and persist appearance while preserving the caller's sign-in choice and excluding wallpaper.");
            ExpectInvalidData(() => store.Save([profile, profile with { Name = "evening work" }]));
            ExpectInvalidData(() => store.Save(Enumerable.Repeat(profile, AppearanceProfileStore.MaximumProfiles + 1).ToArray()));
            Require(File.ReadAllText(path) == json, "An invalid profile save leaves the previous store intact.");
            store.Save([]);
            Require(store.Load().Length == 0 && !File.Exists(path), "Deleting the last profile removes the store.");
            File.WriteAllText(path, "{ malformed }");
            ExpectInvalidData(() => store.Load());
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private static void ExpectInvalidData(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid appearance data was accepted.");
    }

    private static void WithSession(Action<ThemeCoordinator, ThemeJournal, FakePlatform> test)
    {
        var directory = Directory.CreateTempSubdirectory("11toXP-check-");
        var journal = new ThemeJournal(directory.FullName);
        try
        {
            var platform = new FakePlatform();
            test(new ThemeCoordinator(journal, platform), journal, platform);
        }
        finally
        {
            journal.Clear();
            journal.ClearPreviewTrayRollback();
            directory.Delete();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("The injected failure was not reported.");
    }

    private sealed class FakePlatform : IThemePlatform
    {
        public const string Original = @"C:\Original.jpg";
        public int Build { get; set; } = 26100;
        public bool AutoHide { get; private set; }
        public bool Shell { get; private set; }
        public bool TrayRevealAllowed { get; private set; }
        public bool TrayThumbnailShown { get; private set; }
        public TaskbarOptions? Options { get; private set; }
        public LunaVariant? FailingShellVariant { get; set; }
        public int ConnectedDisplays { get; set; } = 1;
        public bool HasMatchingDisplay { get; set; } = true;
        public bool ShellWasStartedBeforeAutoHide { get; private set; }
        public int ShellCalls { get; private set; }
        public int AutoHideReads { get; private set; }
        public int AutoHideWrites { get; private set; }
        public bool AutoHideAvailable { get; set; } = true;
        public bool AutoHideReadbackUnconfirmed { get; set; }
        public bool? FailingAutoHideTarget { get; set; }
        public Action<bool>? BeforeSetAutoHide { get; set; }
        public LunaVariant Variant { get; private set; }
        public int Captures { get; private set; }
        public int WallpaperCalls { get; private set; }
        public bool FailWallpaperApply { get; set; }
        public bool FailWallpaperRestore { get; set; }
        public string? WallpaperRestoreError { get; set; }
        public bool FailAppearanceRestore { get; set; }
        public List<AppearanceComponents> AppearanceCaptures { get; } = [];
        public List<AppearanceComponents> AppearanceApplies { get; } = [];
        public List<AppearanceComponents> AppearanceRestores { get; } = [];
        public string Wallpaper { get; private set; } = Original;
        public string? Registered { get; set; }
        public string? StartupRemovalFailure { get; set; }
        public bool FailStartupRegistration { get; set; }
        public int StartupCommandReads { get; private set; }
        public string CurrentCommand { get; set; } = "\"C:\\Tools\\11toXP.exe\" --background";

        public string StartupCommand()
        {
            StartupCommandReads++;
            return CurrentCommand;
        }
        public bool HasStartup(string command) => Registered == command;
        public void EnsureStartup(string command, string? previous)
        {
            if (FailStartupRegistration)
                throw new InvalidOperationException("Injected startup registration failure.");
            if (Registered is not null && Registered != command && Registered != previous)
                throw new InvalidOperationException("Collision.");
            Registered = command;
        }
        public void RemoveStartup(string command)
        {
            if (command == StartupRemovalFailure)
                throw new InvalidOperationException("Injected startup removal failure.");
            if (Registered == command)
                Registered = null;
        }
        public bool? ReadAutoHide()
        {
            AutoHideReads++;
            return AutoHideAvailable ? AutoHide : null;
        }
        public bool SetAutoHide(bool enabled)
        {
            AutoHideWrites++;
            BeforeSetAutoHide?.Invoke(enabled);
            if (enabled == FailingAutoHideTarget)
                return false;
            if (enabled)
                ShellWasStartedBeforeAutoHide = Shell;
            AutoHide = enabled;
            return !AutoHideReadbackUnconfirmed;
        }
        public WallpaperPlan CaptureWallpaper(string image)
        {
            Captures++;
            WallpaperCalls++;
            return new WallpaperPlan(image, [new("Test monitor", Wallpaper)]);
        }
        public void ApplyWallpaper(WallpaperPlan plan)
        {
            WallpaperCalls++;
            Wallpaper = plan.Requested;
            if (FailWallpaperApply)
                throw new InvalidOperationException("Injected wallpaper Apply failure.");
        }
        public void RestoreWallpaper(WallpaperPlan plan)
        {
            WallpaperCalls++;
            if (FailWallpaperRestore)
                throw new InvalidOperationException("Injected wallpaper Restore failure.");
            if (WallpaperRestoreError is { } error)
                throw new InvalidOperationException(error);
            Wallpaper = plan.Monitors[0].Original;
        }
        public AppearanceSnapshot CaptureAppearance(AppearanceComponents components, AppearanceSnapshot? retained)
        {
            AppearanceCaptures.Add(components);
            return retained ?? new AppearanceSnapshot(1, new AppearanceValue?[16]);
        }
        public void ValidateAppearance(AppearanceSnapshot baseline) { }
        public void ApplyAppearance(AppearanceSnapshot baseline, AppearanceComponents components) =>
            AppearanceApplies.Add(components);
        public void RestoreAppearance(AppearanceSnapshot baseline, AppearanceComponents components)
        {
            if (FailAppearanceRestore)
                throw new InvalidOperationException("Injected appearance Restore failure.");
            AppearanceRestores.Add(components);
        }
        public TaskbarAvailability InspectTaskbar(TaskbarOptions options) =>
            new(HasMatchingDisplay, HasMatchingDisplay && ConnectedDisplays == 1);

        public void StartShell(LunaVariant variant, TaskbarOptions options, bool allowTrayReveal, bool showTrayThumbnail = false)
        {
            ShellCalls++;
            if (FailingShellVariant == variant)
                throw new InvalidOperationException("Injected shell start failure.");
            Shell = HasMatchingDisplay;
            TrayRevealAllowed = Shell && allowTrayReveal;
            TrayThumbnailShown = Shell && showTrayThumbnail;
            Variant = variant;
            Options = options;
        }
        public void StopShell()
        {
            Shell = false;
            TrayRevealAllowed = false;
            TrayThumbnailShown = false;
        }
    }
}
