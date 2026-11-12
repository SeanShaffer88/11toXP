using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LunaDesktop.State;

internal static class WindowsSession
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "11toXP.0348C6BA";
    private const uint GetState = 4;
    private const uint GetTaskbarPosition = 5;
    private const uint SetState = 10;
    private const uint AutoHide = 1;
    private const string CommandSuffix = "\" --background";

    public static string StartupCommand()
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !executable.EndsWith("\\11toXP.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Launch 11toXP.exe directly to use automatic sign-in startup.");
        var command = "\"" + executable + CommandSuffix;
        if (command.Length > 260)
            throw new InvalidOperationException("Move 11toXP.exe to a shorter path before applying.");
        return command;
    }

    public static bool IsStartupCommand(string command)
    {
        if (command.Length > 260 || !command.StartsWith('"') ||
            !command.EndsWith(CommandSuffix, StringComparison.Ordinal))
            return false;
        var path = command[1..^CommandSuffix.Length];
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.AsSpan(2).Contains(':') || path.Contains('"') ||
            !path.EndsWith("\\11toXP.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasStartup(string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return Equals(key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), command);
    }

    public static void EnsureStartup(string command, string? previous)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        var current = key.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (Equals(current, command))
            return;
        if (current is not null && !Equals(current, previous))
            throw new InvalidOperationException("Another command is using the 11toXP sign-in entry.");
        key.SetValue(EntryName, command, RegistryValueKind.String);
        if (!Equals(key.GetValue(EntryName), command))
            throw new InvalidOperationException("Windows did not retain the sign-in entry.");
    }

    public static void RemoveStartup(string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (Equals(key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), command))
            key!.DeleteValue(EntryName, throwOnMissingValue: false);
    }

    public static bool? ReadAutoHide()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0)
            return null;
        var data = NewData(taskbar);
        if (SHAppBarMessage(GetTaskbarPosition, ref data) == 0)
            return null;
        data = NewData(taskbar);
        return (SHAppBarMessage(GetState, ref data) & AutoHide) != 0;
    }

    public static bool SetAutoHide(bool enabled)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0)
            return false;
        var data = NewData(taskbar);
        var previous = SHAppBarMessage(GetState, ref data);
        data.Parameter = unchecked((nint)(enabled ? previous | AutoHide : previous & ~AutoHide));
        SHAppBarMessage(SetState, ref data);
        return ReadAutoHide() == enabled;
    }

    private static AppBarData NewData(nint taskbar) => new()
    {
        Size = (uint)Marshal.SizeOf<AppBarData>(),
        Handle = taskbar
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Handle;
        public uint Callback;
        public uint Edge;
        public int Left, Top, Right, Bottom;
        public nint Parameter;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
}
