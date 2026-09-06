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
    private readonly Panel       _layoutPanel;
    private readonly SplitterBar _splitterBar;
    private readonly PanelView   _leftPanel;
    private readonly PanelView   _rightPanel;

    private int  _splitterLeft;
    private int  _savedSplitterLeft;
    private bool _leftCollapsed;
    private bool _rightCollapsed;
    private readonly System.Windows.Forms.Timer _pathPollTimer;
    private readonly AppSettings    _settings;

    private readonly StatusStrip                  _statusBar;
    private readonly ToolStripStatusLabel         _statusIcon;
    private readonly ToolStripStatusLabel         _statusMessage;
    private readonly ToolStripStatusLabel         _statusDismiss;
    private readonly System.Windows.Forms.Timer   _statusClearTimer;

    private readonly NotifyIcon          _trayIcon;
    private readonly ToolStripMenuItem   _miMinimizeToTray;
    private bool _forceClose;

    // Arbitrary unique ID for the global show-window hotkey
    private const int HotkeyShowWindow = 0x3001;

    // Modifiers and VK that are actually registered (0 = nothing registered)
    private int _registeredModifiers;
    private int _registeredVk;

    public MainForm()
    {
        _settings = SettingsManager.Load();

        Text = "MultiExplorer";
        var iconStream = GetType().Assembly.GetManifestResourceStream("MultiExplorer.file-explorer.ico");
        if (iconStream != null)
        {
            Icon = new Icon(iconStream);
            iconStream.Dispose();
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

        // Exit: command bar button or Ctrl+Q
        _leftPanel.ExitRequested  += (_, _) => ExitApplication();
        _rightPanel.ExitRequested += (_, _) => ExitApplication();
        Application.AddMessageFilter(this);

        // Status bar — subscribes to AppLog and shows Warn/Error messages
        _statusIcon = new ToolStripStatusLabel("✓")
        {
            AutoSize  = false,
            Width     = 20,
            ForeColor = SystemColors.GrayText,
        };
        _statusMessage = new ToolStripStatusLabel("Ready")
        {
            Spring      = true,
            TextAlign   = ContentAlignment.MiddleLeft,
            ForeColor   = SystemColors.GrayText,
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

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(_miMinimizeToTray);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(aboutItem);
        trayMenu.Items.Add(viewLogItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon             = this.Icon,
            Text             = "MultiExplorer",
            ContextMenuStrip = trayMenu,
            Visible          = false,
        };
        _trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };

        RestoreWindowState();
    }

    private void OnLoad(object? sender, EventArgs e) => Shown += OnShown;

    private void OnShown(object? sender, EventArgs e)
    {
        Shown -= OnShown;

        _leftCollapsed     = _settings.LeftPanelCollapsed;
        _rightCollapsed    = _settings.RightPanelCollapsed;
        _splitterLeft      = _settings.SplitterDistance > 0
                             ? _settings.SplitterDistance
                             : _layoutPanel.Width / 2;
        _savedSplitterLeft = _splitterLeft;

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

        _leftPanel.Launch(leftPaths);
        _rightPanel.Launch(rightPaths);

        _leftCollapsed  = savedLc;
        _rightCollapsed = savedRc;
        if (_leftCollapsed || _rightCollapsed)
            ApplyLayout();

        _pathPollTimer.Start();
    }

    private void OnPathPollTick(object? sender, EventArgs e)
    {
        _leftPanel.PollPath();
        _rightPanel.PollPath();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
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

        SaveSettings();
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
        _settings.LeftPanelCollapsed  = _leftCollapsed;
        _settings.RightPanelCollapsed = _rightCollapsed;

        _settings.LeftPanelTabs  = _leftPanel.GetAllPaths();
        _settings.RightPanelTabs = _rightPanel.GetAllPaths();
        _settings.LeftPanelPath  = _leftPanel.CurrentPath();
        _settings.RightPanelPath = _rightPanel.CurrentPath();

        SettingsManager.Save(_settings);
    }

    private void ShowMainWindow()
    {
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
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
        AppLog.Debug("GlobalHotkey",
            $"{HotkeyDialog.Describe(mods, vk)} could not be registered; use the tray icon to restore the window");
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
        const int WM_KEYDOWN = 0x0100;
        if (m.Msg == WM_KEYDOWN
            && m.WParam == (IntPtr)0x51 // VK_Q
            && (Control.ModifierKeys & Keys.Control) != 0
            && (Control.ModifierKeys & (Keys.Alt | Keys.Shift)) == 0)
        {
            ExitApplication();
            return true;
        }
        return false;
    }

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
            const int minPanel = 100;
            int maxLeft = Math.Max(minPanel, tw - bw - minPanel);
            _splitterLeft = Math.Clamp(_splitterLeft, minPanel, maxLeft);

            _leftPanel.Visible  = true;
            _leftPanel.Bounds   = new Rectangle(0, 0, _splitterLeft, th);
            _splitterBar.Bounds = new Rectangle(_splitterLeft, 0, bw, th);
            _rightPanel.Visible = true;
            _rightPanel.Bounds  = new Rectangle(_splitterLeft + bw, 0,
                                                 Math.Max(0, tw - _splitterLeft - bw), th);
        }

        _splitterBar.Invalidate();
    }

    private void ToggleCollapseLeft()
    {
        if (_leftCollapsed)
        {
            _leftCollapsed = false;
            _splitterLeft  = _savedSplitterLeft > 0 ? _savedSplitterLeft : _layoutPanel.Width / 2;
        }
        else if (!_rightCollapsed)
        {
            _savedSplitterLeft = _splitterLeft;
            _leftCollapsed     = true;
        }
        ApplyLayout();
    }

    private void ToggleCollapseRight()
    {
        if (_rightCollapsed)
        {
            _rightCollapsed = false;
            _splitterLeft   = _savedSplitterLeft > 0 ? _savedSplitterLeft : _layoutPanel.Width / 2;
        }
        else if (!_leftCollapsed)
        {
            _savedSplitterLeft = _splitterLeft;
            _rightCollapsed    = true;
        }
        ApplyLayout();
    }

    private void OnSplitterDragMoved(object? sender, int newLeft)
    {
        if (_leftCollapsed || _rightCollapsed) return;
        _splitterLeft = newLeft;
        ApplyLayout();
    }

    private void OnLogMessage(LogEntry entry)
    {
        if (InvokeRequired) { Invoke(() => OnLogMessage(entry)); return; }

        _statusClearTimer.Stop();
        _statusIcon.Text      = entry.Severity == LogSeverity.Error ? "✕" : "⚠";
        _statusIcon.ForeColor = entry.Severity == LogSeverity.Error ? Color.Crimson : Color.DarkOrange;
        _statusMessage.Text      = entry.ShortMessage;
        _statusMessage.ForeColor = SystemColors.ControlText;
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

    private void ClearStatus()
    {
        _statusClearTimer.Stop();
        _statusIcon.Text         = "✓";
        _statusIcon.ForeColor    = SystemColors.GrayText;
        _statusMessage.Text      = "Ready";
        _statusMessage.ForeColor = SystemColors.GrayText;
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
