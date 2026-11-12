# 11toXP

11toXP is a per-user Windows 11 utility that recreates the Windows XP Luna desktop shell. The Windows x64 deliverable is one self-contained executable with no separately installed runtime or theme tools.

| Area | Contract |
| --- | --- |
| Shell | The app supplies a Luna taskbar, green Start button, two-column Start menu, labeled task buttons, optional grouping, configurable Quick Launch, and a clock. Shell menus provide access to the stock Windows 11 taskbar. Native appbar APIs reserve desktop space. |
| Appearance | Luna Blue, Silver, and Olive Green change app-rendered surfaces. Independent controls manage the taskbar, original XP-inspired icons, cursors, and sounds. The app records each newly enabled component's baseline before making changes. |
| XP Explorer | Browse local folders in the same executable through a folder tree, task pane, toolbar, and address bar. Windows opens files and handles file operations through native Explorer. |
| Apply | Save and flush the original values before setting any persistent Windows preferences. Use a named per-user startup entry for the same executable so the theme can resume after sign-in. |
| Repair | Recreate the shell and reapply the selected appearance using the original baseline. Explorer and display notifications refresh the shell. |
| Restore | Close the shell, restore owned settings and remove startup registration. Preserve recovery data until the restore succeeds. Restore and Exit shuts down only after a successful restore. |
| Wallpaper | Default to off. Apply, Repair, Restore, and startup must never read or write wallpaper for a session that never opted in. When selected, use the chosen image only if the original wallpaper configuration can be restored. |
| Status | Display shell, appearance, wallpaper, startup and recovery state. Surface incompatible or failed components in the control window. |
| Compatibility | Use documented Windows APIs for integration. Keep native shell access available when a component fails. Use original artwork and sound assets. |

## Verification

- Build with the configured .NET 10 SDK and inspect the published executable's architecture and SHA-256.
- Exercise apply, repair, restore, repeated operations, and failure recovery using isolated fixtures. Assert zero wallpaper calls when the option was never enabled.
- Verify selective capture, apply, retry, restore, and resumption. Keep active schema-1 journals from existing versions recoverable.
- Verify the packaged control window and shell against the available interactive Windows desktop. Record tested and untested scenarios separately.
- Require independent review of the final diff and evidence before integration.

## References

- [Windows appbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars)
- [Taskbar state and restart notifications](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)
- [Desktop wallpaper API](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper)
