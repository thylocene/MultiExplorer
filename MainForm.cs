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
    // Keeps the native navigation tree and Details view usable at the splitter
    // limit. Windows 11 renders this view through DirectUI, whose columns have
    // no supported live-width API; reserving this width preserves the Name
    // column and the most important metadata instead of clipping the whole view.
    private const int MinimumPanelWidth = 600;
    private const int MinimumWindowWidth = MinimumPanelWidth * 2 + SplitterBar.BarWidth;
    internal const int ActiveRefreshIntervalMilliseconds = 300;
    internal const int IdleRefreshIntervalMilliseconds = 2000;
    internal const int ActiveRefreshWindowMilliseconds = 1500;
    internal const int InitialPresentationTimeoutMilliseconds = 30000;
    internal const int WarningStatusDurationMilliseconds = 30000;
    private const string ApplicationIconResourceName =
        "MultiExplorer.MultiExplorer-Installer.ico";

    private readonly Panel       _layoutPanel;
    private readonly SplitterBar _splitterBar;
    private readonly PanelView   _leftPanel;
    private readonly PanelView   _rightPanel;
    private PanelView? _focusedPanel;

    private int  _splitterLeft;
    private double _splitterRatio = 0.5;
    private bool _splitterInitialized;
    private bool _leftCollapsed;
    private bool _rightCollapsed;
    private readonly System.Windows.Forms.Timer _pathPollTimer;
    private long _fastRefreshUntilTick;
    private readonly AppSettings    _settings;
    private readonly bool           _startInSystemTray;
    private readonly string?        _startupFolder;
    private List<string> _initialLeftPaths = [];
    private List<string> _initialRightPaths = [];
    private bool _explorerPanelsLaunched;
    private bool _initialPresentationPending;
    private bool _hasPendingInitialCollapseState;
    private bool _pendingInitialLeftCollapsed;
    private bool _pendingInitialRightCollapsed;
    private StartupLoadingForm? _startupLoadingForm;
    private System.Windows.Forms.Timer? _startupLoadingTimeout;

    internal event EventHandler? InitialPresentationCompleted;

    private readonly StatusStrip                  _statusBar;
    private readonly ToolStripStatusLabel         _statusIcon;
    private readonly ToolStripStatusLabel         _statusMessage;
    private readonly ToolStripStatusLabel         _statusDismiss;
    private readonly System.Windows.Forms.Timer   _statusClearTimer;
    private LogEntry? _currentStatusEntry;

    private readonly NotifyIcon          _trayIcon;
    private readonly ToolStripMenuItem   _miMinimizeToTray;
    private readonly ContextMenuStrip    _trayMenu;
    private bool _isInSystemTray;
    private bool _forceClose;
    private bool _skipOperationPrompt;
    private bool _exitWhenOperationsComplete;
    private int  _lastActiveOperationCount;
    private readonly OperationManager _operations;

    // Arbitrary unique ID for the global window-toggle hotkey.
    private const int HotkeyToggleWindow = 0x3001;

    // Modifiers and VK that are actually registered (0 = nothing registered)
    private int _registeredModifiers;
    private int _registeredVk;

    public MainForm() : this(SettingsManager.Load(), startInSystemTray: false,
        startupFolder: null) { }

    internal MainForm(AppSettings settings) : this(settings,
        startInSystemTray: false, startupFolder: null) { }

    internal MainForm(AppSettings settings, bool startInSystemTray)
        : this(settings, startInSystemTray, startupFolder: null) { }

    internal MainForm(AppSettings settings, bool startInSystemTray,
        string? startupFolder)
    {
        _settings = settings;
        _startInSystemTray = startInSystemTray;
        _startupFolder = startupFolder is not null
            && (ExplorerHost.IsNetworkPath(startupFolder)
                || Directory.Exists(startupFolder))
            ? Path.GetFullPath(startupFolder)
            : null;
        _operations = new OperationManager(
            GetOperationWindowPlacement,
            ConfirmDeletion);

        // The form needs a real, laid-out HWND for ExplorerBrowser to create its
        // native children, but it should not expose that partially populated UI.
        // Keep it transparent until both initial panes report that their visible
        // navigation and file content is ready.
        Opacity = 0;
        if (_startInSystemTray)
        {
            // Sign-in launches remain completely absent from the taskbar and do
            // not create Explorer panes until the user opens MultiExplorer.
            ShowInTaskbar = false;
        }

        Text = "MultiExplorer";
#if DEBUG
        Text += " [Debug]";
#endif
        using var iconStream = GetType().Assembly.GetManifestResourceStream(ApplicationIconResourceName);
        if (iconStream != null)
        {
            // Icon(Stream) can retain the stream, so clone it before the embedded
            // resource stream is disposed. The form icon is also used by the taskbar.
            using var embeddedIcon = new Icon(iconStream);
            Icon = (Icon)embeddedIcon.Clone();
        }
        MinimumSize = SizeFromClientSize(new Size(MinimumWindowWidth, 600));

        _layoutPanel = new Panel { Dock = DockStyle.Fill };
        _leftPanel   = new PanelView();
        _rightPanel  = new PanelView();
        _leftPanel.SetOpenExplorerWhenTabDroppedOutside(
            _settings.OpenExplorerWhenTabDroppedOutside);
        _rightPanel.SetOpenExplorerWhenTabDroppedOutside(
            _settings.OpenExplorerWhenTabDroppedOutside);
        _leftPanel.SetSortFoldersWithFilesByName(
            _settings.SortFoldersWithFilesByName);
        _rightPanel.SetSortFoldersWithFilesByName(
            _settings.SortFoldersWithFilesByName);
        _leftPanel.OpenInFileExplorerRequested += OnOpenInFileExplorerRequested;
        _rightPanel.OpenInFileExplorerRequested += OnOpenInFileExplorerRequested;
        _leftPanel.SetPreviewPaneWidth(_settings.LeftPreviewPaneWidth);
        _rightPanel.SetPreviewPaneWidth(_settings.RightPreviewPaneWidth);
        _leftPanel.PreviewPaneWidthChanged += (_, width) =>
            SavePreviewPaneWidth(isLeftPane: true, width);
        _rightPanel.PreviewPaneWidthChanged += (_, width) =>
            SavePreviewPaneWidth(isLeftPane: false, width);
        _leftPanel.SetFocusBorderEdge(PaneDividerEdge.Right);
        _rightPanel.SetFocusBorderEdge(PaneDividerEdge.Left);
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
        _leftPanel.ComparePanesRequested += (_, _) => ShowFolderCompare();
        _rightPanel.ComparePanesRequested += (_, _) => ShowFolderCompare();
        _leftPanel.CreateShortcutRequested += (_, path) =>
            ShowCreateShortcut(_leftPanel, _rightPanel, path);
        _rightPanel.CreateShortcutRequested += (_, path) =>
            ShowCreateShortcut(_rightPanel, _leftPanel, path);
        _leftPanel.TransferToOtherPaneRequested += (_, request) =>
            TransferToOtherPane(_rightPanel, request);
        _rightPanel.TransferToOtherPaneRequested += (_, request) =>
            TransferToOtherPane(_leftPanel, request);

        // QuickLook toggle: persist the setting immediately and keep both panels in sync
        _leftPanel.QuickLookToggled  += OnQuickLookToggled;
        _rightPanel.QuickLookToggled += OnQuickLookToggled;

        // Hotkey configuration
        _leftPanel.SetHotkeyRequested  += (_, _) => OnSetHotkeyRequested();
        _rightPanel.SetHotkeyRequested += (_, _) => OnSetHotkeyRequested();
        _leftPanel.SettingsRequested += OnSettingsRequested;
        _rightPanel.SettingsRequested += OnSettingsRequested;

        _leftPanel.StartWithWindowsToggled  += OnStartWithWindowsToggled;
        _rightPanel.StartWithWindowsToggled += OnStartWithWindowsToggled;
        _leftPanel.MinimizeToTrayToggled  += OnMinimizeToTrayToggled;
        _rightPanel.MinimizeToTrayToggled += OnMinimizeToTrayToggled;

        _leftPanel.FocusReceived  += (_, _) => SetFocusedPanel(_leftPanel);
        _rightPanel.FocusReceived += (_, _) => SetFocusedPanel(_rightPanel);

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

        _statusClearTimer = new System.Windows.Forms.Timer
        {
            Interval = WarningStatusDurationMilliseconds,
        };
        _statusClearTimer.Tick += (_, _) => ClearStatus();
        AppLog.MessageLogged += OnLogMessage;
        if (AppLog.TakeLatestUserMessage() is { } startupMessage)
            OnLogMessage(startupMessage);

        _pathPollTimer = new System.Windows.Forms.Timer
        {
            Interval = IdleRefreshIntervalMilliseconds,
        };
        _pathPollTimer.Tick += OnPathPollTick;
        _leftPanel.RefreshActivityObserved += OnRefreshActivityObserved;
        _rightPanel.RefreshActivityObserved += OnRefreshActivityObserved;

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
            UpdateMinimizeToTrayUi();
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
        UpdateMinimizeToTrayUi();

        RestoreWindowState();
        ApplyApplicationTheme();

        // Keep sign-in registration independent of whether Windows ever presents
        // the initial form. Startup launches begin fully transparent and hidden,
        // and some launchers can suppress the first Shown event altogether.
        _ = SynchronizeStartupRegistrationAsync();
    }

    private OperationWindowPlacement GetOperationWindowPlacement()
    {
        Rectangle anchor = WindowState == FormWindowState.Minimized
            ? RestoreBounds
            : Bounds;
        Screen screen = Screen.FromRectangle(anchor);
        Rectangle workArea = screen.WorkingArea;
        return new OperationWindowPlacement
        {
            AnchorLeft = anchor.Left,
            AnchorTop = anchor.Top,
            AnchorWidth = anchor.Width,
            AnchorHeight = anchor.Height,
            WorkAreaLeft = workArea.Left,
            WorkAreaTop = workArea.Top,
            WorkAreaWidth = workArea.Width,
            WorkAreaHeight = workArea.Height,
        };
    }

    private bool ConfirmDeletion(
        FileOperationKind operation,
        IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!_settings.ConfirmFileAndFolderDeletions)
            return true;

        using var dialog = new DeleteConfirmationDialog(operation, paths);
        return dialog.ShowDialog(this) == DialogResult.OK;
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
        if (_currentStatusEntry is { } statusEntry)
            ShowStatus(statusEntry);
        else
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

        if (_settings.WindowWidth == 0)
            _settings.QuickLookEnabled = IsQuickLookAvailable();

        ExplorerHost.QuickLookEnabled = _settings.QuickLookEnabled;

        _initialLeftPaths = _settings.LeftPanelTabs.Count > 0
            ? [.. _settings.LeftPanelTabs]
            : [_settings.LeftPanelPath];
        _initialRightPaths = _settings.RightPanelTabs.Count > 0
            ? [.. _settings.RightPanelTabs]
            : [_settings.RightPanelPath];
        if (_startupFolder is not null)
            _initialLeftPaths[0] = _startupFolder;

        _leftPanel.SetPathHistory(_settings.LeftPathHistory);
        _rightPanel.SetPathHistory(_settings.RightPathHistory);

        if (_startInSystemTray)
        {
            // A Windows sign-in launch only needs the message window, hotkey, and
            // notification icon. Creating IExplorerBrowser instances here competes
            // with every other sign-in application and delays the tray icon. The
            // saved panes are launched on demand when the user first opens the app.
            BeginInvoke((Action)(() =>
            {
                StartInSystemTray();
            }));
            return;
        }

        BeginInitialPresentation();
    }

    private void BeginInitialPresentation()
    {
        if (_initialPresentationPending || IsDisposed || Disposing)
            return;
        if (_explorerPanelsLaunched) return;

        _initialPresentationPending = true;
        Opacity = 0;
        _startupLoadingForm = new StartupLoadingForm(Icon);
        _startupLoadingForm.ShowCentered(this);

        _startupLoadingTimeout = new System.Windows.Forms.Timer
        {
            Interval = InitialPresentationTimeoutMilliseconds,
        };
        _startupLoadingTimeout.Tick += OnStartupLoadingTimeout;
        _startupLoadingTimeout.Start();

        LaunchExplorerPanels();
    }

    private void OnStartupLoadingTimeout(object? sender, EventArgs e)
    {
        AppLog.Warn(null, nameof(MainForm),
            "Initial Explorer content did not finish loading within 30 seconds; "
            + "showing the main window while the remaining content continues.");
        CompleteInitialPresentation();
    }

    private void CompleteInitialPresentation()
    {
        if (!_initialPresentationPending) return;

        _initialPresentationPending = false;
        RestorePendingInitialCollapseState();
        StopStartupLoadingTimeout();

        ShowInTaskbar = true;
        Opacity = 1;
        Show();
        Refresh();

        StartupLoadingForm? loadingForm = _startupLoadingForm;
        _startupLoadingForm = null;
        if (loadingForm is not null)
        {
            loadingForm.Close();
            loadingForm.Dispose();
        }

        Activate();
        BringToFront();
        InitialPresentationCompleted?.Invoke(this, EventArgs.Empty);
        _ = WarmContextMenuCachesAsync();
    }

    private void StopStartupLoadingTimeout()
    {
        if (_startupLoadingTimeout is null) return;

        _startupLoadingTimeout.Stop();
        _startupLoadingTimeout.Tick -= OnStartupLoadingTimeout;
        _startupLoadingTimeout.Dispose();
        _startupLoadingTimeout = null;
    }

    private void CancelInitialPresentation()
    {
        _initialPresentationPending = false;
        RestorePendingInitialCollapseState();
        StopStartupLoadingTimeout();

        StartupLoadingForm? loadingForm = _startupLoadingForm;
        _startupLoadingForm = null;
        if (loadingForm is null) return;

        loadingForm.Close();
        loadingForm.Dispose();
    }

    private void RestorePendingInitialCollapseState()
    {
        if (!_hasPendingInitialCollapseState) return;

        _hasPendingInitialCollapseState = false;
        _leftCollapsed = _pendingInitialLeftCollapsed;
        _rightCollapsed = _pendingInitialRightCollapsed;
        if (_leftCollapsed || _rightCollapsed)
            ApplyLayout();
    }

    private void LaunchExplorerPanels()
    {
        if (_explorerPanelsLaunched || IsDisposed || Disposing)
            return;

        _explorerPanelsLaunched = true;

        // Browsers require non-zero bounds to initialise, so launch with both panels
        // visible at full size, then apply the saved collapse state afterwards.
        bool savedLc = _leftCollapsed;
        bool savedRc = _rightCollapsed;
        _pendingInitialLeftCollapsed = savedLc;
        _pendingInitialRightCollapsed = savedRc;
        _hasPendingInitialCollapseState = true;
        _leftCollapsed = false;
        _rightCollapsed = false;
        ApplyLayout();

        // ExplorerBrowser's two navigation trees share Shell image-list state.
        // Initializing both on separate STA threads at exactly the same time can
        // leave either tree with blank icon slots until that browser is recreated.
        // Start the second pane once the first native navigation is ready, while
        // allowing its managed Details list to finish in parallel.
        bool leftContentReady = false;
        bool rightContentReady = false;
        bool completionStarted = false;

        void TryCompleteInitialPresentation()
        {
            if (!leftContentReady || !rightContentReady || completionStarted
                || !_initialPresentationPending || IsDisposed || Disposing)
                return;

            completionStarted = true;
            _ = CompleteAndRestoreLeftNavigationAsync();
        }

        async Task CompleteAndRestoreLeftNavigationAsync()
        {
            // The file views are ready. Show them before repairing any tree
            // expansion reset by the second browser's initialization.
            CompleteInitialPresentation();
            if (!IsDisposed && !Disposing)
                await _leftPanel.RestoreActiveNavigationPaneAsync();
        }

        EventHandler? leftContentCompleted = null;
        leftContentCompleted = (_, _) =>
        {
            _leftPanel.InitialBrowserReady -= leftContentCompleted;
            leftContentReady = true;
            TryCompleteInitialPresentation();
        };
        _leftPanel.InitialBrowserReady += leftContentCompleted;

        EventHandler? launchRightPanel = null;
        launchRightPanel = (_, _) =>
        {
            _leftPanel.InitialNavigationReady -= launchRightPanel;
            if (!IsDisposed && !Disposing)
            {
                _startupLoadingForm?.SetMessage(
                    "Preparing files, folders, and navigation in the second pane…");
                // ExplorerBrowser navigation trees share process-wide Shell
                // state. Initialising the second tree can reset the expansion
                // applied to the first, so restore the left tree once the right
                // browser has completed its own initial navigation.
                EventHandler? restoreLeftNavigation = null;
                restoreLeftNavigation = (_, _) =>
                {
                    _rightPanel.InitialBrowserReady -= restoreLeftNavigation;
                    rightContentReady = true;
                    TryCompleteInitialPresentation();
                };
                _rightPanel.InitialBrowserReady += restoreLeftNavigation;
                _rightPanel.Launch(_initialRightPaths);
            }
        };
        _leftPanel.InitialNavigationReady += launchRightPanel;
        _leftPanel.Launch(_initialLeftPaths);

        BeginFastRefreshWindow();
        _pathPollTimer.Start();
        SetFocusedPanel(_leftPanel);
    }

    private static async Task WarmContextMenuCachesAsync()
    {
        try
        {
            await Task.WhenAll(
                ShellNewMenu.WarmCacheAsync(),
                QuickAccessService.WarmCacheAsync());
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(WarmContextMenuCachesAsync),
                "Could not pre-load context-menu data.");
        }
    }

    private void StartInSystemTray()
    {
        if (IsDisposed || Disposing) return;

        _isInSystemTray = true;
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

    /// <summary>Hides the main window while keeping the app available from the notification area.</summary>
    private void HideMainWindowToTray()
    {
        _isInSystemTray = true;
        SaveSettings();
        _pathPollTimer.Stop();
        _trayIcon.Visible = true;
        Hide();
    }

    private void OnPathPollTick(object? sender, EventArgs e)
    {
        _leftPanel.PollPath();
        _rightPanel.PollPath();
        SetPathPollInterval(GetPathPollInterval(
            Environment.TickCount64, _fastRefreshUntilTick));
    }

    private void OnRefreshActivityObserved(object? sender, EventArgs e) =>
        BeginFastRefreshWindow();

    private void BeginFastRefreshWindow()
    {
        _fastRefreshUntilTick = Environment.TickCount64
            + ActiveRefreshWindowMilliseconds;
        SetPathPollInterval(ActiveRefreshIntervalMilliseconds);
    }

    private void SetPathPollInterval(int interval)
    {
        if (_pathPollTimer.Interval != interval)
            _pathPollTimer.Interval = interval;
    }

    internal static int GetPathPollInterval(long now, long fastRefreshUntil) =>
        now < fastRefreshUntil
            ? ActiveRefreshIntervalMilliseconds
            : IdleRefreshIntervalMilliseconds;

    private void SetFocusedPanel(PanelView panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (ReferenceEquals(_focusedPanel, panel)) return;

        _focusedPanel?.SetFocusIndicator(false);
        _focusedPanel = panel;
        _focusedPanel.SetFocusIndicator(true);
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
                    HideMainWindowToTray();
                    return;
                case OperationExitChoice.CancelAndExit:
                    e.Cancel = true;
                    _forceClose = false;
                    _exitWhenOperationsComplete = true;
                    _operations.CancelAll();
                    HideMainWindowToTray();
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
            HideMainWindowToTray();
            return;
        }

        CancelInitialPresentation();
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
            CancelInitialPresentation();
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

        // A sign-in launch deliberately leaves the Explorer panes dormant. Keep
        // their persisted paths intact if the session ends before the user opens
        // the window, rather than replacing them with empty/default values.
        if (_explorerPanelsLaunched)
        {
            _settings.LeftPanelTabs  = _leftPanel.GetAllPaths();
            _settings.RightPanelTabs = _rightPanel.GetAllPaths();
            _settings.LeftPanelPath  = _leftPanel.CurrentPath();
            _settings.RightPanelPath = _rightPanel.CurrentPath();
        }
        _settings.LeftPreviewPaneWidth = _leftPanel.PreviewPaneWidth;
        _settings.RightPreviewPaneWidth = _rightPanel.PreviewPaneWidth;
        _settings.LeftPathHistory  = _leftPanel.GetPathHistory();
        _settings.RightPathHistory = _rightPanel.GetPathHistory();

        SettingsManager.Save(_settings);
    }

    private void ShowMainWindow()
    {
        _isInSystemTray = false;
        ShowInTaskbar = true;
        Opacity = _initialPresentationPending || !_explorerPanelsLaunched ? 0 : 1;
        Show();
        WindowState = _settings.WindowState == "Maximized"
                          ? FormWindowState.Maximized
                          : FormWindowState.Normal;
        _trayIcon.Visible = false;

        if (_initialPresentationPending)
        {
            _startupLoadingForm?.Activate();
            return;
        }

        if (!_explorerPanelsLaunched)
        {
            BeginInitialPresentation();
            return;
        }

        Activate();
        BringToFront();
        BeginFastRefreshWindow();
        _pathPollTimer.Start();
    }

    private void ToggleMainWindowFromGlobalHotkey()
    {
        if (ShouldShowMainWindowForGlobalHotkey(_isInSystemTray))
            ShowMainWindow();
        else
            HideMainWindowToTray();
    }

    private void ExitApplication()
    {
        _forceClose = true;
        Close();
    }

    private void ShowFolderCompare()
    {
        using var dialog = new FolderCompareDialog(
            _leftPanel.CurrentPath(), _rightPanel.CurrentPath(), _operations.Start);
        dialog.ShowDialog(this);
    }

    private void ShowCreateShortcut(PanelView sourcePane, PanelView otherPane,
        string targetPath)
    {
        string currentFolder = sourcePane.CurrentPath();
        if (!Directory.Exists(currentFolder))
            currentFolder = Path.GetDirectoryName(targetPath) ?? currentFolder;

        using var dialog = new CreateShortcutDialog(targetPath, currentFolder,
            otherPane.CurrentPath(), new ShortcutCreationService());
        if (dialog.ShowDialog(this) != DialogResult.OK
            || dialog.CreatedPath is not { } createdPath)
            return;

        string? destination = Path.GetDirectoryName(createdPath);
        if (string.Equals(destination, sourcePane.CurrentPath(),
                StringComparison.OrdinalIgnoreCase))
            sourcePane.RefreshCurrentFolder();
        if (string.Equals(destination, otherPane.CurrentPath(),
                StringComparison.OrdinalIgnoreCase))
            otherPane.RefreshCurrentFolder();
    }

    private void TransferToOtherPane(PanelView destinationPane,
        OppositePaneTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(destinationPane);
        ArgumentNullException.ThrowIfNull(request);
        string? destination = destinationPane.AvailableFileOperationDestination;
        if (destination is null)
        {
            MessageBox.Show(this,
                "The opposite pane does not have an available folder.",
                "Cannot transfer items", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _operations.Start(request.Kind, request.Paths, destination);
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

        if (NativeMethods.RegisterHotKey(Handle, HotkeyToggleWindow,
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
            && NativeMethods.RegisterHotKey(Handle, HotkeyToggleWindow,
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

        NativeMethods.UnregisterHotKey(Handle, HotkeyToggleWindow);

        if (NativeMethods.RegisterHotKey(Handle, HotkeyToggleWindow,
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
                NativeMethods.RegisterHotKey(Handle, HotkeyToggleWindow,
                    curMods | NativeMethods.MOD_NOREPEAT, curVk);

            MessageBox.Show(this,
                $"{HotkeyDialog.Describe(newMods, newVk)} is already in use by another application.",
                "Could not register hotkey",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(Handle, HotkeyToggleWindow);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)Program.WM_SHOW_INSTANCE)
        {
            ShowMainWindow();
            string? requestedFolder = ActivationRequestStore.TryTakeFolder();
            if (requestedFolder is not null)
                (_focusedPanel ?? _leftPanel).NavigateTo(requestedFolder);
            return;
        }
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyToggleWindow)
        {
            ToggleMainWindowFromGlobalHotkey();
            return;
        }
        base.WndProc(ref m);
    }

    public bool PreFilterMessage(ref Message m)
    {
        // Do not treat these application-level shortcuts while the main window is
        // hidden in the notification area. ExplorerHost separately forwards the
        // shortcuts from native browser STAs while the window is visible.
        CommandBar.Cmd? shortcut = GetApplicationShortcut(
            m.Msg, m.WParam, Control.ModifierKeys);
        if (Visible && shortcut.HasValue)
        {
            (_focusedPanel ?? _leftPanel).ExecuteCommand(shortcut.Value);
            return true;
        }
        return false;
    }

    private async void OnSettingsRequested(object? sender, EventArgs e)
    {
        try
        {
            await ShowSettingsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OnSettingsRequested),
                "Could not apply the selected settings.");
            MessageBox.Show(
                this,
                "MultiExplorer could not apply all of the selected settings. "
                    + "See the application log for details.",
                "Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task ShowSettingsAsync()
    {
        var state = new SettingsDialogState(
            ThemeManager.Current,
            _settings.ShowWindowModifiers,
            _settings.ShowWindowVk,
            _settings.StartWithWindows,
            _settings.MinimizeToTray,
            _settings.QuickLookEnabled,
            _settings.ConfirmFileAndFolderDeletions,
            _settings.OpenExplorerWhenTabDroppedOutside,
            _settings.SortFoldersWithFilesByName);
        using var dialog = new SettingsForm(state, ExplorerOptions.Open);

        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (!TryUpdateHotkey(
                    dialog.SelectedHotkeyModifiers,
                    dialog.SelectedHotkeyVirtualKey))
            {
                dialog.DialogResult = DialogResult.None;
                continue;
            }

            bool startWithWindows = _settings.StartWithWindows;
            if (dialog.StartWithWindows != _settings.StartWithWindows)
            {
                (bool success, Exception? error) =
                    await StartupManager.TrySetEnabledAsync(
                        dialog.StartWithWindows);
                if (success)
                {
                    startWithWindows = dialog.StartWithWindows;
                }
                else
                {
                    AppLog.Warn(error!, nameof(ShowSettingsAsync),
                        "Could not update start-with-Windows registration.");
                    MessageBox.Show(
                        this,
                        "The Start with Windows setting could not be changed. "
                            + "The other settings were saved.",
                        "Settings",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            _settings.StartWithWindows = startWithWindows;
            _settings.MinimizeToTray = dialog.MinimizeToTray;
            _settings.QuickLookEnabled = dialog.QuickLookEnabled;
            _settings.ConfirmFileAndFolderDeletions =
                dialog.ConfirmFileAndFolderDeletions;
            _settings.OpenExplorerWhenTabDroppedOutside =
                dialog.OpenExplorerWhenTabDroppedOutside;
            _settings.SortFoldersWithFilesByName =
                dialog.SortFoldersWithFilesByName;
            _leftPanel.SetOpenExplorerWhenTabDroppedOutside(
                _settings.OpenExplorerWhenTabDroppedOutside);
            _rightPanel.SetOpenExplorerWhenTabDroppedOutside(
                _settings.OpenExplorerWhenTabDroppedOutside);
            _leftPanel.SetSortFoldersWithFilesByName(
                _settings.SortFoldersWithFilesByName);
            _rightPanel.SetSortFoldersWithFilesByName(
                _settings.SortFoldersWithFilesByName);
            ExplorerHost.QuickLookEnabled = dialog.QuickLookEnabled;
            _miMinimizeToTray.Checked = dialog.MinimizeToTray;
            UpdateStartWithWindowsUi();
            UpdateMinimizeToTrayUi();
            SettingsManager.Save(_settings);

            if (dialog.SelectedTheme != ThemeManager.Current)
                ChangeTheme(dialog.SelectedTheme);
            break;
        }
    }

    private async void OnOpenInFileExplorerRequested(
        object? sender, OpenInFileExplorerRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Path)) return;

        ExplorerLaunchResult result;
        try
        {
            result = await ExplorerWindowLauncher.OpenAtAsync(
                e.Path, e.DropPosition, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OnOpenInFileExplorerRequested),
                $"Could not open Windows File Explorer at \"{e.Path}\".");
            return;
        }

        if (result.WindowFound && sender is PanelView sourcePanel)
        {
            try
            {
                sourcePanel.CloseTabAfterExplorerOpened(e.SourceTab);
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, nameof(OnOpenInFileExplorerRequested),
                    "Windows File Explorer opened, but the source tab could not be closed.");
            }
        }

        if (!result.WindowFound)
            AppLog.Warn(null, nameof(OnOpenInFileExplorerRequested),
                "Windows File Explorer was launched, but its new window could not be found.");
        else if (!result.Placed)
            AppLog.Warn(null, nameof(OnOpenInFileExplorerRequested),
                "Windows File Explorer opened, but its new window could not be placed and focused on the drop screen.");
    }

    private bool TryUpdateHotkey(int newModifiers, int newVirtualKey)
    {
        if (newModifiers == _settings.ShowWindowModifiers
            && newVirtualKey == _settings.ShowWindowVk)
            return true;

        int previousModifiers = _registeredModifiers;
        int previousVirtualKey = _registeredVk;
        NativeMethods.UnregisterHotKey(Handle, HotkeyToggleWindow);

        if (NativeMethods.RegisterHotKey(
                Handle,
                HotkeyToggleWindow,
                newModifiers | NativeMethods.MOD_NOREPEAT,
                newVirtualKey))
        {
            _registeredModifiers = newModifiers;
            _registeredVk = newVirtualKey;
            _settings.ShowWindowModifiers = newModifiers;
            _settings.ShowWindowVk = newVirtualKey;
            return true;
        }

        if (previousModifiers != 0)
        {
            NativeMethods.RegisterHotKey(
                Handle,
                HotkeyToggleWindow,
                previousModifiers | NativeMethods.MOD_NOREPEAT,
                previousVirtualKey);
        }

        MessageBox.Show(
            this,
            $"{HotkeyDialog.Describe(newModifiers, newVirtualKey)} is already "
                + "in use by another application.",
            "Could not register hotkey",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }

    internal static CommandBar.Cmd? GetApplicationShortcut(
        int message, IntPtr virtualKey, Keys modifiers)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            return null;

        if (modifiers == Keys.Alt)
            return virtualKey.ToInt32() switch
            {
                (int)Keys.C => CommandBar.Cmd.CopyToOtherPane,
                (int)Keys.M => CommandBar.Cmd.MoveToOtherPane,
                (int)Keys.Return => CommandBar.Cmd.Properties,
                _ => null,
            };

        if (message != WM_KEYDOWN) return null;

        if (virtualKey.ToInt32() == (int)Keys.F1 && modifiers == Keys.None)
            return CommandBar.Cmd.Help;

        if (virtualKey.ToInt32() == (int)Keys.F5 && modifiers == Keys.None)
            return CommandBar.Cmd.Refresh;

        if (virtualKey.ToInt32() == (int)Keys.F4 && modifiers == Keys.None)
            return CommandBar.Cmd.EditAddressBar;

        if (virtualKey.ToInt32() == (int)Keys.N
            && modifiers == (Keys.Control | Keys.Shift))
            return CommandBar.Cmd.NewFolder;

        if (virtualKey.ToInt32() == (int)Keys.C
            && modifiers == (Keys.Control | Keys.Shift))
            return CommandBar.Cmd.CopyPaths;

        if (modifiers != Keys.Control) return null;

        return virtualKey.ToInt32() switch
        {
            0x4E => CommandBar.Cmd.NewTab,        // N
            0x54 => CommandBar.Cmd.NewTab,        // T
            0x4F => CommandBar.Cmd.FolderOptions, // O
            0x4C => CommandBar.Cmd.ViewLog,       // L
            0x51 => CommandBar.Cmd.Exit,          // Q
            _    => null,
        };
    }

    internal static bool ShouldShowMainWindowForGlobalHotkey(bool isInSystemTray) =>
        isInSystemTray;

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
        int usableWidth = Math.Max(0, layoutWidth - SplitterBar.BarWidth);
        int minimumPanelWidth = Math.Min(MinimumPanelWidth, usableWidth / 2);
        return Math.Clamp(splitterLeft, minimumPanelWidth, usableWidth - minimumPanelWidth);
    }

    internal static bool CanLayoutExpandedPanels(int layoutWidth) =>
        layoutWidth >= MinimumWindowWidth;

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

        if (!ShouldReplaceStatus(_currentStatusEntry?.Severity, entry.Severity))
            return;

        _currentStatusEntry = entry;
        _statusClearTimer.Stop();
        ShowStatus(entry);

        if (entry.Severity == LogSeverity.Warn)
            _statusClearTimer.Start();
    }

    internal static bool ShouldReplaceStatus(
        LogSeverity? currentSeverity, LogSeverity incomingSeverity) =>
        currentSeverity != LogSeverity.Error || incomingSeverity == LogSeverity.Error;

    private void ShowStatus(LogEntry entry)
    {
        _statusIcon.Text      = entry.Severity == LogSeverity.Error ? "✕" : "⚠";
        _statusIcon.ForeColor = entry.Severity == LogSeverity.Error ? ThemeManager.Error : ThemeManager.Warning;
        _statusMessage.Text      = entry.ShortMessage;
        _statusMessage.ForeColor = ThemeManager.Text;
        _statusMessage.IsLink    = true;
        _statusDismiss.Visible   = true;
    }

    private void OnQuickLookToggled(object? sender, EventArgs e)
    {
        _settings.QuickLookEnabled = ExplorerHost.QuickLookEnabled;
        SettingsManager.Save(_settings);
    }

    private void SavePreviewPaneWidth(bool isLeftPane, int width)
    {
        if (isLeftPane)
        {
            if (_settings.LeftPreviewPaneWidth == width) return;
            _settings.LeftPreviewPaneWidth = width;
        }
        else
        {
            if (_settings.RightPreviewPaneWidth == width) return;
            _settings.RightPreviewPaneWidth = width;
        }

        SettingsManager.Save(_settings);
    }

    private async void OnStartWithWindowsToggled(object? sender, EventArgs e)
    {
        bool enabled = !_settings.StartWithWindows;
        (bool success, Exception? error) =
            await StartupManager.TrySetEnabledAsync(enabled);
        if (!success)
        {
            AppLog.Warn(error!, nameof(OnStartWithWindowsToggled),
                $"Could not {(enabled ? "enable" : "disable")} start with Windows.");
            return;
        }

        _settings.StartWithWindows = enabled;
        SettingsManager.Save(_settings);
        UpdateStartWithWindowsUi();
    }

    private void OnMinimizeToTrayToggled(object? sender, EventArgs e) =>
        _miMinimizeToTray.Checked = !_settings.MinimizeToTray;

    private async Task SynchronizeStartupRegistrationAsync()
    {
        (bool success, Exception? error) =
            await StartupManager.TrySetEnabledAsync(_settings.StartWithWindows);
        if (!success)
        {
            AppLog.Warn(error!, nameof(SynchronizeStartupRegistrationAsync),
                "Could not synchronize the start-with-Windows preference.");
        }
    }

    private void UpdateStartWithWindowsUi()
    {
        _leftPanel.SetStartWithWindowsChecked(_settings.StartWithWindows);
        _rightPanel.SetStartWithWindowsChecked(_settings.StartWithWindows);
    }

    private void UpdateMinimizeToTrayUi()
    {
        _leftPanel.SetMinimizeToTrayChecked(_settings.MinimizeToTray);
        _rightPanel.SetMinimizeToTrayChecked(_settings.MinimizeToTray);
    }

    private void ClearStatus()
    {
        _statusClearTimer.Stop();
        _currentStatusEntry = null;
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
