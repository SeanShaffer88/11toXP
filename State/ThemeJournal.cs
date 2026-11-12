using System.IO;
using System.Text.Json;
using LunaDesktop.Appearance;

namespace LunaDesktop.State;

internal enum ThemePhase { Applying, Active, Restoring }

internal sealed class ThemeState
{
    public int Schema { get; set; } = 1;
    public ThemePhase Phase { get; set; }
    public bool? OriginalAutoHide { get; set; }
    public int AutoHideBuild { get; set; }
    public string? StartupCommand { get; set; }
    public string? PreviousStartupCommand { get; set; }
    public WallpaperPlan? Wallpaper { get; set; }
    public AppearanceSnapshot? Appearance { get; set; }
}

internal sealed class ThemeJournal(string directory)
{
    private readonly string path = Path.Combine(directory, "theme-state.json");

    public ThemeState? Load()
    {
        try
        {
            StateFiles.Guard(directory, directory: true);
            StateFiles.Guard(path, directory: false);
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024 * 1024)
                throw new InvalidDataException("The restore record exceeds its size limit.");
            var state = JsonSerializer.Deserialize<ThemeState>(stream);
            if (state is null || state.Schema != 1 || !Enum.IsDefined(state.Phase))
                throw new InvalidDataException("The restore record format is unsupported.");
            return state;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Save(ThemeState state) => StateFiles.WriteAtomically(directory, path, stream => JsonSerializer.Serialize(stream, state));

    public void Clear()
    {
        StateFiles.Guard(directory, directory: true);
        StateFiles.Guard(path, directory: false);
        File.Delete(path);
    }
}
