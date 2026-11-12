using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LunaDesktop.Shell;

internal sealed record TaskIdentity(string Key, string Label)
{
    private const uint QueryLimitedInformation = 0x1000;
    private const ushort VtLpwstr = 31;
    private static readonly Guid StoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };

    internal static TaskIdentity? Read(nint window, uint process)
    {
        string? explicitId = ReadWindowAppId(window);
        if (explicitId is not null)
            return FromAppId(explicitId, null);

        using SafeProcessHandle handle = OpenProcess(QueryLimitedInformation, false, process);
        if (handle.IsInvalid)
            return null;

        string? path = ReadImagePath(handle);
        string? appId = ReadProcessAppId(handle);
        if (appId is not null)
            return FromAppId(appId, path is null ? null : Path.GetFileNameWithoutExtension(path));
        if (path is null)
            return null;

        string name = Path.GetFileNameWithoutExtension(path);
        // These hosts can carry windows for several unrelated applications.
        if (name.ToUpperInvariant() is "APPLICATIONFRAMEHOST" or "RUNDLL32" or "DLLHOST" or "WWAHOST")
            return null;
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            name = "File Explorer";
        return new TaskIdentity("image:" + path.ToUpperInvariant(), name);
    }

    private static TaskIdentity FromAppId(string appId, string? executableName)
    {
        int separator = appId.LastIndexOf('!');
        string label = separator >= 0 && separator < appId.Length - 1 ? appId[(separator + 1)..] : appId;
        if (label.Equals("App", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(executableName))
            label = executableName;
        return new TaskIdentity("appid:" + appId.ToUpperInvariant(), label);
    }

    private static string? ReadWindowAppId(nint window)
    {
        if (SHGetPropertyStoreForWindow(window, StoreId, out IPropertyStore? store) < 0 || store is null)
            return null;
        try
        {
            int result = store.GetValue(AppIdKey, out PropVariant value);
            try
            {
                return result >= 0 && value.Type == VtLpwstr && value.String != 0
                    ? ReadAppId(Marshal.PtrToStringUni(value.String))
                    : null;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static string? ReadImagePath(SafeProcessHandle handle)
    {
        const int InsufficientBuffer = 122;
        for (int capacity = 512; capacity <= 32768; capacity *= 2)
        {
            var path = new StringBuilder(capacity);
            int size = capacity;
            if (QueryFullProcessImageNameW(handle, 0, path, ref size))
                return path.ToString();
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer)
                break;
        }
        return null;
    }

    private static string? ReadProcessAppId(SafeProcessHandle handle)
    {
        var appId = new StringBuilder(256);
        int size = appId.Capacity;
        return GetApplicationUserModelId(handle, ref size, appId) == 0 ? ReadAppId(appId.ToString()) : null;
    }

    private static string? ReadAppId(string? value) =>
        value is { Length: > 0 and <= 255 } && !string.IsNullOrWhiteSpace(value) ? value : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint String;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(nint window, in Guid id,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref int size, StringBuilder appId);
}
