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
- **Muted** when the endpoint is muted or its volume is zero. An unmuted endpoint does not add a status label to the card.

The card follows default-output changes while the application is running. If Windows has no usable default output endpoint, the card reports that the endpoint is unavailable and synchronization cannot proceed until one becomes available.

## Active Strip and Bus cards

The **Strip** and **Bus** cards list only enabled bindings. Each compact item shows the current Voicemeeter name, its channel index (such as **Strip 3** or **Bus 0**), and the assigned device. A custom name replaces the default name when one is set.

If Voicemeeter does not report a device for the channel, the item shows **No device selected**. Long names are shortened to fit; hover over them to read the full text.

- If no strip is enabled, the Strip card shows **No active strip bindings**.
- If no bus is enabled, the Bus card shows **No active bus bindings**.

Bindings are managed on the [Bindings page](bindings.md). Enabling or disabling a target updates these cards immediately.

Operational events, including mute and unmute changes, appear on the separate [Diagnostics page](diagnostics.md).

## Layout behavior

The Voicemeeter, Windows audio, Strip, and Bus cards are stacked vertically at every window width. The **Compact** and **Expanded** interface modes in Settings determine the maximum page width; see [Appearance](settings.md#appearance).
