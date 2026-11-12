using System.IO;
using System.Text.Json;
using LunaDesktop.Appearance;
using LunaDesktop.Shell;

namespace LunaDesktop.State;

internal sealed record AppearanceProfile(string Name, LunaVariant Variant, bool UseTaskbar,
    AppearanceComponents Appearance, TaskbarOptions Taskbar, bool KeepWindowsTrayVisible)
{
    internal const int MaximumNameLength = 48;

    internal static AppearanceProfile Capture(string name, ThemePreferences preferences) =>
        new AppearanceProfile(name.Trim(), preferences.Variant, preferences.UseTaskbar, preferences.Appearance,
            preferences.Taskbar, preferences.KeepWindowsTrayVisible).CheckedCopy();

    internal ThemePreferences ToPreferences(bool startWithWindows) =>
        new ThemePreferences(Variant, UseTaskbar, Appearance, Taskbar)
        {
            StartWithWindows = startWithWindows,
            KeepWindowsTrayVisible = KeepWindowsTrayVisible
        }.CheckedCopy();

    internal AppearanceProfile CheckedCopy()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaximumNameLength ||
            Name != Name.Trim() || Name.Any(char.IsControl))
            throw new InvalidDataException($"Enter a profile name of 1 to {MaximumNameLength} characters.");
        return this with { Taskbar = ToPreferences(startWithWindows: false).Taskbar };
    }
}

internal sealed class AppearanceProfileStore(string directory)
{
    internal const int MaximumProfiles = 16;
    private const int MaximumBytes = 1024 * 1024;
    private readonly string path = Path.Combine(directory, "appearance-profiles.json");

    internal AppearanceProfile[] Load()
    {
        try
        {
            StateFiles.Guard(directory, directory: true);
            StateFiles.Guard(path, directory: false);
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumBytes)
                throw new InvalidDataException("The appearance profiles file exceeds its size limit.");
            return CheckedCopy(JsonSerializer.Deserialize<AppearanceProfile[]>(stream));
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException error) { throw new InvalidDataException("The appearance profiles file is invalid.", error); }
    }

    internal void Save(AppearanceProfile[] profiles)
    {
        var copy = CheckedCopy(profiles);
        if (copy.Length == 0)
        {
            StateFiles.Guard(directory, directory: true);
            StateFiles.Guard(path, directory: false);
            File.Delete(path);
            return;
        }
        StateFiles.WriteAtomically(directory, path, stream =>
        {
            JsonSerializer.Serialize(stream, copy);
            if (stream.Length > MaximumBytes)
                throw new InvalidDataException("The appearance profiles file exceeds its size limit.");
        });
    }

    private static AppearanceProfile[] CheckedCopy(AppearanceProfile[]? profiles)
    {
        if (profiles is null || profiles.Length > MaximumProfiles)
            throw new InvalidDataException($"Save up to {MaximumProfiles} appearance profiles.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return profiles.Select(profile =>
        {
            if (profile is null || !names.Add(profile.Name))
                throw new InvalidDataException("Appearance profiles need unique names.");
            return profile.CheckedCopy();
        }).ToArray();
    }
}
