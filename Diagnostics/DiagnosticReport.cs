using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using LunaDesktop.Appearance;
using LunaDesktop.State;
using Microsoft.Win32;

namespace LunaDesktop.Diagnostics;

internal sealed record RecoveryFailure(string Component, string Kind)
{
    internal static RecoveryFailure From(string component, Exception error) => new(
        component switch
        {
            "XP taskbar" => "xpTaskbar",
            "Windows taskbar" => "windowsTaskbar",
            "Appearance" => "appearance",
            "Wallpaper" => "wallpaper",
            "Sign-in startup" => "signInStartup",
            "Restore journal" => "restoreJournal",
            _ => "unknown"
        },
        error switch
        {
            UnauthorizedAccessException => "accessDenied",
            InvalidDataException => "invalidData",
            IOException => "ioError",
            COMException => "windowsError",
            InvalidOperationException => "operationFailed",
            _ => "unexpectedError"
        });
}

internal static class DiagnosticReport
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static bool Export(Window owner, ThemeCoordinator? coordinator, bool startupFailed)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Diagnostics",
            FileName = "11toXP-diagnostics.json",
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(owner) != true)
            return false;

        var dpi = VisualTreeHelper.GetDpi(owner);
        WriteNew(dialog.FileName, Environment.OSVersion.Version.Build, dpi.DpiScaleX, dpi.DpiScaleY, coordinator, startupFailed);
        return true;
    }

    internal static void WriteNew(string path, int windowsBuild, double scaleX, double scaleY,
        ThemeCoordinator? coordinator, bool startupFailed)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Choose a file for the diagnostics.");
        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "11toXP");
        if (string.Equals(directory, stateDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a folder outside the 11toXP settings folder.");

        try
        {
            StateFiles.WriteAtomically(directory, path,
                stream => Write(stream, windowsBuild, scaleX, scaleY, coordinator, startupFailed), overwrite: false);
        }
        catch (IOException error) when (File.Exists(path))
        {
            throw new IOException("A file with that name already exists. Choose a new name for the diagnostics.", error);
        }
    }

    internal static void Write(Stream stream, int windowsBuild, double scaleX, double scaleY,
        ThemeCoordinator? coordinator, bool startupFailed)
    {
        var state = coordinator?.State;
        var preferences = state?.Preferences;
        var appearance = preferences?.Appearance ?? AppearanceComponents.None;
        var failure = coordinator?.LastRecoveryFailure;
        var report = new
        {
            windowsBuild,
            displayScaling = new { x = scaleX, y = scaleY },
            enabledComponents = new
            {
                xpTaskbar = preferences?.UseTaskbar == true,
                xpExplorer = preferences?.Taskbar.UseXpExplorer == true,
                icons = appearance.HasFlag(AppearanceComponents.Icons),
                cursors = appearance.HasFlag(AppearanceComponents.Cursors),
                sounds = appearance.HasFlag(AppearanceComponents.Sounds),
                wallpaper = state?.WallpaperSelection is not null,
                startWithWindows = preferences?.StartWithWindows == true
            },
            lastRecoveryError = failure ?? (startupFailed ? new RecoveryFailure("startup", "operationFailed") : null)
        };
        JsonSerializer.Serialize(stream, report, Format);
    }
}
