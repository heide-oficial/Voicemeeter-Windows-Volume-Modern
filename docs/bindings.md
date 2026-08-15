# Bindings

Bindings define which Voicemeeter channels follow the default Windows output volume. They are divided into **Strip** and **Bus** groups.

## Available targets

After connecting, the application detects the running Voicemeeter edition and shows only its available targets:

| Edition | Strips | Buses |
| --- | ---: | ---: |
| Voicemeeter | 3 | 2 |
| Voicemeeter Banana | 5 | 5 |
| Voicemeeter Potato | 8 | 8 |

The standard role names are edition-aware. Examples include hardware inputs and VAIO strips, plus the A and B output buses supported by the detected edition.

Before the first successful connection, generic strip and bus placeholders may be visible because the application has not yet discovered the active edition. The list is replaced with the edition-specific targets after connection.

## Information shown for each binding

Each row contains:

- the Voicemeeter role name, or the custom label reported by Voicemeeter when one is defined;
- its stable **Strip _n_** or **Bus _n_** index;
- the assigned device name, or **No device selected** when no device is reported;
- an **On/Off** switch.

When a meaningful custom label exists, it replaces the standard role name instead of being appended to it. The stable index remains visible so the target can still be identified.

## Enabling and disabling targets

Turn a binding **On** to include it in volume synchronization. If mute synchronization is enabled in Settings, the same target also receives Windows mute changes.

Changes are saved automatically. Enabling a target while connected immediately queues the current Windows volume and mute state, so a separate volume adjustment is not required to initialize it.

Turn a binding **Off** to stop sending future updates to that channel. Disabling a binding does not reset the existing gain or mute value in Voicemeeter.

## Relationship with other pages

- Enabled targets appear in the Dashboard's **Strip** and **Bus** cards.
- Gain values are calculated from the options under [Volume mapping](settings.md#volume-mapping).
- Mute behavior is controlled by **Sync mute state** under [Startup and sync](settings.md#startup-and-sync).
- If Voicemeeter disconnects, the selections remain saved but synchronization pauses until the connection is restored.

On narrow windows, the Strip and Bus groups stack vertically. On wider windows, they are shown side by side.
