# Developing Lagoon

## Build and run

```powershell
dotnet build -c Release
```

Output goes to `%LOCALAPPDATA%\LagoonBuild\...` (`Directory.Build.props` keeps `bin`/`obj` out of
synced folders such as OneDrive). Quit a running Lagoon first: the exe is locked while it runs.
`Lagoon.exe --quit` closes a running copy properly (taskbar and notification pop-ups restored).

## Release

1. Bump `<Version>` in `NotchApp.csproj` and `AppVersion` in `installer/Lagoon.iss`.
2. Self-contained single-file build:
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
     -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
     -o $env:LOCALAPPDATA\LagoonBuild\dist-build
   ```
3. Installer ([Inno Setup 6](https://jrsoftware.org/isinfo.php)):
   `ISCC.exe installer\Lagoon.iss` → `dist\LagoonSetup-<version>.exe`
4. Attach the setup to a GitHub release.

The installer is per-user (no admin) and installs to `%LOCALAPPDATA%\Programs\Lagoon`. Before an
update or uninstall it runs `Lagoon.exe --quit`; `Lagoon.exe --enable-startup` registers the
sign-in task.

## Dev switches (environment variables)

| Variable | Effect |
|---|---|
| `NOTCHAPP_LOG=1` | writes a debug log to `%TEMP%\Lagoon.log` |
| `NOTCHAPP_OPENCARD=media\|clipboard\|widgets\|timer\|agents\|controls` | holds the card open on that tab |
| `NOTCHAPP_DOCKPOPUP=calendar\|notifications\|controls\|background` | opens that dock card at start |
| `NOTCHAPP_DOCKSTAY=1` | the dock never tucks away |
| `NOTCHAPP_FAKEAPPS=N` | adds N copies of the dock's icons (stress test) |
| `NOTCHAPP_BATTERYTEST=1` | plays fake charger / low battery / full alerts |
| `NOTCHAPP_BUDSTEST=1` | shows the earbuds-connected pop-out |
| `NOTCHAPP_ALARMTEST=1` | rings the timer alarm |
| `NOTCHAPP_FREEZETEST=1` | freezes the UI thread to test the freeze guard |
| `NOTCHAPP_DATADIR=path` | uses another settings folder |

## Ground rules

- Keep it light: react to Windows events, never poll in a loop; read system state only when shown.
- Anything Lagoon changes in Windows (taskbar auto-hide, notification banners, battery saver
  threshold) is recorded first and restored on quit, on crash, and by the freeze guard.
- Settings and logs live in `%APPDATA%\Lagoon` (`settings.json`, `crash.log`).
