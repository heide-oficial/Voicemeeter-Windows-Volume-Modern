# Diagnostics

Open **Diagnostics** in the sidebar to check the Voicemeeter session and current failures, and review connection attempts, audio changes, and recovery actions. The current-status panel appears above the scrollable event history.

## Current status

- **Voicemeeter session** shows the connection state and, while connected, the detected edition on the same line, for example **Connected - Voicemeeter Banana**. It also distinguishes waiting for the process from reconnecting.
- **Current failures** shows unresolved connection, audio-monitoring, and volume/mute synchronization errors, as well as settings and endpoint availability problems. Successful retries clear the corresponding failure here without removing old events from the history.

The two fields appear side by side in wide windows and stack in narrow windows. They update automatically as the connection, monitoring, or synchronization state changes, without requiring Refresh. Long errors can scroll independently while the event history remains accessible below the status panel.

## Event history

The newest event appears first. The list keeps up to 200 entries from the current application session. Consecutive volume changes replace the previous volume-change entry rather than filling the list with repeated messages.

Mute and unmute events are both recorded here. On the [Dashboard](dashboard.md), only the muted state adds a label to the Windows audio card.

Changing the interface language does not create a language-saved event or record existing bindings as newly changed. Previously recorded events retain their original language.

## Local log files

Events are also written to the local Logs folder. Diagnostic logging periodically removes files older than seven days and removes the oldest files when the folder exceeds 50 MiB. Under heavy activity, repetitive events may be omitted in favor of errors, so the files are not an exhaustive audio-event trace.

Logs can contain device names, volume and mute values, local paths, and error details. They are not uploaded automatically. Review the [privacy disclosures](../README.md#-privacy-and-disclosures) before sharing a log.

## Dates and times

Times follow Windows regional preferences, including custom date patterns and the 12-hour or 24-hour clock. Events use the Windows long-time format; update checks use its short-time format. Hover over an event's time to see its date and time. Changing the application's translation language does not change these formats. Regional changes also apply to existing events when Windows notifies the app or you return to its window.

## Related actions

Diagnostics reports status and activity; it does not change the connection or audio state. Use [Settings](settings.md#control) for connection and recovery actions, and see [Recovery and safety](recovery-and-safety.md) for automatic recovery behavior.
