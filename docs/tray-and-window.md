# Tray and window behavior

The application can keep audio monitoring and Voicemeeter synchronization active without leaving the main window open.

## Notification-area icon

The notification-area icon uses the logo variant selected in Settings. Its context menu contains:

- **Show** - restores and activates the main window.
- **Exit** - stops the background services and closes the application.

Double-clicking the icon also restores the window. If Windows Explorer restarts, the application recreates its notification-area icon.

## Close to tray

With **Close to tray** enabled, the window close button hides the application instead of exiting it. The visual shell is unloaded while hidden to reduce graphical resource use, but audio monitoring and synchronization continue.

Use **Show** from the notification-area menu or double-click the icon to recreate and restore the interface. Closing the window with this option disabled exits the application.

## Start with Windows

With **Start with Windows** enabled, the application starts for the current user in background mode. It creates the tray icon, initializes audio monitoring, and attempts to connect to Voicemeeter without displaying the main window.

The same portable or installed executable that enabled startup is used for the registration. Disabling the option removes the registration.

## Single-instance behavior

Only one application instance remains active. Starting the executable again restores and activates the existing window instead of creating another background synchronizer.

## Sidebar behavior

The sidebar contains Dashboard, Bindings, Settings, and optionally Support me. The control at the bottom expands or collapses the pane:

- the expanded pane shows the application name and navigation labels;
- the collapsed pane keeps the logo and navigation icons visible;
- tooltips and accessible names identify icon-only controls.

The Support me item can be removed through [Appearance settings](settings.md#appearance).

## Responsive pages

The main content responds to window width. Dashboard groups and Binding columns move from side-by-side layouts to stacked layouts when needed. **Compact** and **Expanded** interface modes control the page width without changing the available functionality.
