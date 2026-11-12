namespace LunaDesktop.Shell;

internal sealed record ShellDisplay(string Id, string Label, bool IsPrimary, ShellNative.Rectangle Bounds,
    ShellNative.Rectangle WorkArea)
{
    internal nint Handle { get; init; }
    private string LogicalName { get; init; } = "";

    internal static ShellDisplay[] Enumerate()
    {
        var result = new List<ShellDisplay>();
        if (!ShellNative.EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = ShellNative.MonitorInfoEx.Create();
            if (ShellNative.GetMonitorInfoW(monitor, ref info) && info.Area.Width > 0 && info.Area.Height > 0 &&
                !string.IsNullOrWhiteSpace(info.DeviceName))
            {
                string logicalName = info.DeviceName;
                var device = ShellNative.DisplayDevice.Create();
                string id = logicalName;
                string label = logicalName;
                // The monitor interface name usually survives display renumbering. Logical names
                // keep virtual or remote displays usable when Windows reports no interface path.
                if (ShellNative.EnumDisplayDevicesW(logicalName, 0, ref device, ShellNative.GetMonitorInterfaceName))
                {
                    if (!string.IsNullOrWhiteSpace(device.Id))
                        id = device.Id;
                    if (!string.IsNullOrWhiteSpace(device.Description))
                        label = $"{device.Description} ({logicalName.Replace(@"\\.\", "", StringComparison.Ordinal)})";
                }
                bool primary = (info.Flags & ShellNative.PrimaryMonitor) != 0;
                result.Add(new ShellDisplay(id, primary ? $"{label} — Primary" : label, primary, info.Area, info.WorkArea)
                {
                    Handle = monitor,
                    LogicalName = logicalName
                });
            }
            return true;
        }, 0))
            throw new InvalidOperationException("Windows could not enumerate its displays.");

        var displays = result.OrderByDescending(display => display.IsPrimary).ThenBy(display => display.Label)
            .DistinctBy(display => display.Bounds).ToArray();
        var duplicateIds = displays.GroupBy(display => display.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return displays
            .Select(display => duplicateIds.Contains(display.Id) ? display with { Id = display.LogicalName } : display)
            .ToArray();
    }

    internal static ShellDisplay[] Resolve(TaskbarOptions options, IReadOnlyList<ShellDisplay> displays) =>
        options.DisplayIds is { } ids
            ? displays.Where(display => ids.Contains(display.Id, StringComparer.OrdinalIgnoreCase)).ToArray()
            : displays.Where(display => display.IsPrimary).Take(1).ToArray();

    internal ShellDisplay? Refresh()
    {
        var info = ShellNative.MonitorInfoEx.Create();
        if (!ShellNative.GetMonitorInfoW(Handle, ref info) ||
            !string.Equals(info.DeviceName, LogicalName, StringComparison.OrdinalIgnoreCase) ||
            info.Area.Width <= 0 || info.Area.Height <= 0)
            return null;
        return this with
        {
            IsPrimary = (info.Flags & ShellNative.PrimaryMonitor) != 0,
            Bounds = info.Area,
            WorkArea = info.WorkArea
        };
    }
}
