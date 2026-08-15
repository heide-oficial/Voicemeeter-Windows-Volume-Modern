# Recovery and safety

Recovery features keep synchronization usable when Windows audio or Voicemeeter changes state. They can be combined according to the stability needs of the system.

## Connection recovery

If an automatic connection attempt fails, the application waits 10 seconds and tries again. A lost session also returns to the recovery flow. After reconnecting, the application refreshes the edition-specific targets and reapplies the current Windows volume and mute state.

A manual disconnect intentionally pauses automatic retries. Use **Connect to Voicemeeter** in Settings to resume immediately.

## Callback monitoring and fallback polling

Windows audio callbacks are the primary source of volume, mute, and default-device changes. If no callback has been received for five seconds, fallback polling checks the current endpoint at the interval selected in Settings.

Fallback polling does not replace normal callbacks and does not continuously perform duplicate work while callbacks are healthy.

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

Both options use the same recovery action when a relevant device event reaches the application. Enabling either one activates device-change recovery.

## Windows resume

After Windows resumes from sleep, the application refreshes the default endpoint and resumes connection handling.

- If **Restart engine after resume** is enabled and Voicemeeter is connected, the engine is restarted before the current audio state is reapplied.
- Otherwise, the application requests connection recovery and queues the current Windows volume and mute state.

Resume and recovery failures are non-fatal and appear in the Dashboard's Diagnostics list.

## Diagnosing a recovery problem

Use the [Dashboard](dashboard.md) to check the connection card and the newest Diagnostics entries. Then use the [Control actions](settings.md#control) to reconnect, show Voicemeeter, or restart its audio engine when manual intervention is needed.
