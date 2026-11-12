# 11toXP

11toXP gives your Windows 11 desktop the Windows XP Luna look, with an XP-style taskbar, Start menu, and optional file browser. One executable contains the app, its artwork, and the .NET runtime.

**Download the Windows 11 x64 beta** from [GitHub Releases](https://github.com/SeanShaffer88/11toXP/releases).

![11toXP control window showing the Luna palettes, appearance options, and recovery controls](docs/images/11toXP-preview.png)

*11toXP running in preview mode.*

## Run

Extract the portable ZIP to a folder you plan to keep, then open `11toXP.exe`. The executable is unsigned and includes its runtime. The ZIP also includes `Notices.txt`.

- **Apply** enables the selected appearance components and starts 11toXP at subsequent sign-ins.
- **Repair** applies changed preferences, refreshes the selected components, and updates the sign-in path if you moved the executable. Turning off a component restores the settings 11toXP still owns for that component.
- **Restore** removes the XP-style taskbar and sign-in entry and restores the settings 11toXP saved. Restore before deleting the executable.
- **Restore and Exit** closes 11toXP after successful restoration. A failed restore leaves the controls open for another attempt.

Choose **Luna Blue**, **Luna Silver**, or **Luna Olive Green**. The taskbar and Start menu, desktop icons, cursors, and sounds each have their own control. The XP-style taskbar runs on the primary display. Use **Group Similar Windows** and the Quick Launch list to configure task buttons and the built-in File Explorer, Windows Settings, and XP Explorer shortcuts. The taskbar context menu opens the settings, switches to the Windows taskbar, or opens XP Explorer.

**Open XP Explorer** opens the built-in file browser with a folder tree, task pane, and address bar. It browses local folders and opens files through Windows. **Open in Windows Explorer** opens the displayed folder for native file operations. **Use XP Explorer** routes supported taskbar and Start menu folder shortcuts to the built-in browser.

**Change Wallpaper** starts unchecked. The unchecked default leaves Windows wallpaper untouched through Apply, Repair, Restore, sign-in, and Explorer recovery. To opt in, select a local JPG, PNG, or BMP. Keep the original and replacement files available. Repair requires the original displays. Restore handles connected original displays first, keeps pictures selected independently in Windows, and retries missing displays when reconnected. 11toXP supports static Picture mode and declines unsupported modes it can detect, including slideshow and Windows Spotlight. Uncheck the option and select Repair to restore the saved picture and stop managing it. [Microsoft wallpaper monitor status](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-idesktopwallpaper-getmonitorrect)

**Show Windows Taskbar** in the XP Start menu pauses the XP taskbar and restores the Windows taskbar's original auto-hide setting. Select **Repair** to resume the XP taskbar. If Windows ignores the auto-hide request, both bars may be visible. The Windows key still opens the Windows Start menu.

11toXP styles the built-in desktop icons for This PC, Documents, Network, and Recycle Bin if already visible. It uses original assets for those icons and four pointer roles, plus five original sounds for six supported events. High contrast, custom pointer sizes, and muted sound events can limit these changes. Windows Settings, native Explorer, individual app frames, system flyouts, and protected Windows screens retain their native appearance.

## State And Recovery

11toXP records original settings and active preferences in `%LOCALAPPDATA%\11toXP\theme-state.json` before applying changes. It also stores control and browser window placement and local copies of generated appearance assets. Sign-in uses the app-owned `11toXP.0348C6BA` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Reopen 11toXP and select **Restore** after an interrupted operation. If no instance is running, `11toXP.exe --restore` can perform the same recovery. A running instance returns exit code 3 for this command and can restore through its window.

After an OS build change, 11toXP uses its taskbar alongside the native one until Repair verifies auto-hide on the new build. The app uses Windows taskbar APIs and per-user settings. It makes no network requests. The bundled runtime can extract native libraries to the .NET temporary bundle cache at launch.

## Build And Verify

Use the .NET 10 SDK on Windows. The x64 bundle requires Windows 11 and no separate runtime installation. Run the following from the source directory in PowerShell.

```powershell
dotnet publish LunaDesktop.csproj -c Release -r win-x64 --self-contained true -o .\bin\bundle -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$check = Start-Process .\bin\bundle\11toXP.exe -ArgumentList '--self-test' -PassThru -Wait
if ($check.ExitCode -ne 0) { exit $check.ExitCode }
```

`--self-test` uses an isolated temporary journal and a simulated Windows settings adapter. It checks default wallpaper isolation, opt-in and opt-out, rollback, retry, per-component transitions, legacy journals, Explorer and sign-in recovery, startup collisions and moves, and taskbar compatibility decisions. `--preview` opens the controls with simulated theme actions for UI inspection. Live shell behavior requires an interactive Windows 11 desktop.

Live checks on Windows 11 x64 covered fullscreen transitions, an Explorer restart, background recovery after process termination, and **Restore and Exit**. The current wallpaper stayed unchanged with **Change Wallpaper** off. Real sign-out and sign-in, mixed-DPI display transitions, and live wallpaper opt-in and restore remain unverified.

Project source uses the MIT license. The self-contained executable redistributes the .NET Windows Desktop runtime under its own license and notices. [Microsoft runtime licensing](https://github.com/dotnet/core/blob/main/license-information-windows.md)
