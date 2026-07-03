# Voicemeeter Windows Volume Modern

Voicemeeter Windows Volume Modern is a native Windows app that synchronizes the Windows default output volume and mute state with selected Voicemeeter strips and buses.

This project is a Windows-focused rewrite of the original [Frosthaven/voicemeeter-windows-volume](https://github.com/Frosthaven/voicemeeter-windows-volume). The modern app removes the legacy Node.js tray runtime and replaces it with WinUI 3, Windows App SDK, Core Audio callbacks, and a native Voicemeeter Remote client.

![Voicemeeter Windows Volume Modern dashboard](https://i.imgur.com/dGvicy8.png)
![Voicemeeter Windows Volume Modern settings](https://i.imgur.com/KlaOjxN.png)
![Voicemeeter Windows Volume Modern bindings](https://i.imgur.com/5pyZ0fy.png)

## What Voicemeeter Windows Volume Modern Is

Voicemeeter Windows Volume Modern is a desktop utility for Windows 10/11. It runs as a native WinUI 3 app, monitors the current Windows default audio endpoint, and applies matching volume or mute changes to Voicemeeter Remote targets.

The app is not a replacement for Voicemeeter. It is a companion app that keeps selected Voicemeeter strips and buses aligned with Windows volume behavior.

## What Voicemeeter Windows Volume Modern Is For

- Keeping Windows volume changes synchronized with Voicemeeter input strips or output buses.
- Mirroring Windows mute changes to selected Voicemeeter targets.
- Restoring a small, focused tray utility workflow using native Windows 11 UI.
- Avoiding the operational fragility of the old Node.js, PowerShell polling, and native addon stack.

## Improvements

- Moves the app from a legacy Node.js tray process to a native C#/.NET Windows app.
- Replaces the old tray-menu-only experience with a full WinUI 3 shell for status, bindings, settings, diagnostics, and project information.
- Replaces PowerShell-based audio scanning with native Windows Core Audio callbacks for normal volume, mute, and device-change monitoring.
- Replaces the `ffi-napi`/Node native addon path with a native Voicemeeter Remote integration.
- Keeps compatibility with the legacy settings shape while moving persistence to typed C# models and source-generated JSON serialization.
- Adds atomic settings writes, corrupt-settings backup, duplicate-toggle normalization, and polling-bound validation around the old JSON configuration model.
- Reduces repeated runtime work through coalesced volume/mute events, batched Voicemeeter updates, debounced settings writes, and bounded diagnostic logs.
- Adds a real Windows 11-style UI while preserving the core workflow of selecting Voicemeeter strips and buses that follow Windows audio state.
- Replaces the old build/release chain based on webpack, nexe, and NSIS scripts with .NET build projects and release scripts for setup and portable EXE artifacts.

## Bug Fixes

- Replaced the legacy startup task flow that could hang the installer while waiting for a child `cmd.exe` process.
- Removed duplicate app launch paths from the old autostart handling, reducing the chance of multiple tray instances.
- Replaced fragile PowerShell worker execution with native services and explicit lifecycle handling.
- Fixed the old "restart on any device change" behavior that could keep using the startup toggle value instead of the current setting.
- Removed duplicated resume scanners that could be created when repeatedly toggling resume recovery.
- Preserved `initial_volume = 0` as a valid remembered volume instead of treating it as missing state.
- Added corrupt settings recovery by backing up invalid JSON before recreating settings.
- Replaced the legacy `ffi-napi`/`voicemeeter-connector` dependency path that no longer built reliably on current Node.js versions.

## Development Guide

### Requirements

- Windows 10 version 1809 or newer.
- .NET SDK 10.
- Windows App SDK build tooling.
- Microsoft WinApp CLI for packaged development runs and UI smoke testing.
- Voicemeeter installed and running for full manual validation.

### Build the Windows Host

```powershell
rtk dotnet build native\VMWV.Core\VMWV.Core.csproj -c Debug
rtk dotnet build native\VMWV.Infrastructure.Windows\VMWV.Infrastructure.Windows.csproj -c Debug
```

### Build the WinUI Shell

```powershell
rtk dotnet build native\VMWV.App\VMWV.App.csproj -c Debug -p:Platform=x64
```

### Run Locally

Packaged WinUI development runs should use WinApp CLI:

```powershell
rtk dotnet build native\VMWV.App\VMWV.App.csproj -c Debug -p:Platform=x64
rtk winapp run native\VMWV.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64
```

Run core tests:

```powershell
rtk dotnet run --project native\VMWV.Core.Tests\VMWV.Core.Tests.csproj
```

### Create Release Artifacts

```powershell
rtk powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Create-ReleaseArtifacts.ps1 -Platform x64
```

Outputs:

- `artifacts\release\VoicemeeterWindowsVolumeModern-Portable-x64.exe`
- `artifacts\release\VoicemeeterWindowsVolumeModern-Setup-x64.exe`

Generate only the portable EXE:

```powershell
rtk powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Create-PortableRelease.ps1 -Platform x64
```

### Project Layout

- `native/VMWV.App` - WinUI 3 desktop app, navigation, tray integration, title bar, app icon handling, and single-instance activation.
- `native/VMWV.Core` - settings, source-generated JSON persistence, volume mapping, and service contracts.
- `native/VMWV.Infrastructure.Windows` - Windows Core Audio and Voicemeeter Remote integrations.
- `native/VMWV.Core.Tests` - lightweight test runner for critical core behavior.
- `native/VMWV.Portable` - portable EXE bootstrapper that contains the release app payload.
- `native/VMWV.Setup` - setup EXE bootstrapper that installs the release app payload per user.
- `scripts` - release and UI smoke-test scripts.

## Credits

- Modern rewrite: Matheus Heidemann.
- Original project: [Frosthaven/voicemeeter-windows-volume](https://github.com/Frosthaven/voicemeeter-windows-volume).

## License

GNU General Public License v3.0 or later. See [LICENSE](LICENSE).
