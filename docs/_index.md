# Voicemeeter Windows Volume Modern documentation

Voicemeeter Windows Volume Modern monitors the default Windows output device and applies its volume, and optionally its mute state, to the Voicemeeter strips and buses selected by the user. It can remain active in the notification area, reconnect after lifecycle changes, and expose status and recovery controls through a native Windows interface.

The application is organized into four main areas: **Dashboard**, **Bindings**, **Settings**, and **Support me**. The following documents describe the current public behavior of each area and the workflows that connect them.

## Application areas

- [Dashboard](dashboard.md) - View the current Voicemeeter connection, Windows audio state, active strip and bus bindings, and recent diagnostic events.
- [Bindings](bindings.md) - Choose which edition-specific Voicemeeter strips and buses follow Windows volume and mute changes.
- [Settings](settings.md) - Control the connection, update status, appearance, language, startup behavior, synchronization, mapping, and project information.
- [Support me](support.md) - Open the Ko-fi, GitHub star, and video showcase support actions.

## Behaviors and workflows

- [Connection and synchronization](connection-and-synchronization.md) - Understand automatic connection, manual connection controls, volume and mute synchronization, and target updates.
- [Recovery and safety](recovery-and-safety.md) - Configure fallback monitoring, remembered volume, spike protection, device-change recovery, and resume recovery.
- [Tray and window behavior](tray-and-window.md) - Use background startup, close-to-tray, the notification-area menu, single-instance activation, and responsive navigation.

## Typical workflow

1. Start Voicemeeter and the application.
2. Wait for the Dashboard to identify the running Voicemeeter edition.
3. Open [Bindings](bindings.md) and enable the strips or buses that should follow Windows audio.
4. Review [Settings](settings.md) to choose mute synchronization, volume mapping, startup, tray, appearance, and recovery behavior.
5. Leave the application open or use its [tray behavior](tray-and-window.md) to keep synchronization active in the background.

Synchronization is paused when Voicemeeter is unavailable or no bindings are enabled. The Dashboard and diagnostic list show the current state without requiring a separate diagnostics page.
