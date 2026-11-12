using System.IO;
using System.IO.Enumeration;

namespace LunaDesktop.Browser;

internal enum BrowserLocationKind { KnownFolder, Drive }

internal sealed record BrowserLocation(string Name, string Path, BrowserLocationKind Kind);

internal sealed record BrowserEntry(string Name, string Path, bool Directory, bool Link, long Length, DateTime Modified)
{
    public string Kind => Link ? "Link" : Directory ? "File Folder" : "File";
    public string SizeText => Directory ? "" : $"{Math.Max(1, (Length + 1023) / 1024):N0} KB";
    public string ModifiedText => Modified.ToString("g");
}

internal sealed record FolderListing(IReadOnlyList<BrowserEntry> Entries, bool Limited);

internal static class FolderReader
{
    private const int FolderLimit = 256;
    private const int DetailLimit = 4000;
    private const int ExaminationLimit = 16000;

    internal static IReadOnlyList<BrowserLocation> Roots(CancellationToken token)
    {
        var result = new List<BrowserLocation>();
        AddKnownFolder("My Documents", Environment.SpecialFolder.MyDocuments);
        AddKnownFolder("My Pictures", Environment.SpecialFolder.MyPictures);
        AddKnownFolder("My Music", Environment.SpecialFolder.MyMusic);
        AddKnownFolder("Desktop", Environment.SpecialFolder.DesktopDirectory);

        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (CanReadDrive(drive) && drive.IsReady)
                    result.Add(new BrowserLocation($"Local Drive ({drive.Name[..2]})", drive.Name, BrowserLocationKind.Drive));
            }
            catch (Exception error) when (IsFolderError(error)) { }
        }
        return result;

        void AddKnownFolder(string label, Environment.SpecialFolder specialFolder)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var candidate = Environment.GetFolderPath(specialFolder, Environment.SpecialFolderOption.DoNotVerify);
                var path = ValidateDirectory(candidate, token);
                result.Add(new BrowserLocation(label, path, BrowserLocationKind.KnownFolder));
            }
            catch (Exception error) when (IsFolderError(error)) { }
        }
    }

    internal static string ValidateDirectory(string path, CancellationToken token)
    {
        var fullPath = GetLocalPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var drive = new DriveInfo(root);
        token.ThrowIfCancellationRequested();
        if (!CanReadDrive(drive) || !drive.IsReady)
            throw new IOException("Choose a ready local drive, or open the location in Windows Explorer.");

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Use Windows Explorer to browse links and redirected folders.");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("Enter the path to a folder.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    internal static FolderListing Read(string path, bool foldersOnly, CancellationToken token)
    {
        path = ValidateDirectory(path, token);
        var files = new FileSystemEnumerable<BrowserEntry>(path,
            (ref FileSystemEntry entry) => new BrowserEntry(entry.FileName.ToString(), entry.ToFullPath(), entry.IsDirectory,
                (entry.Attributes & FileAttributes.ReparsePoint) != 0, entry.IsDirectory ? 0 : entry.Length,
                entry.LastWriteTimeUtc.LocalDateTime),
            new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = false });
        var entries = new List<BrowserEntry>();
        var limit = foldersOnly ? FolderLimit : DetailLimit;
        var examined = 0;
        var limited = false;
        foreach (var item in files)
        {
            token.ThrowIfCancellationRequested();
            if (++examined > ExaminationLimit)
            {
                limited = true;
                break;
            }
            if (foldersOnly && (!item.Directory || item.Link))
                continue;
            if (entries.Count == limit)
            {
                limited = true;
                break;
            }
            entries.Add(item);
        }

        entries.Sort((left, right) => left.Directory == right.Directory
            ? StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
            : right.Directory.CompareTo(left.Directory));
        return new FolderListing(entries, limited);
    }

    internal static string GetLocalPath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            path.IndexOf(':', 2) >= 0)
            throw new IOException("Enter a local drive path such as C:\\. Use Windows Explorer for other locations.");
        return Path.GetFullPath(path);
    }

    internal static bool IsFolderError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static bool CanReadDrive(DriveInfo drive) =>
        drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;
}
