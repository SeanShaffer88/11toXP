using System.IO;
using System.Text.Json.Serialization;

namespace LunaDesktop.Shell;

internal enum TaskGrouping { Never, Always }
internal enum QuickLaunchItem { FileExplorer, Settings, XpExplorer }

internal sealed record QuickLaunchEntry(QuickLaunchItem? BuiltIn, string? ShortcutPath)
{
    [JsonIgnore]
    public string Label => BuiltIn switch
    {
        QuickLaunchItem.FileExplorer => "File Explorer",
        QuickLaunchItem.Settings => "Windows Settings",
        QuickLaunchItem.XpExplorer => "XP Explorer",
        _ => Path.GetFileNameWithoutExtension(ShortcutPath) ?? "Shortcut"
    };

    internal static QuickLaunchEntry FromFile(string path)
    {
        if (!IsCanonicalShortcutPath(path))
            throw new InvalidOperationException("Choose a Windows shortcut (.lnk) on a local drive.");
        RequireAvailableFile(path);
        string fullPath = Path.GetFullPath(path);
        if (!IsCanonicalShortcutPath(fullPath))
            throw new InvalidOperationException("Choose a Windows shortcut (.lnk) on a local drive.");
        return new QuickLaunchEntry(null, fullPath);
    }

    internal static void RequireAvailableFile(string path)
    {
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("Choose a shortcut stored on a local drive.");
        if (!File.Exists(path))
            throw new FileNotFoundException("The Quick Launch shortcut is missing. Remove it or restore the shortcut file.");
    }

    internal bool SameAs(QuickLaunchEntry other) => BuiltIn == other.BuiltIn &&
        string.Equals(ShortcutPath, other.ShortcutPath, StringComparison.OrdinalIgnoreCase);

    internal bool IsValid() => BuiltIn is { } item
        ? Enum.IsDefined(item) && ShortcutPath is null
        : IsCanonicalShortcutPath(ShortcutPath);

    private static bool IsCanonicalShortcutPath(string? path)
    {
        if (path is null || path.Length < 7 || path.Length > 2048 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.AsSpan(3).Contains(':') ||
            !string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            return false;

        var invalid = Path.GetInvalidFileNameChars();
        // Keep journal validation independent of the file system, including DOS short-name resolution.
        return path[3..].Split('\\').All(segment => segment.Length > 0 && !segment.EndsWith('.') &&
            !segment.EndsWith(' ') && segment.IndexOfAny(invalid) < 0 && !IsDeviceName(segment));
    }

    private static bool IsDeviceName(string segment)
    {
        int extension = segment.IndexOf('.');
        string name = (extension < 0 ? segment : segment[..extension]).TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            (name.Length == 4 && (name[..3] is "COM" or "LPT") && "123456789¹²³".Contains(name[3]));
    }

    public override string ToString() => Label;
}

internal sealed record TaskbarOptions(TaskGrouping Grouping, QuickLaunchItem[] QuickLaunch, bool UseXpExplorer)
{
    internal const int MaximumQuickLaunchItems = 12;
    internal static TaskbarOptions Default => new(TaskGrouping.Never, [QuickLaunchItem.FileExplorer, QuickLaunchItem.Settings], false);
    public QuickLaunchEntry[]? OrderedQuickLaunch { get; init; }

    internal IReadOnlyList<QuickLaunchEntry> EffectiveQuickLaunch => OrderedQuickLaunch ??
        QuickLaunch.Select(item => new QuickLaunchEntry(item, null)).ToArray();

    internal TaskbarOptions CheckedCopy()
    {
        Validate();
        return this with { QuickLaunch = [.. QuickLaunch], OrderedQuickLaunch = OrderedQuickLaunch?.ToArray() };
    }

    internal bool HasSameQuickLaunch(TaskbarOptions other)
    {
        var first = EffectiveQuickLaunch;
        var second = other.EffectiveQuickLaunch;
        return first.Count == second.Count && first.Zip(second).All(pair => pair.First.SameAs(pair.Second));
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Grouping) || QuickLaunch is null || QuickLaunch.Length > 3 ||
            QuickLaunch.Any(item => !Enum.IsDefined(item)) || QuickLaunch.Distinct().Count() != QuickLaunch.Length)
            throw new InvalidDataException("The taskbar options are invalid.");
        if (OrderedQuickLaunch is not { } entries)
            return;

        var builtIns = new HashSet<QuickLaunchItem>();
        var shortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entries.Length > MaximumQuickLaunchItems || entries.Any(entry => entry is null || !entry.IsValid() ||
            !(entry.BuiltIn is { } item ? builtIns.Add(item) : shortcuts.Add(entry.ShortcutPath!))))
            throw new InvalidDataException("The Quick Launch entries are invalid.");
    }
}
