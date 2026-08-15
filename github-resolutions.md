# GitHub Issue and Pull Request Resolutions

The entries below summarize how each issue and pull request was handled and identify the version in which the related change was included. Version 1.2.0 refers to the current 1.2.0 codebase.

## Issues

### [Issue #1 - Memory usage steadily increases while running idle](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/issues/1)

**Resolved in:** `v1.2.0`

The memory growth was caused by fallback polling repeatedly disposing and recreating the Core Audio endpoint after callback inactivity. The refresh path now reuses the attached endpoint and recreates it only when it is missing or invalid. An extended test confirmed stable memory usage for more than eight hours.

### [Issue #6 - Icon hard to identify in Taskbar and Systray](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/issues/6)

**Resolved in:** `v1.2.0`

The existing simplified logo was rebuilt as multi-resolution ICO and Windows target-size assets. Taskbar and notification-area icons now load the size appropriate for the current DPI instead of scaling a single small bitmap, improving clarity across display scales and preserving the Color, Black, and White variants.

## Pull Requests

### [PR #2 - Stop rebuilding the audio endpoint during fallback refresh](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/pull/2)

**Included with revisions in:** `v1.2.0`

The proposed root-cause fix was incorporated into the managed Core Audio service. Fallback refreshes now read the current endpoint state and only reattach after an invalid or unavailable endpoint, eliminating the native allocation churn behind issue #1 without changing normal device-change handling.

### [PR #3 - Move background sync to a low-memory native host](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/pull/3)

**Decision:** Not merged; superseded by the `v1.2.0` fix for issue #1.

The native C++ host was evaluated, but it would introduce a second runtime architecture, duplicated synchronization logic, and additional lifecycle and packaging complexity. The reported memory growth was resolved in the existing managed service through PR #2's lower-risk endpoint refresh fix, so the native-host rewrite was not adopted.

### [PR #4 - Prevent 100% volume spikes after Voicemeeter engine restart](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/pull/4)

**Included with revisions in:** `v1.2.0`

Volume recovery now tracks the last safe Windows level and restores it after Voicemeeter engine restarts, device recovery, resume, or startup at an unexpected 100%. The existing spike-protection setting is functional, and the recovery flow avoids reapplying transient endpoint values.

### [PR #5 - Show Voicemeeter A1/VAIO role names in bindings](https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/pull/5)

**Included with revisions in:** `v1.2.0`

Bindings now use edition-aware Voicemeeter names such as A1, B1, VAIO, and AUX while preserving stable Strip/Bus identifiers for synchronization and saved settings. Custom Voicemeeter labels replace the default role name when present, and the binding details show the technical index and selected device where available.
