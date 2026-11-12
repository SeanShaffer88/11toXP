using System.IO;

namespace LunaDesktop.State;

internal static class StateFiles
{
    public static void Guard(string path, bool directory)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new InvalidDataException("11toXP state contains a redirected path or an unexpected file type.");
    }

    public static void WriteAtomically(string directory, string path, Action<Stream> write, bool overwrite = true)
    {
        Guard(directory, directory: true);
        Directory.CreateDirectory(directory);
        Guard(directory, directory: true);
        Guard(path, directory: false);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            Guard(directory, directory: true);
            Guard(path, directory: false);
            File.Move(staging, path, overwrite);
        }
        finally
        {
            Guard(directory, directory: true);
            Guard(staging, directory: false);
            File.Delete(staging);
        }
    }
}
