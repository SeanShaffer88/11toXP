# Luna Desktop

Luna Desktop is a per-user Windows 11 utility that recreates the Windows XP Luna Blue desktop shell. The Windows x64 deliverable is one self-contained executable with no separately installed runtime or theme tools.

| Area | Contract |
| --- | --- |
| Shell | The app supplies a blue taskbar, green Start button, two-column Start menu, task switching and clock. The stock taskbar remains available. Native appbar APIs reserve desktop space. |
| Apply | Save and flush the original values before setting any persistent Windows preferences. Use a named per-user startup entry for the same executable so the theme can resume after sign-in. |
| Repair | Recreate the shell and reapply the selected appearance using the original baseline. Explorer and display notifications refresh the shell. |
| Restore | Close the shell, restore owned settings and remove startup registration. Preserve recovery data until the restore succeeds. |
| Wallpaper | Default to off. Apply, Repair, Restore, and startup must never read or write wallpaper for a session that never opted in. When selected, use the chosen image only if the original wallpaper configuration can be restored. |
| Compatibility | Use documented Windows APIs for integration. Keep native shell access available when a component fails. Use original artwork and sound assets. |

## Verification

- Build with the configured .NET 10 SDK and inspect the published executable's architecture and SHA-256.
- Exercise apply, repair, restore, repeated operations, and failure recovery using isolated fixtures. Assert zero wallpaper calls when the option was never enabled.
- Verify the packaged control window and shell against the available interactive Windows desktop. Record tested and untested scenarios separately.
- Require independent review of the final diff and evidence before integration.

## References

- [Windows appbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars)
- [Taskbar state and restart notifications](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)
- [Desktop wallpaper API](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper)
