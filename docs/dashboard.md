# Dashboard

The Dashboard is the default application page. It summarizes the live Windows and Voicemeeter state and shows which bindings are currently active.

## Voicemeeter status

The **Voicemeeter** card shows the connection state and, after a successful connection, the detected edition such as Voicemeeter, Voicemeeter Banana, or Voicemeeter Potato.

The state can change while the application is running:

- **Connecting** while the application is waiting for the Voicemeeter process and native client.
- **Connected** when commands and synchronization are available.
- **Disconnected** when the client is not connected or after a manual disconnect.
- **Error** when a connection attempt fails. The related message is also added to Diagnostics.

Connection actions and automatic retry behavior are described in [Connection and synchronization](connection-and-synchronization.md).

## Windows audio status

The **Windows audio** card shows:

- the current volume percentage of the default Windows output endpoint;
- the endpoint display name;
- whether the endpoint is muted or unmuted.

The card follows default-output changes while the application is running. If Windows has no usable default output endpoint, the card reports that the endpoint is unavailable and synchronization cannot proceed until one becomes available.

## Active Strip and Bus cards

The **Strip** and **Bus** cards list only enabled bindings. Each active target is displayed as a compact item using its current Voicemeeter name.

- If no strip is enabled, the Strip card shows **No active strip bindings**.
- If no bus is enabled, the Bus card shows **No active bus bindings**.

Bindings are managed on the [Bindings page](bindings.md). Enabling or disabling a target updates these cards immediately.

## Diagnostics

The **Diagnostics** card presents recent operational events with a timestamp, category, and message. Events include connection attempts, audio changes, settings updates, recovery actions, and failures.

The newest event appears first. Consecutive volume-change events replace the previous volume-change line instead of filling the list with repeated entries. The on-screen list is bounded, so older entries are removed as new events arrive.

Diagnostics is intended to explain the current application state. It does not provide controls; the relevant actions remain in [Settings](settings.md).

## Layout behavior

At wider window sizes, Windows and Voicemeeter status cards are arranged beside the Strip and Bus cards. At narrower sizes, the groups stack vertically. The **Compact** and **Expanded** interface modes in Settings determine the maximum page width; see [Appearance](settings.md#appearance).
