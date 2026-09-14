using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MultiExplorer;

public class MainForm : Form, IMessageFilter
{
    private const int MinimumPanelWidth = 100;
    private const string ApplicationIconResourceName =
        "MultiExplorer.MultiExplorer-Installer.ico";

    private readonly Panel       _layoutPanel;
    private readonly SplitterBar _splitterBar;
    private readonly PanelView   _leftPanel;
    private readonly PanelView   _rightPanel;

    private int  _splitterLeft;
    private double _splitterRatio = 0.5;
    private bool _splitterInitialized;
    private bool _leftCollapsed;
    private bool _rightCollapsed;
    private readonly System.Windows.Forms.Timer _pathPollTimer;
    private readonly AppSettings    _settings;
    private readonly bool           _startInSystemTray;

    private readonly StatusStrip                  _statusBar;
    private readonly ToolStripStatusLabel         _statusIcon;
    private readonly ToolStripStatusLabel         _statusMessage;
    private readonly ToolStripStatusLabel         _statusDismiss;
    private readonly System.Windows.Forms.Timer   _statusClearTimer;

    private readonly NotifyIcon          _trayIcon;
    private readonly ToolStripMenuItem   _miMinimizeToTray;
    private readonly ContextMenuStrip    _trayMenu;
    private bool _forceClose;
    private bool _skipOperationPrompt;
    private bool _exitWhenOperationsComplete;
    private int  _lastActiveOperationCount;
    private readonly OperationManager _operations;

    // Arbitrary unique ID for the global show-window hotkey
    private const int HotkeyShowWindow = 0x3001;

    // Modifiers and VK that are actually registered (0 = nothing registered)
    private int _registeredModifiers;
    private int _registeredVk;

    public MainForm() : this(SettingsManager.Load(), startInSystemTray: false) { }

    internal MainForm(AppSettings settings) : this(settings, startInSystemTray: false) { }

    internal MainForm(AppSettings settings, bool startInSystemTray)
    {
        _settings = settings;
        _startInSystemTray = startInSystemTray;
        _operations = new OperationManager();

        if (_startInSystemTray)
        {
            // Keep the initial window out of both the taskbar and the visible desktop.
            // Opacity is restored after the first Shown event so later user activation
            // displays the window normally.
            ShowInTaskbar = false;
            Opacity = 0;
        }

        Text = "MultiExplorer";
        using var iconStream = GetType().Assembly.GetManifestResourceStream(ApplicationIconResourceName);
        if (iconStream != null)
        {
            // Icon(Stream) can retain the stream, so clone it before the embedded
            // resource stream is disposed. The form icon is also used by the taskbar.
            using var embeddedIcon = new Icon(iconStream);
            Icon = (Icon)embeddedIcon.Clone();
        }
        MinimumSize = new Size(800, 600);

        _layoutPanel = new Panel { Dock = DockStyle.Fill };
        _leftPanel   = new PanelView();
        _rightPanel  = new PanelView();
        _splitterBar = new SplitterBar();
        _splitterBar.CollapseLeftClicked  += (_, _) => ToggleCollapseLeft();
        _splitterBar.CollapseRightClicked += (_, _) => ToggleCollapseRight();
        _splitterBar.DragMoved            += OnSplitterDragMoved;
        _layoutPanel.Resize               += (_, _) => ApplyLayout();

        _layoutPanel.Controls.Add(_leftPanel);
        _layoutPanel.Controls.Add(_splitterBar);
        _layoutPanel.Controls.Add(_rightPanel);
        Controls.Add(_layoutPanel);

        // Mirror button: each panel's request navigates the opposite panel
        _leftPanel.MirrorToOtherRequested  += (_, path) => _rightPanel.NavigateTo(path);
        _rightPanel.MirrorToOtherRequested += (_, path) => _leftPanel.NavigateTo(path);

        // QuickLook toggle: persist the setting immediately and keep both panels in sync
        _leftPanel.QuickLookToggled  += OnQuickLookToggled;
        _rightPanel.QuickLookToggled += OnQuickLookToggled;

        // Hotkey configuration
        _leftPanel.SetHotkeyRequested  += (_, _) => OnSetHotkeyRequested();
        _rightPanel.SetHotkeyRequested += (_, _) => OnSetHotkeyRequested();

        _leftPanel.StartWithWindowsToggled  += OnStartWithWindowsToggled;
        _rightPanel.StartWithWindowsToggled += OnStartWithWindowsToggled;

        _leftPanel.ThemeSelected  += (_, theme) => ChangeTheme(theme);
        _rightPanel.ThemeSelected += (_, theme) => ChangeTheme(theme);

        // Exit: command bar button or Ctrl+Q
        _leftPanel.ExitRequested  += (_, _) => ExitApplication();
        _rightPanel.ExitRequested += (_, _) => ExitApplication();
        Application.AddMessageFilter(this);

        // Status bar — subscribes to AppLog and shows Warn/Error messages
        _statusIcon = new ToolStripStatusLabel("✓")
        {
            AutoSize  = false,
            Width     = 20,
            ForeColor = ThemeManager.MutedText,
        };
        _statusMessage = new ToolStripStatusLabel("Ready")
        {
            Spring      = true,
            TextAlign   = ContentAlignment.MiddleLeft,
            ForeColor   = ThemeManager.MutedText,
            ToolTipText = "Click to open log file",
        };
        _statusMessage.Click += (_, _) => AppLog.OpenLogFile();
        _statusDismiss = new ToolStripStatusLabel("×")
        {
            AutoSize    = false,
            Width       = 20,
            TextAlign   = ContentAlignment.MiddleCenter,
            Visible     = false,
            ToolTipText = "Dismiss",
        };
        _statusDismiss.Click += (_, _) => ClearStatus();

        _statusBar = new StatusStrip { SizingGrip = false };
        _statusBar.Items.AddRange(new ToolStripItem[] { _statusIcon, _statusMessage, _statusDismiss });
        Controls.Add(_statusBar);

        _statusClearTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        _statusClearTimer.Tick += (_, _) => ClearStatus();
        AppLog.MessageLogged += OnLogMessage;

        _pathPollTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _pathPollTimer.Tick += OnPathPollTick;

        Load        += OnLoad;
        FormClosing += OnFormClosing;
        _miMinimizeToTray = new ToolStripMenuItem("Minimize to tray on close")
        {
            Checked      = _settings.MinimizeToTray,
            CheckOnClick = true,
        };
        _miMinimizeToTray.CheckedChanged += (_, _) =>
        {
            _settings.MinimizeToTray = _miMinimizeToTray.Checked;
            SettingsManager.Save(_settings);
        };

        var openItem = new ToolStripMenuItem("Open MultiExplorer");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) => ShowMainWindow();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        var aboutItem = new ToolStripMenuItem("About MultiExplorer");
        aboutItem.Click += (_, _) => PanelView.ShowAboutDialog(this);

        var viewLogItem = new ToolStripMenuItem("View log");
        viewLogItem.Click += (_, _) => AppLog.OpenLogFile();

        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_miMinimizeToTray);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(aboutItem);
        _trayMenu.Items.Add(viewLogItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            // Keep the notification-area icon in sync with the taskbar icon.
            Icon             = this.Icon,
            Text             = "MultiExplorer",
            ContextMenuStrip = _trayMenu,
            Visible          = false,
        };
        _trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
        _trayIcon.BalloonTipClicked += (_, _) => ShowMainWindow();

        _operations.OperationsChanged += OnOperationsChanged;
        _operations.OperationsBecameIdle += OnOperationsBecameIdle;
        _lastActiveOperationCount = _operations.ActiveCount;
        UpdateOperationTrayStatus();

        UpdateStartWithWindowsUi();
        SynchronizeStartupRegistration();

        RestoreWindowState();
        ApplyApplicationTheme();
    }

    private void ChangeTheme(ApplicationTheme theme)
    {
        if (theme == ThemeManager.Current) return;
        ThemeManager.SetCurrent(theme);
        _settings.ApplicationTheme = theme.ToString();
        SettingsManager.Save(_settings);

        // Explorer's DirectUI file and folder views bind to their theme when
        // created. Recreate them after changing the process preference so the
        // entire view, including selection visuals, changes immediately.
        _leftPanel.RecreateShellViewsForTheme();
        _rightPanel.RecreateShellViewsForTheme();
        ApplyApplicationTheme();
    }

    private void ApplyApplicationTheme()
    {
        SuspendLayout();
        ThemeManager.ApplyTo(this);
        _leftPanel.ApplyTheme();
        _rightPanel.ApplyTheme();
        ThemeManager.ApplyToolStrip(_statusBar);
        ThemeManager.ApplyToolStrip(_trayMenu);
        BackColor = ThemeManager.Background;
        ForeColor = ThemeManager.Text;
        ClearStatus();
        if (IsHandleCreated) ThemeManager.ApplyNativeWindow(Handle);
        ResumeLayout(true);
        Refresh();
    }

    private void OnLoad(object? sender, EventArgs e) => Shown += OnShown;

    private void OnShown(object? sender, EventArgs e)
    {
        Shown -= OnShown;

        _leftCollapsed     = _settings.LeftPanelCollapsed;
        _rightCollapsed    = _settings.RightPanelCollapsed;
        if (_settings.SplitterPositionVersion < AppSettings.CurrentSplitterPositionVersion)
        {
            // Apply a newly chosen product default once to existing installations.
            // Merely changing AppSettings.SplitterDistance does not affect an existing
            // settings file because its saved SplitterRatio otherwise wins here.
            _splitterLeft = AppSettings.DefaultSplitterDistance;
            _splitterRatio = CalculateSplitterRatio(_splitterLeft, _layoutPanel.Width);
            _settings.SplitterPositionVersion = AppSettings.CurrentSplitterPositionVersion;
        }
        else if (_settings.SplitterRatio > 0 && _settings.SplitterRatio < 1)
        {
            _splitterRatio = _settings.SplitterRatio;
            _splitterLeft = SplitterLeftFromRatio(_splitterRatio, _layoutPanel.Width);
        }
        else
        {
            _splitterLeft = _settings.SplitterDistance > 0
                            ? _settings.SplitterDistance
                            : _layoutPanel.Width / 2;
            _splitterRatio = CalculateSplitterRatio(_splitterLeft, _layoutPanel.Width);
        }
        _splitterInitialized = true;

        // Browsers require non-zero bounds to initialise, so launch with both panels
        // visible at full size, then apply the saved collapse state afterwards.
        bool savedLc = _leftCollapsed, savedRc = _rightCollapsed;
        _leftCollapsed = false; _rightCollapsed = false;
        ApplyLayout();

        if (_settings.WindowWidth == 0)
            _settings.QuickLookEnabled = IsQuickLookAvailable();

        ExplorerHost.QuickLookEnabled = _settings.QuickLookEnabled;

        var leftPaths  = _settings.LeftPanelTabs.Count  > 0 ? _settings.LeftPanelTabs
                       : new List<string> { _settings.LeftPanelPath };
        var rightPaths = _settings.RightPanelTabs.Count > 0 ? _settings.RightPanelTabs
                       : new List<string> { _settings.RightPanelPath };

        _leftPanel.SetPathHistory(_settings.LeftPathHistory);
        _rightPanel.SetPathHistory(_settings.RightPathHistory);

        // ExplorerBrowser's two navigation trees share Shell image-list state.
        // Initializing both on separate STA threads at exactly the same time can
        // leave either tree with blank icon slots until that browser is recreated.
        // Start the second pane once the first navigation has finished; the two
        // browser threads remain fully independent after startup.
        EventHandler? launchRightPanel = null;
        launchRightPanel = (_, _) =>
        {
            _leftPanel.InitialBrowserReady -= launchRightPanel;
            if (!IsDisposed && !Disposing)
                _rightPanel.Launch(rightPaths);
        };
        _leftPanel.InitialBrowserReady += launchRightPanel;
        _leftPanel.Launch(leftPaths);

        _leftCollapsed  = savedLc;
        _rightCollapsed = savedRc;
        if (_leftCollapsed || _rightCollapsed)
            ApplyLayout();

        _pathPollTimer.Start();
        if (_startInSystemTray)
            BeginInvoke(StartInSystemTray);
    }

    private void StartInSystemTray()
    {
        if (IsDisposed || Disposing) return;

        _pathPollTimer.Stop();
        _trayIcon.Visible = true;
        Hide();
        Opacity = 1;
        _trayIcon.ShowBalloonTip(
            5000,
            "MultiExplorer",
            "MultiExplorer started with Windows and is running in the system tray.",
            ToolTipIcon.Info);
    }

    private void OnPathPollTick(object? sender, EventArgs e)
    {
        _leftPanel.PollPath();
        _rightPanel.PollPath();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        bool userRequestedExit = _forceClose
            || e.CloseReason == CloseReason.ApplicationExitCall
            || (e.CloseReason == CloseReason.UserClosing && !_settings.MinimizeToTray);
        if (!_skipOperationPrompt && _operations.ActiveCount > 0 && userRequestedExit)
        {
            using var dialog = new OperationExitDialog(
                _operations.ActiveCount, _operations.DescribeActiveOperations());
            dialog.ShowDialog(this);
            switch (dialog.Choice)
            {
                case OperationExitChoice.ExitAndContinue:
                    break;
                case OperationExitChoice.WaitInTray:
                    e.Cancel = true;
                    _forceClose = false;
                    _exitWhenOperationsComplete = true;
                    SaveSettings();
                    _pathPollTimer.Stop();
                    _trayIcon.Visible = true;
                    Hide();
                    return;
                case OperationExitChoice.CancelAndExit:
                    e.Cancel = true;
                    _forceClose = false;
                    _exitWhenOperationsComplete = true;
                    _operations.CancelAll();
                    SaveSettings();
                    _pathPollTimer.Stop();
                    _trayIcon.Visible = true;
                    Hide();
                    return;
                default:
                    e.Cancel = true;
                    _forceClose = false;
                    return;
            }
        }

        if (!_forceClose && e.CloseReason == CloseReason.UserClosing && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            SaveSettings();
            _pathPollTimer.Stop();
            _trayIcon.Visible = true;
            Hide();
            return;
        }

        Application.RemoveMessageFilter(this);
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _pathPollTimer.Stop();
        _pathPollTimer.Dispose();
        _statusClearTimer.Stop();
        _statusClearTimer.Dispose();
        AppLog.MessageLogged -= OnLogMessage;
        _operations.OperationsChanged -= OnOperationsChanged;
        _operations.OperationsBecameIdle -= OnOperationsBecameIdle;
        _operations.Dispose();

        SaveSettings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operations.OperationsChanged -= OnOperationsChanged;
            _operations.OperationsBecameIdle -= OnOperationsBecameIdle;
            _operations.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SaveSettings()
    {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowX          = bounds.X;
        _settings.WindowY          = bounds.Y;
        _settings.WindowWidth      = bounds.Width;
        _settings.WindowHeight     = bounds.Height;
        _settings.WindowState      = WindowState == FormWindowState.Maximized ? "Maximized" : "Normal";
        _settings.SplitterDistance    = _splitterLeft;
        _settings.SplitterRatio       = _splitterRatio;
        _settings.LeftPanelCollapsed  = _leftCollapsed;
        _settings.RightPanelCollapsed = _rightCollapsed;

        _settings.LeftPanelTabs  = _leftPanel.GetAllPaths();
        _settings.RightPanelTabs = _rightPanel.GetAllPaths();
        _settings.LeftPanelPath  = _leftPanel.CurrentPath();
        _settings.RightPanelPath = _rightPanel.CurrentPath();
        _settings.LeftPathHistory  = _leftPanel.GetPathHistory();
        _settings.RightPathHistory = _rightPanel.GetPathHistory();

        SettingsManager.Save(_settings);
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Opacity = 1;
        Show();
        WindowState = _settings.WindowState == "Maximized"
                          ? FormWindowState.Maximized
                          : FormWindowState.Normal;
        Activate();
        BringToFront();
        _trayIcon.Visible = false;
        _pathPollTimer.Start();
    }

    private void ExitApplication()
    {
        _forceClose = true;
        Close();
    }

    private void OnOperationsChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnOperationsChanged(sender, e)); }
            catch (InvalidOperationException) { }
            return;
        }

        int active = _operations.ActiveCount;
        if (active < _lastActiveOperationCount)
        {
            _leftPanel.RefreshCurrentFolder();
            _rightPanel.RefreshCurrentFolder();
        }
        _lastActiveOperationCount = active;
        UpdateOperationTrayStatus();
    }

    private void OnOperationsBecameIdle(object? sender, EventArgs e)
    {
        if (!_exitWhenOperationsComplete) return;
        _exitWhenOperationsComplete = false;
        _skipOperationPrompt = true;
        _forceClose = true;
        Close();
    }

    private void UpdateOperationTrayStatus()
    {
        int active = _operations.ActiveCount;
        _trayIcon.Text = active == 0
            ? "MultiExplorer"
            : $"MultiExplorer — {active} file operation(s) active";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyNativeWindow(Handle, includeChildren: false);
        RegisterHotKeyFromSettings();
    }

    private void RegisterHotKeyFromSettings()
    {
        int mods = _settings.ShowWindowModifiers;
        int vk   = _settings.ShowWindowVk;

        if (NativeMethods.RegisterHotKey(Handle, HotkeyShowWindow,
                mods | NativeMethods.MOD_NOREPEAT, vk))
        {
            _registeredModifiers = mods;
            _registeredVk        = vk;
            return;
        }

        // Silent fallback: try Ctrl+Alt+M (only if that differs from what was configured)
        int fbMods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        int fbVk   = NativeMethods.VK_M;
        if ((mods != fbMods || vk != fbVk)
            && NativeMethods.RegisterHotKey(Handle, HotkeyShowWindow,
                   fbMods | NativeMethods.MOD_NOREPEAT, fbVk))
        {
            _registeredModifiers = fbMods;
            _registeredVk        = fbVk;
            return;
        }

        _registeredModifiers = 0;
        _registeredVk        = 0;
        AppLog.Warn(null, "GlobalHotkey",
            $"{HotkeyDialog.Describe(mods, vk)} could not be registered; use the tray icon to restore the window.");
    }

    private void OnSetHotkeyRequested()
    {
        // Show the dialog pre-filled with what is currently active (registered),
        // falling back to the configured value if nothing is registered.
        int curMods = _registeredModifiers != 0 ? _registeredModifiers : _settings.ShowWindowModifiers;
        int curVk   = _registeredModifiers != 0 ? _registeredVk        : _settings.ShowWindowVk;

        using var dlg = new HotkeyDialog(curMods, curVk);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int newMods = dlg.SelectedModifiers;
        int newVk   = dlg.SelectedVk;
        if (newMods == _registeredModifiers && newVk == _registeredVk) return;

        NativeMethods.UnregisterHotKey(Handle, HotkeyShowWindow);

        if (NativeMethods.RegisterHotKey(Handle, HotkeyShowWindow,
                newMods | NativeMethods.MOD_NOREPEAT, newVk))
        {
            _registeredModifiers          = newMods;
            _registeredVk                 = newVk;
            _settings.ShowWindowModifiers = newMods;
            _settings.ShowWindowVk        = newVk;
            SettingsManager.Save(_settings);
        }
        else
        {
            // Restore the previous registration and inform the user
            if (_registeredModifiers != 0)
                NativeMethods.RegisterHotKey(Handle, HotkeyShowWindow,
                    curMods | NativeMethods.MOD_NOREPEAT, curVk);

            MessageBox.Show(this,
                $"{HotkeyDialog.Describe(newMods, newVk)} is already in use by another application.",
                "Could not register hotkey",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(Handle, HotkeyShowWindow);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)Program.WM_SHOW_INSTANCE ||
            (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyShowWindow))
        {
            ShowMainWindow();
            return;
        }
        base.WndProc(ref m);
    }

    public bool PreFilterMessage(ref Message m)
    {
        // Do not treat these as process-global hotkeys while the main window is
        // hidden in the notification area. ExplorerHost separately forwards the
        // shortcuts from native browser STAs while the window is visible.
        CommandBar.Cmd? shortcut = GetApplicationShortcut(
            m.Msg, m.WParam, Control.ModifierKeys);
        if (Visible && shortcut.HasValue)
        {
            _leftPanel.ExecuteCommand(shortcut.Value);
            return true;
        }
        return false;
    }

    internal static CommandBar.Cmd? GetApplicationShortcut(
        int message, IntPtr virtualKey, Keys modifiers)
    {
        const int WM_KEYDOWN = 0x0100;
        if (message != WM_KEYDOWN || modifiers != Keys.Control)
            return null;

        return virtualKey.ToInt32() switch
        {
            0x4F => CommandBar.Cmd.FolderOptions, // O
            0x4C => CommandBar.Cmd.ViewLog,       // L
            0x51 => CommandBar.Cmd.Exit,          // Q
            _    => null,
        };
    }

    internal static bool IsQuitShortcut(int message, IntPtr virtualKey, Keys modifiers)
        => GetApplicationShortcut(message, virtualKey, modifiers) == CommandBar.Cmd.Exit;

    private static bool IsQuickLookAvailable()
    {
        if (Process.GetProcessesByName("QuickLook").Length > 0) return true;
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         @"Programs\QuickLook\QuickLook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         @"QuickLook\QuickLook.exe"),
        ];
        return candidates.Any(File.Exists);
    }

    private void ApplyLayout()
    {
        int tw = _layoutPanel.Width;
        int th = _layoutPanel.Height;
        int bw = SplitterBar.BarWidth;

        // Minimizing a WinForms window can briefly resize docked controls to
        // zero (or another unusably small width).  Do not let that transient
        // size clamp and overwrite the divider position we need on restore.
        if (!_leftCollapsed && !_rightCollapsed
            && !CanLayoutExpandedPanels(tw))
            return;

        _splitterBar.LeftCollapsed  = _leftCollapsed;
        _splitterBar.RightCollapsed = _rightCollapsed;

        if (_leftCollapsed)
        {
            _leftPanel.Visible  = false;
            _splitterBar.Bounds = new Rectangle(0, 0, bw, th);
            _rightPanel.Visible = true;
            _rightPanel.Bounds  = new Rectangle(bw, 0, Math.Max(0, tw - bw), th);
        }
        else if (_rightCollapsed)
        {
            int sl = Math.Max(0, tw - bw);
            _leftPanel.Visible  = true;
            _leftPanel.Bounds   = new Rectangle(0, 0, sl, th);
            _splitterBar.Bounds = new Rectangle(sl, 0, bw, th);
            _rightPanel.Visible = false;
        }
        else
        {
            if (_splitterInitialized)
                _splitterLeft = SplitterLeftFromRatio(_splitterRatio, tw);
            _splitterLeft = ConstrainSplitterLeft(_splitterLeft, tw);

            _leftPanel.Visible  = true;
            _leftPanel.Bounds   = new Rectangle(0, 0, _splitterLeft, th);
            _splitterBar.Bounds = new Rectangle(_splitterLeft, 0, bw, th);
            _rightPanel.Visible = true;
            _rightPanel.Bounds  = new Rectangle(_splitterLeft + bw, 0,
                                                 Math.Max(0, tw - _splitterLeft - bw), th);
        }

        _splitterBar.Invalidate();
    }

    internal static int ConstrainSplitterLeft(int splitterLeft, int layoutWidth)
    {
        int maxLeft = layoutWidth - SplitterBar.BarWidth - MinimumPanelWidth;
        return Math.Clamp(splitterLeft, MinimumPanelWidth, maxLeft);
    }

    internal static bool CanLayoutExpandedPanels(int layoutWidth) =>
        layoutWidth >= MinimumPanelWidth * 2 + SplitterBar.BarWidth;

    internal static double CalculateSplitterRatio(int splitterLeft, int layoutWidth)
    {
        int usableWidth = layoutWidth - SplitterBar.BarWidth;
        if (usableWidth <= 0) return 0.5;
        return Math.Clamp((double)splitterLeft / usableWidth, 0, 1);
    }

    internal static int SplitterLeftFromRatio(double ratio, int layoutWidth)
    {
        int usableWidth = Math.Max(0, layoutWidth - SplitterBar.BarWidth);
        int splitterLeft = (int)Math.Round(usableWidth * Math.Clamp(ratio, 0, 1));
        return ConstrainSplitterLeft(splitterLeft, layoutWidth);
    }

    private void ToggleCollapseLeft()
    {
        if (_leftCollapsed)
        {
            _leftCollapsed = false;
            _splitterLeft = SplitterLeftFromRatio(_splitterRatio, _layoutPanel.Width);
        }
        else if (!_rightCollapsed)
        {
            _leftCollapsed     = true;
        }
        ApplyLayout();
    }

    private void ToggleCollapseRight()
    {
        if (_rightCollapsed)
        {
            _rightCollapsed = false;
            _splitterLeft = SplitterLeftFromRatio(_splitterRatio, _layoutPanel.Width);
        }
        else if (!_leftCollapsed)
        {
            _rightCollapsed    = true;
        }
        ApplyLayout();
    }

    private void OnSplitterDragMoved(object? sender, int newLeft)
    {
        if (_leftCollapsed || _rightCollapsed) return;
        _splitterLeft = ConstrainSplitterLeft(newLeft, _layoutPanel.Width);
        _splitterRatio = CalculateSplitterRatio(_splitterLeft, _layoutPanel.Width);
        ApplyLayout();
    }

    private void OnLogMessage(LogEntry entry)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnLogMessage(entry)); }
            catch (InvalidOperationException) { }
            return;
        }

        _statusClearTimer.Stop();
        _statusIcon.Text      = entry.Severity == LogSeverity.Error ? "✕" : "⚠";
        _statusIcon.ForeColor = entry.Severity == LogSeverity.Error ? ThemeManager.Error : ThemeManager.Warning;
        _statusMessage.Text      = entry.ShortMessage;
        _statusMessage.ForeColor = ThemeManager.Text;
        _statusMessage.IsLink    = true;
        _statusDismiss.Visible   = true;

        if (entry.Severity == LogSeverity.Warn)
            _statusClearTimer.Start(); // auto-clear warnings after 6 s; errors persist
    }

    private void OnQuickLookToggled(object? sender, EventArgs e)
    {
        _settings.QuickLookEnabled = ExplorerHost.QuickLookEnabled;
        SettingsManager.Save(_settings);
    }

    private void OnStartWithWindowsToggled(object? sender, EventArgs e)
    {
        bool enabled = !_settings.StartWithWindows;
        if (!StartupManager.TrySetEnabled(enabled, out Exception? error))
        {
            AppLog.Warn(error!, nameof(OnStartWithWindowsToggled),
                $"Could not {(enabled ? "enable" : "disable")} start with Windows.");
            return;
        }

        _settings.StartWithWindows = enabled;
        SettingsManager.Save(_settings);
        UpdateStartWithWindowsUi();
    }

    private void SynchronizeStartupRegistration()
    {
        if (!StartupManager.TrySetEnabled(_settings.StartWithWindows, out Exception? error))
            AppLog.Warn(error!, nameof(SynchronizeStartupRegistration),
                "Could not synchronize the start-with-Windows preference.");
    }

    private void UpdateStartWithWindowsUi()
    {
        _leftPanel.SetStartWithWindowsChecked(_settings.StartWithWindows);
        _rightPanel.SetStartWithWindowsChecked(_settings.StartWithWindows);
    }

    private void ClearStatus()
    {
        _statusClearTimer.Stop();
        _statusIcon.Text         = "✓";
        _statusIcon.ForeColor    = ThemeManager.MutedText;
        _statusMessage.Text      = "Ready";
        _statusMessage.ForeColor = ThemeManager.MutedText;
        _statusMessage.IsLink    = false;
        _statusDismiss.Visible   = false;
    }

    private void RestoreWindowState()
    {
        StartPosition = FormStartPosition.Manual;

        Rectangle screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

        int x, y, w, h;
        if (_settings.WindowWidth <= 0 || _settings.WindowHeight <= 0)
        {
            // First run — size to 80 % of the current screen and center it
            w = (int)(screen.Width  * 0.8);
            h = (int)(screen.Height * 0.8);
            x = screen.Left + (screen.Width  - w) / 2;
            y = screen.Top  + (screen.Height - h) / 2;
        }
        else
        {
            x = Math.Clamp(_settings.WindowX,      screen.Left,        screen.Right  - 400);
            y = Math.Clamp(_settings.WindowY,       screen.Top,         screen.Bottom - 300);
            w = Math.Clamp(_settings.WindowWidth,   MinimumSize.Width,  screen.Width);
            h = Math.Clamp(_settings.WindowHeight,  MinimumSize.Height, screen.Height);
        }

        Location    = new Point(x, y);
        Size        = new Size(w, h);
        WindowState = _settings.WindowState == "Maximized"
                          ? FormWindowState.Maximized
                          : FormWindowState.Normal;
    }
}
