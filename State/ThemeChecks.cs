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
        app.Apply(null);
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
        app.Apply(null);
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
        app.Apply(null);
        Require(journal.Load()?.OriginalAutoHide is null, "An already enabled auto-hide preference is not owned.");
        platform.SetAutoHide(false);
        app.Resume();
        Require(!platform.AutoHide, "Automatic repair preserves a preference we never managed.");
        app.Restore();
        Require(!platform.AutoHide, "Restore preserves a user's subsequent auto-hide change.");
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
            new TaskbarOptions(TaskGrouping.Always, shortcuts, true));
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

            app.Apply(null);
            var path = Path.Combine(directory.FullName, "theme-state.json");
            var record = JsonNode.Parse(File.ReadAllText(path))!;
            record["Preferences"]!.AsObject().Remove("StartWithWindows");
            File.WriteAllText(path, record.ToJsonString());
            platform.Registered = null;
            var reopened = new ThemeCoordinator(journal, platform);
            reopened.Load();
            reopened.Resume();
            Require(reopened.State?.Preferences.StartWithWindows == true && platform.Registered == platform.CurrentCommand,
                "An existing preferences object without the startup setting keeps sign-in enabled.");
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
        public bool ShellWasStartedBeforeAutoHide { get; private set; }
        public int ShellCalls { get; private set; }
        public int AutoHideReads { get; private set; }
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
            return AutoHide;
        }
        public bool SetAutoHide(bool enabled)
        {
            if (enabled)
                ShellWasStartedBeforeAutoHide = Shell;
            AutoHide = enabled;
            return true;
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
        public void StartShell(LunaVariant variant, TaskbarOptions options)
        {
            ShellCalls++;
            Shell = true;
            Variant = variant;
        }
        public void StopShell() => Shell = false;
    }
}
