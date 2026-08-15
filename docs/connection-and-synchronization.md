# Connection and synchronization

The main application workflow links the default Windows output endpoint to selected Voicemeeter targets.

## Automatic connection

After its audio monitor starts, the application attempts to connect to Voicemeeter automatically. If Voicemeeter is not available, it continues retrying at 10-second intervals while the application is running.

After a successful connection, the application:

1. identifies the running Voicemeeter edition;
2. refreshes the available strip and bus targets;
3. restores the saved binding selections;
4. queues the current Windows volume and mute state for enabled targets.

This initialization also occurs after a recovered connection, so users do not need to toggle a binding or change the Windows volume to restart synchronization.

## Manual connection controls

The **Control** group in [Settings](settings.md#control) contains the connection actions:

- **Connect to Voicemeeter** starts an immediate connection attempt.
- **Disconnect** closes the native client connection and pauses synchronization.
- **Show Voicemeeter** brings the Voicemeeter window to the foreground. The application first ensures that the client is connected.
- **Restart Voicemeeter audio engine** requests an engine restart and then reapplies the current Windows audio state.

After a manual disconnect, automatic connection attempts are paused until the user connects again or a lifecycle recovery resumes connection handling.

## Volume synchronization

The application monitors the volume of the default Windows output endpoint. When it changes, the current percentage is converted to a Voicemeeter gain value using the configured mapping and sent to every enabled strip and bus.

Only the latest pending volume is processed when changes arrive faster than Voicemeeter can accept them. This prevents obsolete intermediate values from delaying the final requested volume.

The gain curve and limits are controlled by [Volume mapping](settings.md#volume-mapping).

## Mute synchronization

When **Sync mute state** is enabled, Windows mute and unmute changes are sent to every enabled binding. Turning the option off leaves volume synchronization active but stops future mute updates.

## Paused states

Synchronization cannot apply updates when:

- Voicemeeter is disconnected or unavailable;
- no strips or buses are enabled;
- Windows has no usable default output endpoint;
- the application has exited rather than remaining in the notification area.

Saved bindings and settings are retained during these states. Automatic reconnection and the recovery options are described in [Recovery and safety](recovery-and-safety.md).
