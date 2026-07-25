# VMWV native background host

`VMWV.NativeHost.exe` is the always-running part of the application. It uses Win32, Core Audio COM callbacks, and the Voicemeeter Remote C API directly, without loading .NET, WinUI, XAML, NAudio, or Windows App SDK into the background process.

The existing `VMWV.App.exe` remains the settings and diagnostics interface. The host starts it with `--settings-only` when the tray icon or Start Menu shortcut is opened. Closing that window exits the WinUI process instead of leaving a second tray process behind.

## Build

```powershell
cmake -S native/VMWV.NativeHost -B artifacts/native-host -A x64
cmake --build artifacts/native-host --config Release
```

The host reads the existing settings file from:

```text
%LOCALAPPDATA%\Voicemeeter Windows Volume\settings.json
```

It reloads that file when the WinUI settings interface changes it.
