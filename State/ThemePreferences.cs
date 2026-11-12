using System.IO;
using System.Text.Json;
using LunaDesktop.Appearance;
using LunaDesktop.Shell;

namespace LunaDesktop.State;

internal sealed record ThemePreferences(LunaVariant Variant, bool UseTaskbar, AppearanceComponents Appearance, TaskbarOptions Taskbar)
{
    public bool StartWithWindows { get; init; } = true;

    internal static ThemePreferences Default =>
        new(LunaVariant.Blue, true, AppearanceComponents.All, TaskbarOptions.Default);

    internal ThemePreferences CheckedCopy()
    {
        if (!Enum.IsDefined(Variant) || (Appearance & ~AppearanceComponents.All) != 0 || Taskbar is null)
            throw new InvalidDataException("The appearance options are invalid.");
        return this with { Taskbar = Taskbar.CheckedCopy() };
    }
}

internal sealed class ThemePreferenceStore(string directory)
{
    private readonly string path = Path.Combine(directory, "preferences.json");

    public ThemePreferences Load()
    {
        try
        {
            StateFiles.Guard(directory, directory: true);
            StateFiles.Guard(path, directory: false);
            using var stream = File.OpenRead(path);
            if (stream.Length <= 256 * 1024)
                return JsonSerializer.Deserialize<ThemePreferences>(stream)?.CheckedCopy() ?? ThemePreferences.Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // The restore journal has its own validation and does not depend on this file.
        }
        return ThemePreferences.Default;
    }

    public void Save(ThemePreferences preferences) =>
        StateFiles.WriteAtomically(directory, path, stream => JsonSerializer.Serialize(stream, preferences.CheckedCopy()));
}
