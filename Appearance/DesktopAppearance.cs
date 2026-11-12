using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace LunaDesktop.Appearance;

internal sealed record AppearanceValue(string Data, RegistryValueKind Kind);

internal sealed record AppearanceSnapshot(int Version, AppearanceValue?[] Values);

internal sealed class DesktopAppearance(string stateDirectory)
{
    private const int SnapshotVersion = 1;
    private const string CursorKey = @"Control Panel\Cursors";
    private const string SoundsKey = @"AppEvents\Schemes";
    private const string IconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID";
    private const uint SpiSetCursors = 0x0057;
    private const int ShcneAssocChanged = 0x08000000;

    // These existing Windows 11 per-user values correspond to the desktop icon,
    // pointer and program-event entries described by Microsoft's theme format.
    // https://learn.microsoft.com/windows/win32/controls/themesfileformat-overview
    private static readonly Setting[] Settings =
    [
        new(CursorKey, "Arrow", "Arrow.cur", Category.Cursor),
        new(CursorKey, "Hand", "Hand.cur", Category.Cursor),
        new(CursorKey, "Wait", "Wait.cur", Category.Cursor),
        new(CursorKey, "AppStarting", "AppStarting.cur", Category.Cursor),
        Icon("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "", "Computer.ico"),
        Icon("{59031A47-3F72-44A7-89C5-5595FE6B30EE}", "", "Documents.ico"),
        Icon("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "", "Network.ico"),
        Icon("{645FF040-5081-101B-9F08-00AA002F954E}", "", "BinEmpty.ico"),
        Icon("{645FF040-5081-101B-9F08-00AA002F954E}", "empty", "BinEmpty.ico"),
        Icon("{645FF040-5081-101B-9F08-00AA002F954E}", "full", "BinFull.ico"),
        Sound("SystemAsterisk", "Information.wav"),
        Sound("SystemExclamation", "Warning.wav"),
        Sound("SystemHand", "Error.wav"),
        Sound("SystemQuestion", "Question.wav"),
        Sound("SystemNotification", "Notification.wav"),
        Sound("Notification.Default", "Notification.wav")
    ];

    private readonly string _assetDirectory = Path.Combine(stateDirectory, "Appearance-v1");

    public AppearanceSnapshot Capture()
    {
        var useCursors = CanUseCursors();
        var soundsMuted = SoundsMuted();
        var values = new AppearanceValue?[Settings.Length];
        for (var index = 0; index < Settings.Length; index++)
        {
            var setting = Settings[index];
            using var key = Registry.CurrentUser.OpenSubKey(setting.Key);
            var value = Read(key, setting.Name);
            if (IsOurValue(setting, value))
                throw new InvalidDataException("An 11toXP appearance setting has no saved original value.");

            if (setting.Category == Category.Cursor && !useCursors ||
                setting.Category == Category.Sound && (soundsMuted || string.IsNullOrEmpty(value?.Data)))
                continue;

            values[index] = value;
        }
        return new AppearanceSnapshot(SnapshotVersion, values);
    }

    public void Validate(AppearanceSnapshot baseline)
    {
        if (baseline is null || baseline.Version != SnapshotVersion ||
            baseline.Values is null || baseline.Values.Length != Settings.Length)
            throw new InvalidDataException("The saved 11toXP appearance settings have an unsupported format.");

        for (var index = 0; index < Settings.Length; index++)
        {
            var value = baseline.Values[index];
            if (value is not null &&
                (value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) ||
                 value.Data is null || value.Data.Length > 32767 || value.Data.Contains('\0') ||
                 IsOurValue(Settings[index], value)))
                throw new InvalidDataException("A saved 11toXP appearance setting is invalid.");
        }
    }

    public void Apply(AppearanceSnapshot baseline)
    {
        Validate(baseline);
        var useCursors = CanUseCursors();
        var soundsMuted = SoundsMuted();
        var refreshCursors = false;
        var refreshIcons = false;
        var preparedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Settings.Length; index++)
        {
            var setting = Settings[index];
            if (baseline.Values[index] is null ||
                setting.Category == Category.Cursor && !useCursors ||
                setting.Category == Category.Sound && soundsMuted)
                continue;

            using var key = Registry.CurrentUser.OpenSubKey(setting.Key, writable: true);
            var current = Read(key, setting.Name);
            if (key is null || current is null ||
                setting.Category == Category.Sound && current.Data.Length == 0)
                continue;

            if (preparedAssets.Add(setting.Asset))
                AppearanceAssets.Ensure(_assetDirectory, setting.Asset);
            if (!IsOurValue(setting, current))
                key.SetValue(setting.Name, AssetPath(setting), RegistryValueKind.String);

            // A previous attempt may have been interrupted after writing values.
            refreshCursors |= setting.Category == Category.Cursor;
            refreshIcons |= setting.Category == Category.Icon;
        }
        Refresh(refreshCursors, refreshIcons);
    }

    public void Restore(AppearanceSnapshot baseline)
    {
        Validate(baseline);
        var remainingAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refreshCursors = false;
        var refreshIcons = false;
        for (var index = 0; index < Settings.Length; index++)
        {
            var setting = Settings[index];
            var original = baseline.Values[index];
            using var key = Registry.CurrentUser.OpenSubKey(setting.Key, writable: original is not null);
            if (IsOurValue(setting, Read(key, setting.Name)))
            {
                if (original is null)
                    remainingAssets.Add(setting.Asset);
                else
                    key!.SetValue(setting.Name, original.Data, original.Kind);
            }

            // Repeat the refresh after interrupted restore attempts as well.
            refreshCursors |= original is not null && setting.Category == Category.Cursor;
            refreshIcons |= original is not null && setting.Category == Category.Icon;
        }
        Refresh(refreshCursors, refreshIcons);

        var assets = Settings.Select(setting => setting.Asset).Distinct(StringComparer.OrdinalIgnoreCase);
        AppearanceAssets.Remove(_assetDirectory, assets.Except(remainingAssets, StringComparer.OrdinalIgnoreCase));
    }

    private static bool CanUseCursors()
    {
        using var cursors = Registry.CurrentUser.OpenSubKey(CursorKey);
        using var accessibility = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        return !SystemParameters.HighContrast &&
            cursors?.GetValue("CursorBaseSize") is null or 32 &&
            accessibility?.GetValue("CursorType") is null or 0;
    }

    private static bool SoundsMuted()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SoundsKey);
        return string.Equals(key?.GetValue("") as string, ".None", StringComparison.OrdinalIgnoreCase);
    }

    private static AppearanceValue? Read(RegistryKey? key, string name)
    {
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string data)
            return null;

        var kind = key.GetValueKind(name);
        return kind is RegistryValueKind.String or RegistryValueKind.ExpandString
            ? new AppearanceValue(data, kind)
            : null;
    }

    private static Setting Icon(string clsid, string name, string asset) =>
        new($@"{IconsKey}\{clsid}\DefaultIcon", name, asset, Category.Icon);

    private static Setting Sound(string eventName, string asset) =>
        new($@"{SoundsKey}\Apps\.Default\{eventName}\.Current", "", asset, Category.Sound);

    private string AssetPath(Setting setting)
    {
        var path = Path.Combine(_assetDirectory, setting.Asset);
        return setting.Category == Category.Icon ? path + ",0" : path;
    }

    private bool IsOurValue(Setting setting, AppearanceValue? value) =>
        value is not null &&
        string.Equals(value.Data, AssetPath(setting), StringComparison.OrdinalIgnoreCase);

    private static void Refresh(bool cursors, bool icons)
    {
        // SHCNE_ASSOCCHANGED invalidates the Shell's icon and thumbnail cache.
        // https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify
        if (icons)
            SHChangeNotify(ShcneAssocChanged, 0, 0, 0);

        // SPI_SETCURSORS reloads the active pointer definitions.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfow
        if (cursors && !SystemParametersInfoW(SpiSetCursors, 0, 0, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not reload the cursor settings.");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, nint value, uint flags);

    private enum Category { Cursor, Icon, Sound }

    private sealed record Setting(string Key, string Name, string Asset, Category Category);
}
