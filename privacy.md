# Privacy Policy for MultiExplorer

**Last updated:** 15 September 2026

MultiExplorer is a Windows desktop file-explorer application. It is designed to
operate locally on your computer. This policy explains what information the
application stores, where it stores it, and when information is shared with
other software on your computer.

## Summary

MultiExplorer does not collect, transmit, sell, rent, or share your information
with MultiExplorer or any analytics, advertising, or cloud service. It has no
user accounts, telemetry, advertising, update-checking service, or network
service dependency.

Some information is stored locally so the application can remember your
preferences, perform file operations reliably, and diagnose problems. That
information remains in your current Windows user profile unless you choose to
share it, for example by sending a log file to someone for support.

## Information stored locally

MultiExplorer may create the following files under your current Windows user
account:

| Location | Purpose | Information it can contain |
| --- | --- | --- |
| `%APPDATA%\MultiExplorer\settings.json` | Saves application preferences. | Window size and position; open folder tabs; address-bar history; theme; tray, auto-start, hotkey, and QuickLook preferences. Folder paths can identify files or locations you have used. |
| `%LOCALAPPDATA%\MultiExplorer\app.log` | Records diagnostic information and errors. | Times, error details, and, where relevant, file or folder paths involved in an action. |
| `%LOCALAPPDATA%\MultiExplorer\Operations` | Lets the main application and its file-operation worker coordinate copy, move, and delete actions. | Source and destination paths, operation progress, current item, status, error details, timestamps, and the worker process ID. |

Settings are retained until you change or delete them. The diagnostic log is a
rolling log: when it reaches 512 KB, MultiExplorer removes its oldest half.

For completed, failed, or cancelled file operations, MultiExplorer deletes the
short-lived request and cancellation files. It retains the operation status
record for up to one day so that an interrupted application session can report
the outcome correctly, then removes it. A file can remain longer if Windows or
another process prevents its deletion; the application tries again during later
cleanup.

## Windows and file-system integration

MultiExplorer uses Windows Shell features to display folders, thumbnails,
context menus, previews, drag-and-drop, and to perform file operations. It also
uses the current-user Windows Registry for the optional sign-in auto-start
setting and selected Explorer display preferences.

Windows, installed shell extensions, preview handlers, and security software
may process the files you browse or preview under their own policies.
MultiExplorer does not control those components or send information to them
outside the normal local Windows integration.

## Optional QuickLook integration

QuickLook is separate software and is not included with MultiExplorer. If you
enable **QuickLook preview (Space)** and QuickLook is already running,
MultiExplorer sends the path of the selected item to QuickLook through a
user-specific local Windows named pipe. MultiExplorer does not start,
install, or send data over the network to QuickLook.

QuickLook's handling of that path and any previewed content is governed by its
own privacy practices. You can disable this integration in MultiExplorer at
any time.

## No online collection or tracking

MultiExplorer does not:

- send telemetry, usage statistics, crash reports, or diagnostic logs;
- use analytics, advertising, cookies, or tracking technologies;
- create an account or require an email address;
- upload your files, folder paths, settings, or operation details; or
- contact an update, licensing, or other MultiExplorer-operated server.

## Your choices and deleting local data

You can disable auto-start and QuickLook integration from the application
menus. You can remove the application through Windows Settings.

Uninstalling MultiExplorer removes its installed program files, but it is
intended to preserve your preferences. To remove the local information
described in this policy, close MultiExplorer and delete these folders:

```text
%APPDATA%\MultiExplorer
%LOCALAPPDATA%\MultiExplorer
```

Deleting these folders removes saved preferences, the application log, and any
remaining operation records. It does not undo completed Windows file
operations or remove information held by Windows, QuickLook, or other
third-party software.

## Changes to this policy

If MultiExplorer's data-handling practices change, this policy will be updated
in the project repository and the **Last updated** date will change.

## Questions

For privacy questions or to report a concern, open an issue in the
MultiExplorer project repository.
