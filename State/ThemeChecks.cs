using System.IO;
using LunaDesktop.Appearance;

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
        public int Captures { get; private set; }
        public int WallpaperCalls { get; private set; }
        public bool FailWallpaperApply { get; set; }
        public bool FailWallpaperRestore { get; set; }
        public string Wallpaper { get; private set; } = Original;
        public string? Registered { get; set; }
        public string CurrentCommand { get; set; } = "\"C:\\Tools\\11toXP.exe\" --background";

        public string StartupCommand() => CurrentCommand;
        public bool HasStartup(string command) => Registered == command;
        public void EnsureStartup(string command, string? previous)
        {
            if (Registered is not null && Registered != command && Registered != previous)
                throw new InvalidOperationException("Collision.");
            Registered = command;
        }
        public void RemoveStartup(string command)
        {
            if (Registered == command)
                Registered = null;
        }
        public bool? ReadAutoHide() => AutoHide;
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
            Wallpaper = plan.Monitors[0].Original;
        }
        public AppearanceSnapshot? CaptureAppearance() => null;
        public void ValidateAppearance(AppearanceSnapshot baseline) { }
        public void ApplyAppearance(AppearanceSnapshot baseline) { }
        public void RestoreAppearance(AppearanceSnapshot baseline) { }
        public void StartShell()
        {
            ShellCalls++;
            Shell = true;
        }
        public void StopShell() => Shell = false;
    }
}
