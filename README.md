# MultiExplorer

A dual-pane Windows file explorer built on the native Windows Shell (`IExplorerBrowser` COM interface), written in C# .NET 8.0 WinForms.

**Author:** David Piscopo  
**License:** [CC0 1.0 Universal Public Domain Dedication](https://creativecommons.org/publicdomain/zero/1.0/). No restrictions, no attribution required.  
**Platform:** Windows 10 / Windows 11 (x64)

---

## Features

### Dual-pane layout

Two independent explorer panels sit side-by-side, separated by a draggable splitter. Each panel is a fully native Windows Shell view. The same rendering engine as File Explorer so shell extensions, thumbnails, file icons, and context menus all work exactly as they would in Explorer.

Each panel can be **collapsed** to a thin bar using the arrow buttons on the splitter, giving the remaining panel the full window width. Clicking the arrow again restores the previous split.

### Tabs

Each pane supports multiple tabs. Tabs can be:

- **Added**: click the **+** button at the end of the tab bar.
- **Closed**: click the **×** on any tab (a pane always keeps at least one tab open).
- **Switched**: click any tab label.

Hovering a tab shows a tooltip with the full folder path. All open tab paths are saved and restored between sessions.

### Breadcrumb path bar

The path bar beneath the tab bar renders the current folder path as clickable breadcrumb segments separated by **›** chevrons.

- **Click a segment**: navigate directly to that ancestor folder.
- **Click a chevron**: open a dropdown menu listing the immediate sub-folders of the folder to its left.
- **Click blank space, press F4, or press Enter**: switch to a plain text editor for typing or pasting any path. Environment variables (e.g. `%USERPROFILE%`) are expanded on Enter. UNC paths (e.g. `\\server\share`) are supported. Press **Escape** to cancel.

### Toolbar (command bar)

| Button | Shortcut | Action |
|--------|----------|--------|
| New folder | Ctrl+Shift+N | Create a new folder and immediately start renaming it |
| Cut | Ctrl+X | Cut selected item(s) |
| Copy | Ctrl+C | Copy selected item(s) |
| Copy full paths | — | Copy the full paths of all selected files and folders, one per line |
| Paste | Ctrl+V | Paste from clipboard |
| Rename | F2 | Rename the selected item |
| Delete | Del | Delete the selected item(s) |
| View ▾ | — | Change view mode and toggle panes (see below) |
| ↑ (Go up) | — | Navigate to the parent folder |
| ⇄ (Mirror) | — | Navigate the **opposite** pane to the same folder as the active pane |
| **… ▾** | — | Options, light/dark appearance, log viewer, hotkey settings, About, Exit |

### Application appearance

MultiExplorer provides complete light and dark application modes. To change the appearance from either pane:

1. Click **… ▾** at the far right of the toolbar.
2. Open **Appearance**.
3. Choose **Light** or **Dark**.

The selected mode applies to the entire application, including:

- Both panes and every open tab.
- Toolbars, menus, status bars, path bars, and the splitter.
- Dialogs, filtering results, details, and preview panes.
- Embedded Windows Explorer folder views, navigation trees, scrollbars, and selection highlighting.

The change takes effect without restarting MultiExplorer. The embedded Explorer views briefly reload so Windows can apply the new native theme correctly; their current folders and selected items are preserved where possible.

The selected mode is stored as `ApplicationTheme` in `%APPDATA%\MultiExplorer\settings.json` and is restored on the next launch. Light mode is used by default for a new installation.

For testing or screenshot automation, the persisted selection can be overridden for one launch from the command line:

```powershell
MultiExplorer.exe --theme=light
MultiExplorer.exe --theme=dark
```

The command-line override is not saved and does not change the mode selected in the settings file.

### Type-to-filter

Start typing any printable character while the file list has focus to instantly filter the current folder's contents. There is no need to press a dedicated key — typing begins filtering immediately.

A yellow **Contains:** bar appears at the bottom of the panel showing the current filter text, and a results list overlays the shell view:

- Results show **Name**, **Type**, **Size**, and **Date modified** columns. Folders are listed first (alphabetically), followed by files (alphabetically).
- **Double-click or Enter**: navigate into the matched folder, or open the matched file with its default application.
- **Right-click**: shows the full Windows shell context menu for the item (copy, delete, properties, open with, etc.).
- **Backspace**: removes the last character from the filter.
- **Escape** or click **×**: clears the filter and returns focus to the shell view.
- Typing continues to refine the filter while the results list is open.

### View modes

Accessible from the **View** dropdown on the toolbar:

| Mode | Description |
|------|-------------|
| Details | Columns with name, date, size, type |
| List | Compact multi-column list |
| Tiles | Large icon with two lines of metadata |
| Large icons | 96 px thumbnails |
| Medium icons | 48 px thumbnails |
| Small icons | Small icons in a grid |
| Content | Wide rows with extended metadata |

### Show submenu (under View)

Toggles for shell view options. **Navigation pane**, **Compact view** and **QuickLook** are per-session; the remaining options write to the same Windows Registry keys used by File Explorer and take effect globally:

- **Navigation pane**: left-side folder tree
- **Compact view**: reduced row height in Details mode
- **Item check boxes**: selection checkboxes on every item
- **File name extensions**: show/hide file extensions
- **Hidden items**: show/hide hidden and system files

### Panes

Two optional content panes can be toggled from the **View** dropdown:

#### Details pane (bottom)

Shows metadata for the currently selected item: shell icon, file name, type description, file size (formatted as B / KB / MB / GB), and last-modified date. Updated every 300 ms as the selection changes.

#### Preview pane (right)

Hosts the shell `IPreviewHandler` COM extension registered for the selected file's extension (e.g. the built-in PDF preview, Office document preview, image preview, or plain-text preview). Falls back gracefully when no handler is registered.

### QuickLook integration

When [QuickLook](https://github.com/QL-Win/QuickLook) is installed, pressing **Space** sends the currently selected file to QuickLook for an instant preview in a floating window. Enable or disable this from **View → QuickLook preview (Space)**. The setting is saved across sessions.

MultiExplorer first tries to communicate with a running QuickLook instance via its named pipe; if QuickLook is not running it attempts to launch it automatically.

### Global show-window hotkey

A global hotkey brings the MultiExplorer window to the foreground from any application, even when hidden in the system tray.

**Default hotkey: Win + Ctrl + Alt + M**

Change the hotkey any time via **… → Set show-window hotkey…**. The dialog lets you pick any combination of Win / Ctrl / Alt / Shift plus a letter (A–Z) or function key (F1–F12). The choice is saved to `settings.json` and re-registered at the next launch.

If the configured hotkey is claimed by another application, MultiExplorer silently falls back to **Ctrl+Alt+M**. If that is also unavailable, a note is written to the log and the tray icon can still restore the window.

### System tray

When **Minimize to tray on close** is enabled (the default), closing the main window hides it to the system tray rather than exiting. The tray icon provides:

- **Left-click** or **Open MultiExplorer**: restore the window
- **Minimize to tray on close**: toggle the behaviour
- **View log**: open the application log in the default text editor
- **About MultiExplorer**: version and licence information
- **Exit**: fully quit the application

### Single-instance enforcement

Only one instance of MultiExplorer runs at a time. If a second instance is launched while one is already running, it tells the first instance to show its window and then exits immediately.

---

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| **Ctrl+Q** | Exit MultiExplorer |
| **Ctrl+Shift+N** | Create a new folder |
| **F2** | Rename the selected item |
| **Del** | Delete the selected item(s) |
| **Ctrl+X / C / V** | Cut / Copy / Paste |
| **Ctrl+A** | Select all items |
| **Alt+Enter** | Show properties for the selected item |
| **Space** | QuickLook preview of the selected file (when enabled) |
| **F4** or **Enter** | Edit the path bar directly |
| **Escape** | Cancel path bar edit; or clear the active filter |
| **Backspace** (in filter) | Remove the last filter character |
| **Double-click blank space** | Navigate to the parent folder |
| **Win+Ctrl+Alt+M** | Show MultiExplorer window (global, configurable) |

All standard Windows Explorer keyboard shortcuts (Backspace to go up, Alt+Left/Right for history, F5 to refresh, etc.) also work because key presses are forwarded to the shell view via `IShellView::TranslateAccelerator`.

---

## Installation

### Using the MSI installer (recommended)

Download `MultiExplorer-Setup.msi` from the Releases page and run it. The installer:

- Installs to `%LocalAppData%\Programs\MultiExplorer\` (no administrator rights required)
- Reuses the existing installation directory when upgrading a previous version
- Creates a **Desktop shortcut** and a **Start Menu** entry
- Registers in **Apps & features** so it can be fully uninstalled from there
- Closes any running instance automatically before installing, updating, or uninstalling
- Removes the previous version automatically during an upgrade

To uninstall, go to **Settings → Apps → Apps & features**, find MultiExplorer, and click Uninstall.

### Using the standalone app

If you prefer not to use the installer, copy the complete publish folder. It is
fully self-contained and runs without installation or a separate .NET runtime.
Keep every published file and language subfolder beside `MultiExplorer.exe`.
The small entry executable avoids the long first-launch scan incurred by a
single 160+ MB executable.

To launch MultiExplorer at login without the installer, place a shortcut in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`.

---

## Building from source

**Prerequisites**

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows)
- Windows 10 or 11

**Development build**

```bat
dotnet build
```

**Release — self-contained ReadyToRun folder**

```bat
dotnet publish -c Release
```

Output: `bin\Release\net8.0-windows\win-x64\publish\`

Copy the complete output folder. It runs on any x64 Windows 10/11 machine
without a separate .NET installation.

**MSI installer**

Requires [WiX Toolset v4](https://wixtoolset.org/) (downloaded automatically by `dotnet build` via NuGet on first run).

```powershell
.\Build-Installer.ps1 -Version 1.0.0
```

Options:

| Flag | Description |
|------|-------------|
| `-Version x.y.z` | Version embedded in the app and MSI (defaults to `Directory.Build.props`) |
| `-BuildDate yyyy-MM-dd` | Build date shown in setup (defaults to today's date) |
| `-SkipPublish` | Skip `dotnet publish`; use the existing publish folder (includes a staleness check) |
| `-SkipSigning` | Produce the MSI without code-signing it |
| `-Force` | With `-SkipPublish`, bypass the staleness check |

The script publishes the app, builds the MSI with WiX, and signs it with a self-signed certificate created automatically on first run (requires the Windows 10/11 SDK for `signtool.exe`). Output: `MultiExplorer.Installer\bin\Release\en-US\MultiExplorer-Setup.msi`.

---

## File locations

| File | Location |
|------|----------|
| Settings | `%APPDATA%\MultiExplorer\settings.json` |
| Log file | `%LOCALAPPDATA%\MultiExplorer\app.log` |

### Settings reference

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `WindowX` / `WindowY` | int | 0 | Screen position of the restored window |
| `WindowWidth` / `WindowHeight` | int | 0 | Size of the restored window (0 = first-run default: 80% of screen) |
| `WindowState` | string | `"Normal"` | `"Normal"` or `"Maximized"` |
| `SplitterDistance` | int | 1034 | Pixel position of the vertical splitter |
| `LeftPanelCollapsed` / `RightPanelCollapsed` | bool | `false` | Whether each panel is collapsed |
| `LeftPanelPath` / `RightPanelPath` | string | `"C:\"` | Active folder path for each panel |
| `LeftPanelTabs` / `RightPanelTabs` | string[] | `["C:\\"]` | Folder path for every open tab in each panel |
| `MinimizeToTray` | bool | `true` | Whether closing the window hides to tray |
| `QuickLookEnabled` | bool | auto-detected | Whether Space triggers QuickLook preview |
| `ApplicationTheme` | string | `"Light"` | Selected `Light` or `Dark` application appearance |
| `ShowWindowModifiers` | int | `0x000B` | Modifier flags for the global hotkey (Win\|Ctrl\|Alt) |
| `ShowWindowVk` | int | `0x4D` | Virtual-key code for the global hotkey (`0x4D` = M) |

### Log file

Warnings and errors appear in the status bar at the bottom of the window and are clickable (opens the log in your default text editor). The log rolls over at 512 KB by dropping the oldest half. Debug-level entries are written to the log but not shown in the status bar.

---

## Architecture overview

| Component | File | Role |
|-----------|------|------|
| `Program` | `Program.cs` | Entry point; single-instance mutex; DPI setup |
| `MainForm` | `MainForm.cs` | Top-level window; splitter; panel collapse; tray icon; status bar; global hotkey |
| `PanelView` | `PanelView.cs` | One explorer pane; tab management; type-to-filter overlay; command dispatch |
| `ExplorerHost` | `ExplorerHost.cs` | Embeds `IExplorerBrowser` COM; file operations; view-mode control; keyboard routing |
| `CommandBar` | `CommandBar.cs` | Toolbar with icon buttons and dropdown menus |
| `TabBar` | `TabBar.cs` | Custom-drawn tab strip |
| `PathBar` | `PathBar.cs` | Breadcrumb path bar with inline text editor |
| `DetailsPanel` | `DetailsPanel.cs` | Bottom pane showing shell icon and file metadata |
| `PreviewPane` | `PreviewPane.cs` | Right pane hosting the `IPreviewHandler` shell extension |
| `HotkeyDialog` | `HotkeyDialog.cs` | Modal dialog for choosing a global hotkey combination |
| `ThemeManager` | `ThemeManager.cs` | Shared palette, menu renderer, and native Windows/Explorer theming |
| `AppSettings` | `AppSettings.cs` | Settings data model |
| `SettingsManager` | `SettingsManager.cs` | JSON serialisation to `%APPDATA%\MultiExplorer\settings.json` |
| `AppLog` | `AppLog.cs` | Static logger; Debug → file only; Warn/Error → file + status bar |
| `NativeMethods` | `NativeMethods.cs` | P/Invoke declarations and COM interface definitions |

The shell view is hosted by creating an `IExplorerBrowser` (CLSID `71F96385-DDD6-48D3-A0C1-AE06E8B055FB`) as a child window inside each `ExplorerHost` panel. Shell accelerator keys are forwarded from the WinForms message loop to the shell view via `IMessageFilter` and `IShellView::TranslateAccelerator`. The type-to-filter overlay intercepts printable keystrokes before they reach the shell view and renders results in a managed `ListView` that sits above the native shell HWND.

---

## Privacy

MultiExplorer operates entirely offline. It collects no user data, sends no telemetry, and contains no advertisements.

---

## Licence

This is free and unencumbered software released into the public domain under the [Creative Commons CC0 1.0 Universal Public Domain Dedication](https://creativecommons.org/publicdomain/zero/1.0/). You may copy, modify, distribute, and use this work for any purpose, commercial or non-commercial, without asking permission.
