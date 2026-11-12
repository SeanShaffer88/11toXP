using System.IO;

namespace LunaDesktop.Shell;

internal enum TaskGrouping { Never, Always }

internal enum QuickLaunchItem { FileExplorer, Settings, XpExplorer }

internal sealed record TaskbarOptions(TaskGrouping Grouping, QuickLaunchItem[] QuickLaunch, bool UseXpExplorer)
{
    internal static TaskbarOptions Default => new(TaskGrouping.Never, [QuickLaunchItem.FileExplorer, QuickLaunchItem.Settings], false);

    internal void Validate()
    {
        if (!Enum.IsDefined(Grouping) || QuickLaunch is null || QuickLaunch.Length > 3 ||
            QuickLaunch.Any(item => !Enum.IsDefined(item)) || QuickLaunch.Distinct().Count() != QuickLaunch.Length)
            throw new InvalidDataException("The taskbar options are invalid.");
    }
}
