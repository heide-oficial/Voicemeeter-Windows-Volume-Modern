# Recovery and safety

Recovery features keep synchronization usable when Windows audio or Voicemeeter changes state. They can be combined according to the stability needs of the system.

## Connection recovery

If an automatic connection attempt fails, the application retries with increasing delays of one, two, five, and then ten seconds. A lost session also returns to the recovery flow. After the engine is ready again, the application identifies the edition, refreshes its targets, and reapplies the current Windows volume and mute state.

A manual disconnect intentionally pauses automatic retries. Use **Connect to Voicemeeter** in Settings to resume immediately.

## Callback monitoring and fallback polling

Windows audio callbacks are the primary source of volume, mute, and default-device changes. Background checks also verify that the monitored endpoint is still the Windows default and recover when a notification is missed.

While audio is idle, these checks run roughly once a second. When a change is detected without a recent callback, checking temporarily uses the faster fallback interval configured in Settings, limited to 25-1,000 ms. It slows down again after five seconds without further fallback-detected changes.

If Windows audio cannot initialize, the application retries with increasing delays up to ten seconds. The current problem appears in [Diagnostics](diagnostics.md) and clears after recovery.

## Remembered volume

When **Restore remembered volume** is enabled, the application retains the last accepted Windows volume as a safe recovery value. Recovery actions can restore that value after an engine restart or rejected spike.

This option does not force a fixed startup volume during normal operation. The remembered value is used when a recovery decision requires it.

## Sudden 100% spike protection

**Prevent sudden 100% volume spikes** rejects an unexpected jump directly to 100% and restores the last safe volume. Normal volume changes continue to synchronize, including deliberate gradual changes.

The action is recorded in Diagnostics so the restored value and reason can be reviewed.

## Audio device changes

When device recovery is enabled, multiple rapid device notifications are grouped before recovery begins. The application refreshes the default Windows output endpoint, then either restarts the connected Voicemeeter engine or requests a new connection.

- **Restart engine when audio devices change** is intended for default-device changes.
- **Restart engine when any device changes** enables the broader recovery behavior for systems where other device changes disrupt audio.

The options have distinct triggers: changing only an unrelated device does not activate the default-output-only option. Both use the same recovery action for a relevant event. A deliberate manual disconnect remains paused.

## Windows resume

After Windows resumes from sleep, the application refreshes the default endpoint and resumes automatic connection handling unless the user deliberately disconnected. Multiple Windows notifications for the same wake-up are handled as one recovery.

- If **Restart engine after resume** is enabled and Voicemeeter is connected, the engine is restarted before the current audio state is reapplied.
- Otherwise, the application requests connection recovery and queues the current Windows volume and mute state.

Resume and recovery failures are non-fatal and appear on the [Diagnostics page](diagnostics.md).

## Settings safety

Changes are saved in sequence so rapid edits do not compete for the same settings file. Exiting waits for pending work, including the latest settings save, within the shutdown time limit.

If the settings document is invalid JSON, the application first preserves a timestamped copy before allowing a replacement. If it cannot read the file or preserve that backup, it reports the problem, pauses synchronization, and avoids overwriting the existing file. Resolve the file-access problem and restart the application before changing settings again.

## Diagnosing a recovery problem

Use the [Dashboard](dashboard.md) to check the connection card and [Diagnostics](diagnostics.md) to review recent events. Then use the [Control actions](settings.md#control) to reconnect, show Voicemeeter, or restart its audio engine when manual intervention is needed.
