using System.IO;
using LunaDesktop.Appearance;
using LunaDesktop.Shell;

namespace LunaDesktop.State;

internal sealed record ThemePreferences(LunaVariant Variant, bool UseTaskbar, AppearanceComponents Appearance, TaskbarOptions Taskbar)
{
    internal static ThemePreferences Default =>
        new(LunaVariant.Blue, true, AppearanceComponents.All, TaskbarOptions.Default);

    internal ThemePreferences CheckedCopy()
    {
        if (!Enum.IsDefined(Variant) || (Appearance & ~AppearanceComponents.All) != 0 || Taskbar is null)
            throw new InvalidDataException("The appearance options are invalid.");
        Taskbar.Validate();
        return this with { Taskbar = Taskbar with { QuickLaunch = [.. Taskbar.QuickLaunch] } };
    }
}
