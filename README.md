# Voicemeeter Windows Volume Modern
<p align="left">
<a href="https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/stargazers"><img src="https://img.shields.io/github/stars/heide-oficial/Voicemeeter-Windows-Volume-Modern?colorA=363a4f&colorB=e0ac00&style=for-the-badge" alt="GitHub star count"></a>
<a href="https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/releases"><img src="https://img.shields.io/github/downloads/heide-oficial/Voicemeeter-Windows-Volume-Modern/total?colorA=363a4f&colorB=d53984&style=for-the-badge" alt="GitHub release download count"></a>
<a href="https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/blob/main/LICENSE"><img src="https://img.shields.io/static/v1.svg?style=for-the-badge&label=License&message=GPL-3.0&colorA=363a4f&colorB=b7bdf8" alt="GPL-3.0 license"></a>
</p>

Voicemeeter Windows Volume Modern is a native Windows companion application that synchronizes the default Windows output volume and mute state with selected Voicemeeter strips and buses. It is intended for Voicemeeter users who want reliable system-volume control through a Windows 11-style interface, background tray operation, and recovery from audio or Voicemeeter lifecycle changes.

## ✨ Features

- Synchronizes Windows output volume with selected Voicemeeter input strips and output buses.
- Optionally mirrors the Windows mute state and restores the last remembered volume.
- Displays edition-aware Voicemeeter channel names, custom labels, and assigned audio devices.
- Reconnects to Voicemeeter automatically and includes recovery options for device changes, audio-engine restarts, and Windows resume.
- Protects against unexpected 100% volume recovery and supports configurable gain mapping.
- Runs in the notification area, supports close-to-tray and Windows startup, and prevents duplicate application instances.
- Provides dashboard status, strip and bus binding controls, and bounded diagnostic events.
- Supports compact and expanded interface layouts with selectable logo variants.

## 🖼️ Demo

![Dashboard showing Voicemeeter and Windows audio status](https://i.imgur.com/TMq51En.png)

![Bindings page for Voicemeeter strips and buses](https://i.imgur.com/bcykopZ.png)

![Settings page with native Windows controls](https://i.imgur.com/BSBzsKL.png)

## 🚀 Usage

1. Install Voicemeeter and ensure it can run on Windows.
2. Start Voicemeeter Windows Volume Modern. The application attempts to connect to the running Voicemeeter edition automatically.
3. Open **Bindings** and enable the strips or buses that should follow the Windows default output volume.
4. Use **Settings** to configure mute synchronization, startup and tray behavior, volume mapping, recovery options, language, and appearance.
5. Close the main window to keep the application running in the notification area when close-to-tray is enabled.

For detailed descriptions of the application screens, controls, and behavior, see the [application documentation](docs/_index.md).

## ⚙️ Requirements

- Windows 10 version 1809 or newer.
- A 64-bit Windows installation for the published x64 packages.
- Voicemeeter installed; it must be running for synchronization to operate.

## ⬇️ Installation

### Recommended installation

Download `VoicemeeterWindowsVolumeModern-Setup-x64.msi` from the [latest GitHub release](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/releases/latest), open it, and follow the Windows Installer steps. The per-machine installation may request administrator approval. After installation, launch **Voicemeeter Windows Volume Modern** from the Start menu or the optional desktop shortcut.

### Portable version

Download `VoicemeeterWindowsVolumeModern-Portable-x64.exe` from the [latest GitHub release](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/releases/latest) and run it directly. The launcher extracts its self-contained application payload to the current user's temporary directory and starts the application; no separate .NET runtime installation is required.

## 🔒 Privacy and disclosures

- The application does not include telemetry, analytics, advertising, authentication, or user accounts.
- Settings are stored locally in `%LOCALAPPDATA%\Voicemeeter Windows Volume\settings.json`. Invalid settings files may be retained beside it as timestamped recovery backups.
- Diagnostic log files can be written locally under `%LOCALAPPDATA%\Voicemeeter Windows Volume\Logs`.
- The update checker sends an HTTPS request to the public GitHub Releases API for this repository. It sends the application name and version as its HTTP user agent and does not upload settings or audio data.
- Voicemeeter control and Windows audio monitoring are performed locally through the Voicemeeter Remote API and Windows audio services.
- Enabling **Start with Windows** creates an entry for the current user under the Windows `Run` registry key.
- GitHub repository, release, issue, and Ko-fi links open in the default browser only after the user activates them. The Support page displays the Ko-fi brand image from Ko-fi's content delivery network.
- The application is a full-trust Windows desktop application so it can access local audio APIs, Voicemeeter, the notification area, local settings, and startup registration.

## 🌐 Supported languages

- English (`1.0.0+`)
- Brazilian Portuguese (`1.2.0+`)

Help break the language barrier! Want to translate Voicemeeter Windows Volume Modern into your language? Download the [English language file](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/blob/main/native/VMWV.App/Localization/en-us.json), create a copy using the appropriate language code, and translate the text values without changing the keys. Once finished, submit the translated file through a GitHub pull request or attach it to a new GitHub issue. Your contribution will be credited in the project.

## ❤️ Support

Please consider supporting my work. There are many hours of work, thinking and effort behind it. You can support the application by [donating any amount on Ko-fi](https://ko-fi.com/heide_oficial), [starring the GitHub repository](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern), or publishing a video about the application and [submitting it for showcase](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/issues/new?title=%5BSHOWCASE+VIDEO%5D+Video+title+here&labels=showcase+video&body=Here%27s+my+video+showcasing+or+featuring+the+app%3A+%5BINSERT+LINK+HERE%5D).

Thank you!

<a href="https://ko-fi.com/heide_oficial" target="_blank">
  <img src="https://storage.ko-fi.com/cdn/brandasset/v2/support_me_on_kofi_beige.png" alt="Support me on Ko-fi" width="200">
</a>

## 👥 Credits

- Created by [Matheus Heidemann - heide-oficial](https://github.com/heide-oficial).
- Based on the original application by [Frosthaven](https://github.com/Frosthaven/voicemeeter-windows-volume).
- [Aeurias](https://github.com/Aeurias) contributed the [Core Audio fallback refresh fix](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/pull/2).
- [DashingNights](https://github.com/DashingNights) contributed [volume spike recovery](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/pull/4) and [edition-aware Voicemeeter channel naming](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/pull/5).

## 📄 License

This application is licensed under the [GPL-3.0 license](LICENSE).
