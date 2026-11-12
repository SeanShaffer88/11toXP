# 11toXP

11toXP is a per-user Windows 11 utility that recreates the Windows XP Luna desktop shell. The Windows x64 deliverable is one self-contained executable with no separately installed runtime or theme tools.

| Area | Contract |
| --- | --- |
| Shell | The app supplies a Luna taskbar, green Start button, two-column Start menu, Small and Large taskbar sizes, optional task labels and grouping, configurable Quick Launch, and a clock. Icon-only task buttons keep their accessible names. Unlocking the taskbar exposes a mouse and keyboard resize grip that stages the same confirmation as a layout change in settings. Quick Launch accepts local Windows shortcut files and asks Windows to open them only when selected for launch. Shell menus provide access to the stock Windows 11 taskbar. |
| Displays | Follow the Windows primary display by default. In explicit mode, place a native appbar on each connected selected display. Keep disconnected choices saved for reconnect. Multiple connected displays leave global native taskbar auto-hide to Windows and restore any earlier 11toXP-owned auto-hide setting. |
| Taskbar preview | Stage taskbar changes in appbar windows during a 15-second monotonic confirmation window. On a single display, previewing **Keep Windows tray visible** can temporarily turn off native auto-hide so the actual tray icons appear. Record the original auto-hide state before changing it. Keep restores this temporary state before committing via Apply or Repair. Undo, Esc, close, or timeout restores the previous taskbar layout and native tray state. Startup recovers an interrupted tray preview. A confirmed grip resize updates only taskbar size. |
| Windows tray | **Keep Windows tray visible** defaults to checked, including for older preferences that lack the option. On a single display with the XP taskbar active, keep the Windows taskbar visible beneath it so Windows continues to handle its tray icons and overflow. Uncheck the option and select **Repair** to request the XP-only layout. **Restore** returns the original Windows auto-hide setting. |
| Temporary tray reveal | In XP-only mode on a verified Windows build, **Show Windows Tray** reveals the native tray below the XP bar and **Hide Windows Tray** returns to XP-only mode. The XP bar stays active. The reveal is session state, while the existing journal retains the original auto-hide setting for recovery. Repair, Restore, pausing, and restarting 11toXP end the reveal. |
| Appearance | Luna Blue, Silver, and Olive Green change app-rendered surfaces. Independent controls manage the taskbar, original XP-inspired icons, cursors, and sounds. The app records each newly enabled component's baseline before making changes. |
| XP Explorer | Browse local folders in the same executable through a folder tree, task pane, toolbar, and address bar. Windows opens files and handles file operations through native Explorer. |
| Apply | Save and flush the original values before setting any persistent Windows preferences. **Start with Windows** defaults to enabled, including for older journals. Apply and Repair register the named per-user startup entry when enabled and remove the owned entry when disabled. |
| Repair | Recreate the shell and reapply the selected appearance using the original baseline. Explorer and display notifications refresh the shell. |
| Restore | Close the shell, restore owned settings and remove startup registration. Preserve recovery data until the restore succeeds. With **Start with Windows** off, attempt restoration at sign-out and retry an interrupted restoration on the next launch. Restore and Exit shuts down only after a successful restore. Keep saved non-wallpaper preferences in a separate guarded file after restoration. |
| Wallpaper | Default to off. Apply, Repair, Restore, and startup must never read or write wallpaper for a session that never opted in. When selected, use the chosen image only if the original wallpaper configuration can be restored. |
| Status | Display shell, appearance, wallpaper, startup and recovery state. Surface incompatible or failed components in the control window. |
| Diagnostics | On explicit export, create a new local JSON file at the chosen destination. Refuse existing destinations and files in the app settings folder. Include the Windows build, current control-window display scaling, configured component flags from the active recovery journal, and the latest recovery error category and type for the session. Exclude private paths, filenames, and usernames. Collect no telemetry. |
| Compatibility | Use documented Windows APIs for integration. Keep native shell access available when a component fails. Use original artwork and sound assets. |

## Verification

- Build with the configured .NET 10 SDK and inspect the published executable's architecture and SHA-256.
- Exercise apply, repair, restore, repeated operations, and failure recovery using isolated fixtures. Assert zero wallpaper calls when the option was never enabled.
- Verify selective capture, apply, retry, restore, and resumption. Keep active schema-1 journals from existing versions recoverable.
- Verify the default tray setting for older preferences, native tray interaction, XP-only layout on Repair, and restoration of the original auto-hide setting.
- Verify keyboard access to the temporary tray toggle, coexistence with the active XP bar, and recovery after Explorer restarts or 11toXP exits unexpectedly during a reveal.
- Verify Keep, explicit Undo, and timeout for taskbar preview. Verify that the real tray appears when requested, that the committed journal and saved preferences are preserved until Keep, and that cancellation and interrupted-preview recovery restore native auto-hide.
- Verify default display following, explicit selection and disconnected identity resolution. Distinguish simulated display changes from checks on physical hardware.
- Verify that session-only mode removes only the owned startup entry and restores after an interrupted sign-out. Verify local shortcut ordering and the diagnostic export field allowlist.
- Verify the packaged control window and shell against the available interactive Windows desktop. Record tested and untested scenarios separately.
- Require independent review of the final diff and evidence before integration.

## References

- [Windows appbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars)
- [Taskbar state and restart notifications](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)
- [Desktop wallpaper API](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper)
