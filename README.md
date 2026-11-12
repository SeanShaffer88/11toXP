# 11toXP

11toXP gives the Windows 11 desktop the blue Windows XP Luna look. One executable contains the taskbar, Start menu, configuration window, artwork, and .NET runtime.

## Run

Put `11toXP.exe` in a location you plan to keep, then open it.

- **Apply** enables the XP-style taskbar and two-column Start menu. It changes supported desktop icons, pointer roles, and sound events, then starts 11toXP at subsequent sign-ins.
- **Repair** reapplies the appearance and rebuilds the taskbar. It also updates the sign-in path if you moved the executable.
- **Restore** removes the XP-style taskbar and sign-in entry and restores the settings 11toXP saved. Restore before deleting the executable.

**Change Wallpaper** starts unchecked. The unchecked default leaves Windows wallpaper untouched through Apply, Repair, Restore, sign-in, and Explorer recovery. To opt in, select a local JPG, PNG, or BMP. Keep the original and replacement files available. Repair requires the original displays. Restore handles connected original displays first, keeps pictures selected independently in Windows, and retries missing displays when reconnected. 11toXP supports static Picture mode and declines unsupported modes it can detect, including slideshow and Windows Spotlight. Uncheck the option and select Repair to restore the saved picture and stop managing it. [Microsoft wallpaper monitor status](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-idesktopwallpaper-getmonitorrect)

**Show Windows Taskbar** in the XP Start menu pauses the XP taskbar and restores the Windows taskbar's original auto-hide setting. Select **Repair** to resume the XP taskbar. If Windows ignores the auto-hide request, both bars may be visible. The Windows key still opens the Windows Start menu.

11toXP styles the built-in desktop icons for This PC, Documents, Network, and Recycle Bin if already visible. It uses original assets for those icons and four pointer roles, plus five original sounds for six supported events. High contrast, custom pointer sizes, and muted sound events can limit these changes. Windows Settings, Explorer interiors, individual app frames, system flyouts, and protected Windows screens retain their native appearance.

## State And Recovery

11toXP records original settings in `%LOCALAPPDATA%\11toXP\theme-state.json` before applying changes. It also stores control-window placement and local copies of generated appearance assets. Sign-in uses the app-owned `11toXP.0348C6BA` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Reopen 11toXP and select **Restore** after an interrupted operation. If no instance is running, `11toXP.exe --restore` can perform the same recovery. A running instance returns exit code 3 for this command and can restore through its window.

After an OS build change, 11toXP uses its taskbar alongside the native one until Repair verifies auto-hide on the new build. The app uses Windows taskbar APIs and per-user settings. It makes no network requests. The bundled runtime can extract native libraries to the .NET temporary bundle cache at launch.

## Build And Verify

Use the .NET 10 SDK on Windows. The x64 bundle requires Windows 11 and no separate runtime installation. Run the following from the source directory in PowerShell.

```powershell
dotnet publish LunaDesktop.csproj -c Release -r win-x64 --self-contained true -o .\bin\bundle -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$check = Start-Process .\bin\bundle\11toXP.exe -ArgumentList '--self-test' -PassThru -Wait
if ($check.ExitCode -ne 0) { exit $check.ExitCode }
```

`--self-test` uses an isolated temporary journal and a simulated Windows settings adapter. It checks default wallpaper isolation, opt-in and opt-out, rollback, retry, Explorer and sign-in recovery, startup collisions and moves, and taskbar compatibility decisions. `--preview` opens the controls with simulated actions for UI inspection. Live shell behavior requires an interactive Windows 11 desktop.

Project source uses the MIT license. The self-contained executable redistributes the .NET Windows Desktop runtime under its own license and notices. [Microsoft runtime licensing](https://github.com/dotnet/core/blob/main/license-information-windows.md)
