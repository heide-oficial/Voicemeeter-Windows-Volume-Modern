# Settings

Settings contains the application's operational controls and persistent preferences. Changes are saved locally and reused the next time the application starts.

## Updates

The update status card compares the installed version with the latest GitHub release. **Check now**, on the right, starts a manual check. The card also shows the last attempt and last successful check, using Windows regional date and time formats.

The separate **Check for updates automatically** toggle below the status card enables or disables background checks. It is enabled by default. Automatic checks are limited to one attempt per 24 hours, including failed attempts, and the previous result is kept between application sessions. Manual checks remain available when the toggle is off and do not wait for this interval.

- **You're up to date** means the installed version is at least the latest published version.
- **Version _x_ is available** provides an **Open download page** link to the release page.
- **Update check unavailable** means the GitHub release request failed. This does not stop local audio synchronization.
- **Updates have not been checked** means no usable previous result is available and a check has not completed yet.

The application reports availability only. It does not download or install an update automatically.

## Control

The Control group provides immediate actions:

- **Refresh status** rechecks Windows audio and the Voicemeeter connection, refreshes binding targets, and reapplies the current audio state. It does not reconnect a session that was deliberately disconnected.
- **Voicemeeter connection** changes between **Connect to Voicemeeter** and **Disconnect** according to the current state.
- **Show Voicemeeter** brings the Voicemeeter window to the foreground.
- **Restart Voicemeeter audio engine** requests an engine restart and reapplies the current Windows audio state after the engine settles.

Failures are reported on the [Diagnostics page](diagnostics.md). See [Connection and synchronization](connection-and-synchronization.md) for the complete connection flow.

## Appearance

### Logo variant

Select **Color**, **Black**, or **White**. The chosen variant is applied to the sidebar, taskbar, window, and notification-area icon.

### Interface layout

- **Compact** centers pages and limits their maximum content width.
- **Expanded** allows pages to use the available horizontal space.

Both modes remain responsive: multi-column content stacks when the window becomes narrow.

### Language

Select **English**, **Brazilian Portuguese**, **Italian**, or **Simplified Chinese**. Visible navigation labels, settings, status text, binding placeholders, and support content update without restarting the application. Missing translations in an additional language file fall back to English.

Dates and times continue to follow Windows regional preferences, independently of the selected language. Existing diagnostic events keep the language in which they were recorded; switching languages does not add a language-saved event.

### Hide Support me tab

Removes **Support me** from the sidebar. If the page is open when this option is enabled, the application returns to Settings. The page can be restored by turning the option off.

## Startup and sync

### Start with Windows

Registers the application for the current Windows user. At sign-in it starts in the notification area without opening the main window, then initializes audio monitoring and automatic Voicemeeter connection.

### Close to tray

When enabled, closing the main window hides it and keeps synchronization active. When disabled, closing the window exits the application and stops synchronization.

See [Tray and window behavior](tray-and-window.md) for restoration and exit actions.

### Sync mute state

Mirrors Windows mute and unmute changes to enabled bindings. Enabling it also applies the current mute state immediately. Volume synchronization remains active regardless of this option.

### Restore remembered volume

Keeps the last safe Windows volume available to recovery operations. It is used with engine-restart and spike-protection flows described in [Recovery and safety](recovery-and-safety.md).

## Volume mapping

### Limit maximum gain to 0 dB

Caps the mapped maximum at 0 dB, preventing Windows volume changes from applying positive Voicemeeter gain. A lower configured maximum remains in effect: for example, a maximum of -12 dB stays at -12 dB when this option is enabled.

### Use linear volume scale

Uses a linear interpolation between the configured minimum and maximum gains. When disabled, the application uses a logarithmic audio curve.

### Minimum gain

Sets the gain used at the lowest Windows volume level.

### Maximum gain

Sets the gain used when Windows volume reaches 100%. The 0 dB limit overrides positive values when enabled.

Gain limits must be between -60 and 12 dB, and the minimum must not exceed the maximum. An empty or invalid value shows a validation message and leaves the last valid mapping in effect. Valid changes to the limits, curve, or 0 dB cap are applied to enabled bindings without waiting for another Windows volume change.

### Fallback polling

Sets the active fallback check interval in milliseconds. Callbacks normally deliver audio changes immediately. Background checks also verify the current default output; after detecting changes missed by callbacks, they temporarily run more frequently, then slow down while audio is idle. The active interval is limited to 25-1,000 ms. See [Recovery and safety](recovery-and-safety.md#callback-monitoring-and-fallback-polling).

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
