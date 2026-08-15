# Settings

Settings contains the application's operational controls and persistent preferences. Changes are saved locally and reused the next time the application starts.

## Updates

The update card compares the installed version with the latest GitHub release when the application starts.

- **You're up to date** means the installed version is at least the latest published version.
- **Version _x_ is available** provides an **Open download page** link to the release page.
- **Update check unavailable** means the GitHub release request failed. This does not stop local audio synchronization.

The application reports availability only. It does not download or install an update automatically.

## Control

The Control group provides immediate actions:

- **Refresh status** records a status refresh and confirms that the application shell and settings are available.
- **Voicemeeter connection** changes between **Connect to Voicemeeter** and **Disconnect** according to the current state.
- **Show Voicemeeter** brings the Voicemeeter window to the foreground.
- **Restart Voicemeeter audio engine** requests an engine restart and reapplies the current Windows audio state after the engine settles.

Failures are reported in the Dashboard's Diagnostics list. See [Connection and synchronization](connection-and-synchronization.md) for the complete connection flow.

## Appearance

### Logo variant

Select **Color**, **Black**, or **White**. The chosen variant is applied to the sidebar, taskbar, window, and notification-area icon.

### Interface layout

- **Compact** centers pages and limits their maximum content width.
- **Expanded** allows pages to use the available horizontal space.

Both modes remain responsive: multi-column content stacks when the window becomes narrow.

### Language

Select one of the installed application languages. Visible navigation labels, settings, status text, binding placeholders, and support content update without restarting the application.

### Hide Support me tab

Removes **Support me** from the sidebar. If the page is open when this option is enabled, the application returns to Settings. The page can be restored by turning the option off.

## Startup and sync

### Start with Windows

Registers the application for the current Windows user. At sign-in it starts in the notification area without opening the main window, then initializes audio monitoring and automatic Voicemeeter connection.

### Close to tray

When enabled, closing the main window hides it and keeps synchronization active. When disabled, closing the window exits the application and stops synchronization.

See [Tray and window behavior](tray-and-window.md) for restoration and exit actions.

### Sync mute state

Mirrors Windows mute and unmute changes to enabled bindings. Volume synchronization remains active regardless of this option.

### Restore remembered volume

Keeps the last safe Windows volume available to recovery operations. It is used with engine-restart and spike-protection flows described in [Recovery and safety](recovery-and-safety.md).

## Volume mapping

### Limit maximum gain to 0 dB

Caps the mapped maximum at 0 dB, preventing Windows volume changes from applying positive Voicemeeter gain.

### Use linear volume scale

Uses a linear interpolation between the configured minimum and maximum gains. When disabled, the application uses a logarithmic audio curve.

### Minimum gain

Sets the gain used at the lowest Windows volume level.

### Maximum gain

Sets the gain used when Windows volume reaches 100%. The 0 dB limit overrides positive values when enabled.

### Fallback polling

Sets the fallback check interval in milliseconds. Polling is used only when normal Windows audio callbacks have not provided updates for several seconds.

## Advanced settings

- **Prevent sudden 100% volume spikes** restores the last safe level when an unexpected jump to 100% is detected. Engine-restart recovery also uses the safe level.
- **Restart engine when audio devices change** requests recovery after default audio device changes.
- **Restart engine when any device changes** enables the broader device-change recovery path for unstable audio configurations.
- **Restart engine after resume** restarts the Voicemeeter engine after Windows wakes from sleep before reapplying the current audio state.

These options are detailed in [Recovery and safety](recovery-and-safety.md).

## About

The final Settings group identifies:

- the installed Voicemeeter Windows Volume Modern version and its repository;
- the original Voicemeeter Windows Volume project by Frosthaven and its repository.

**Go to repo** opens the selected repository in the default browser.
