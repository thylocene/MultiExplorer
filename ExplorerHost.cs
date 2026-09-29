using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiExplorer;

internal enum QuickLookFailure
{
    NotInstalled,
    NotRunning,
    Unavailable,
}

internal enum NetworkPathStatus
{
    NotNetwork,
    Checking,
    Available,
    Unavailable,
}

internal sealed class NavigationContextMenuEventArgs(
    string path, Point screenLocation) : EventArgs
{
    internal string Path { get; } = path
        ?? throw new ArgumentNullException(nameof(path));
    internal Point ScreenLocation { get; } = screenLocation;
}

internal sealed class FileContextMenuEventArgs(
    IReadOnlyList<string> paths, Point screenLocation) : EventArgs
{
    internal IReadOnlyList<string> Paths { get; } = paths
        ?? throw new ArgumentNullException(nameof(paths));
    internal Point ScreenLocation { get; } = screenLocation;
}

/// <summary>
/// Hosts a full Windows Explorer UI (address bar, toolbar, file list) using the
/// IExplorerBrowser COM interface (CLSID_ExplorerBrowser).
///
/// The browser is created as an in-process child window inside this control's HWND.
/// There is no external process, no window detection, and no DPI context mismatch.
///
/// Path tracking
/// -------------
/// Instead, GetCurrentPath() queries the live folder on demand via the chain:
///   IExplorerBrowser.GetCurrentView(IFolderView) → IFolderView.GetFolder(IPersistFolder2)
///   → IPersistFolder2.GetCurFolder(pidl) → SHGetPathFromIDList
/// This is called once at window-close time, which is sufficient for persistence.
/// </summary>
public sealed class ExplorerHost : Control, IMessageFilter
{
    internal const bool DefaultCompactViewEnabled = true;
    private const int ItemCountFallbackRefreshIntervalMilliseconds = 60000;
    private const int ItemCountWatcherDebounceMilliseconds = 400;
    private static readonly TimeSpan NetworkNavigationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NavigationPaneNetworkNavigationTimeout =
        TimeSpan.FromSeconds(10);
    private const int NativeStatusBarHeight = 24;
    private const uint GW_CHILD = 5; // GetWindow flag: first child in Z-order
    private const string ExplorerViewStatePropertyBag = "MultiExplorer";

    private const int WM_KEYDOWN       = 0x0100;
    private const int WM_SYSKEYDOWN    = 0x0104;
    private const int WM_LBUTTONDOWN   = 0x0201;
    private const int WM_LBUTTONUP     = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN   = 0x0204;
    private const int WM_RBUTTONUP     = 0x0205;
    private const int WM_MBUTTONDOWN   = 0x0207;
    private const int WM_XBUTTONDOWN   = 0x020B;
    private const int WM_SETFOCUS      = 0x0007;

    private const uint FVM_DETAILS = 4;

    private const uint FWF_CHECKSELECT        = 0x00040000;
    private const uint FWF_NOCOLUMNHEADER     = 0x00800000;
    private const uint FWF_NOHEADERINALLVIEWS = 0x01000000;

    /// <summary>
    /// When true, pressing Space in the file list sends the selected item to QuickLook.
    /// Configured in the application Settings form and persisted in AppSettings.
    /// </summary>
    internal static bool QuickLookEnabled { get; set; }

    private NativeMethods.IExplorerBrowser? _browser;
    private BrowserThread?                  _browserThread;
    private BrowserSiteImpl?               _site;
    private ExplorerBrowserEventsImpl?     _events;
    private uint                           _eventsCookie;
    // Read by the WinForms UI thread and updated by the browser STA.  Keep this
    // as a snapshot: UI polling must never synchronously enter the browser STA,
    // because Shell drag/drop can keep that STA in a modal loop for the entire
    // copy/move operation.
    private volatile string                _currentPath;
    private string?                        _cachedSelectedItemPath;
    private string[]                       _cachedSelectedFileSystemPaths = [];
    private int                            _selectionRefreshPending;
    private int                            _pathRefreshPending;
    private long                           _nextPathRefreshTick;
    private int                            _initialNavigationReported;
    private int                            _currentViewMode = (int)FVM_DETAILS;
    private int                            _currentViewIconSize = 16;
    private int                            _quickLookAlertPending;
    private int                            _navigationClickGeneration;
    private int                            _navigationRestoreGeneration;
    private string?                        _pendingNavigationPath;
    private int                            _itemCountRequestGeneration;
    private long                           _nextItemCountRefreshTick;
    private readonly object                _itemCountRefreshLock = new();
    private readonly CancellationTokenSource _itemCountWorkerCancellation = new();
    private ItemCountRefreshRequest?       _pendingItemCountRefresh;
    private bool                           _itemCountWorkerRunning;
    private bool                           _itemCountMonitoringActive;
    private IDisposable?                   _itemCountWatchSubscription;
    private string                         _itemCountWatcherPath = string.Empty;
    private int                            _itemCountWatcherUiUpdatePending;
    private ShellFileDropTarget?           _fileDropTarget;
    private IntPtr                         _fileDropTargetWindow;
    private NavigationTreeNotificationHook? _navigationTreeContextMenuHook;
    private FileViewContextMenuHook?         _fileViewContextMenuHook;
    private ExplorerSeparatorHook?         _explorerSeparatorHook;
    private bool                           _browserWasLaunched;
    private bool                           _showNavPane = true;
    private volatile bool                  _renderedNavPaneVisible = true;
    private int                            _networkViewRebuildGeneration;
    private bool                           _pendingNetworkThemeRebuild;
    private readonly System.Windows.Forms.Timer _browserBoundsTimer;
    private readonly System.Windows.Forms.Timer _itemCountDebounceTimer;
    private readonly ExplorerTabViewSettings _viewSettings;
    private volatile NetworkPathStatus _networkPathStatus;
    private int _networkProbeGeneration;
    private int _networkBrowseGeneration;
    private int _consecutiveNetworkMonitorFailures;
    private long _nextNetworkCheckTick;

    // Used by the manual double-click detector in PreFilterMessage.
    // IExplorerBrowser child windows often lack CS_DBLCLKS, so Windows never posts
    // WM_LBUTTONDBLCLK for them — we detect the double-click ourselves from
    // successive WM_LBUTTONDOWN messages that fall within the system thresholds.
    private long  _lastBlankClickTick;
    private Point _lastBlankClickPt;
    private IntPtr _suppressedTypeHeaderMouseUpWindow;

    // Short-lived timers used to defer selection restore / inline-rename until the
    // shell view has finished loading asynchronously. Tracked so they can be
    // stopped and disposed if the host is torn down before they fire — otherwise a
    // rapid theme toggle or tab close leaks a timer per operation.
    private readonly List<System.Windows.Forms.Timer> _pendingTimers = new();

    // ── Name filter ───────────────────────────────────────────────────────────

    /// <summary>True while a name filter overlay is active over this host.</summary>
    internal bool IsFiltering { get; set; }

    /// <summary>Fired when a printable character is typed in the file list (not during rename).</summary>
    internal event EventHandler<char>? FilterCharInput;

    /// <summary>Fired when Backspace is pressed while the filter bar is active.</summary>
    internal event EventHandler? FilterBackspaceTyped;

    /// <summary>Fired when Escape is pressed while the filter bar is active.</summary>
    internal event EventHandler? FilterEscapePressed;

    /// <summary>Raised on the WinForms thread for an application-wide Ctrl shortcut.</summary>
    internal event EventHandler<CommandBar.Cmd>? ApplicationShortcutRequested;

    /// <summary>
    /// Raised on the WinForms thread when the user presses a mouse button in
    /// either native Explorer pane.  Managed sibling controls cannot otherwise
    /// observe mouse input sent directly to ExplorerBrowser child HWNDs.
    /// </summary>
    internal event EventHandler? ShellMouseDown;

    /// <summary>Raised on the WinForms thread when the native shell view receives keyboard focus.</summary>
    internal event EventHandler? ShellFocused;

    /// <summary>
    /// Raised on the WinForms thread when a file-system folder in the native
    /// navigation tree requests its context menu.
    /// </summary>
    internal event EventHandler<NavigationContextMenuEventArgs>?
        NavigationContextMenuRequested;

    /// <summary>Raised on the WinForms thread for a native file-list item menu.</summary>
    internal event EventHandler<FileContextMenuEventArgs>?
        FileContextMenuRequested;

    /// <summary>Raised on the WinForms thread when QuickLook cannot preview the selection.</summary>
    internal event EventHandler<QuickLookFailure>? QuickLookUnavailable;

    /// <summary>Raised on the WinForms thread when this tab's item totals change.</summary>
    internal event EventHandler<DirectoryItemCount>? ItemCountChanged;

    /// <summary>Raised after a folder's persisted view mode has been restored.</summary>
    internal event EventHandler<ExplorerFolderViewState>? FolderViewStateChanged;

    /// <summary>Raised on the WinForms thread after this host navigates to a folder.</summary>
    internal event EventHandler? NavigationChanged;
    internal event EventHandler? NetworkPathStatusChanged;
    internal event EventHandler? NavigationPaneVisibilityChanged;

    /// <summary>
    /// Raised on the WinForms thread after the cached native Shell selection changes.
    /// Consumers can read the non-blocking selection snapshot from this host.
    /// </summary>
    internal event EventHandler? SelectionChanged;

    /// <summary>
    /// Raised on the WinForms thread after this host's first navigation attempt.
    /// Shell navigation trees share process-wide image-list infrastructure, so the
    /// main form uses this signal to avoid initializing both panes concurrently.
    /// </summary>
    internal event EventHandler? InitialNavigationCompleted;

    // ── Construction ──────────────────────────────────────────────────────────

    public ExplorerHost(string initialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialPath);
        // Never ask a network provider to resolve a saved path on the UI thread.
        // Even an offline share remains the tab's persisted path.
        _currentPath = IsNetworkPath(initialPath) || Directory.Exists(initialPath)
            ? initialPath : @"C:\";
        _viewSettings = new ExplorerTabViewSettings(
            compactViewEnabled: DefaultCompactViewEnabled,
            itemCheckBoxesEnabled: ReadItemCheckBoxesDefault(),
            fileExtensionsVisible: ReadFileExtensionsDefault(),
            hiddenItemsVisible: ReadHiddenItemsDefault());
        BackColor    = ThemeManager.Window;
        _browserBoundsTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _browserBoundsTimer.Tick += (_, _) => ApplyQueuedBrowserBounds();
        _itemCountDebounceTimer = new System.Windows.Forms.Timer
        {
            Interval = ItemCountWatcherDebounceMilliseconds,
        };
        _itemCountDebounceTimer.Tick += OnItemCountDebounceTick;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the IExplorerBrowser instance and navigates to the initial path.
    /// Call once from MainForm.OnShown after the panel has its final dimensions.
    /// </summary>
    public void LaunchExplorer()
    {
        if (_browser != null || _browserThread != null
            || !IsHandleCreated || Width == 0 || Height == 0) return;

        if (IsNetworkPath(_currentPath))
        {
            if (_networkPathStatus is NetworkPathStatus.NotNetwork
                or NetworkPathStatus.Available)
                BeginNetworkProbe(_currentPath);
            return;
        }

        LaunchExplorerAfterProbe();
    }

    private void LaunchExplorerAfterProbe()
    {
        if (_browser != null || _browserThread != null
            || !IsHandleCreated || Width == 0 || Height == 0) return;

        // The shell view owns modal loops used by native drag/drop operations.
        // Keep it on its own STA so those loops cannot stall the WinForms pump.
        NativeMethods.RECT initialBounds = BrowserBounds();
        _browserThread = new BrowserThread(Handle, Width, Height);
        _browserThread.MessageHook = BrowserMessageHook;
        // Do not wait here. ExplorerBrowser.Initialize can synchronously notify
        // ancestor windows, which need the WinForms thread to keep pumping.
        _browserThread.Post(() => LaunchExplorerCore(initialBounds));
    }

    private void LaunchExplorerCore(NativeMethods.RECT initialBounds)
    {
        if (_browser != null || _browserThread == null) return;
        BrowserThread browserThread = _browserThread;

        try
        {
            var clsid = new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");
            var type  = Type.GetTypeFromCLSID(clsid)
                        ?? throw new InvalidOperationException("CLSID_ExplorerBrowser not found.");

            var rect = initialBounds;
            var fs   = CreateHeaderEnabledFolderSettings(FVM_DETAILS);
            var browser = (NativeMethods.IExplorerBrowser)Activator.CreateInstance(type)!;
            _browser = browser;

            // Set a host site before Initialize so the browser never dereferences
            // a null/uninitialised site pointer during navigation.
            _site = new BrowserSiteImpl();
            if (browser is NativeMethods.IObjectWithSite objectWithSite)
                objectWithSite.SetSite(_site);

            // Give ExplorerBrowser an application-specific property bag. The
            // Shell then persists each folder's view mode and icon size when a
            // navigation starts or a view is destroyed, without changing the
            // standalone Windows File Explorer's folder-view preferences.
            int propertyBagResult = browser.SetPropertyBag(
                ExplorerViewStatePropertyBag);
            if (propertyBagResult < 0)
            {
                AppLog.Warn(new COMException(
                        "IExplorerBrowser.SetPropertyBag failed.", propertyBagResult),
                    nameof(LaunchExplorerCore),
                    "Per-folder view-mode persistence is unavailable.");
            }

            // EBO_SHOWFRAMES (0x0002) — navigation pane + command band.
            browser.SetOptions(_showNavPane ? 0x0002u : 0u);
            _renderedNavPaneVisible = _showNavPane;
            int hr = browser.Initialize(browserThread.Handle, ref rect, ref fs);
            if (hr < 0)
                throw new COMException("IExplorerBrowser.Initialize failed.", hr);

            // Subscribe before the first navigation. The callback implementation is
            // deliberately non-nested so its .NET 8 CCW answers QueryInterface reliably.
            _events = new ExplorerBrowserEventsImpl(this);
            IntPtr eventsPtr = Marshal.GetComInterfaceForObject(
                _events, typeof(NativeMethods.IExplorerBrowserEvents));
            try
            {
                hr = browser.Advise(eventsPtr, out _eventsCookie);
                if (hr < 0)
                    throw new COMException("IExplorerBrowser.Advise failed.", hr);
            }
            finally { Marshal.Release(eventsPtr); }

            // Remember that this host had a live browser. If WinForms later
            // recreates our HWND (for example after a DPI-related handle change),
            // OnHandleCreated will restore the browser into the new parent HWND.
            _browserWasLaunched = true;

            BrowseTo(_currentPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(LaunchExplorer));
            DestroyBrowserCore();
            PostToUi(DestroyBrowser);
            ReportInitialNavigationCompleted();
        }
    }

    private bool BrowserMessageHook(NativeMethods.MSG msg)
    {
        var message = Message.Create(msg.hwnd, (int)msg.message, msg.wParam, msg.lParam);
        return PreFilterMessage(ref message);
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* the host closed while posting */ }
    }

    private bool SwitchToBrowserThread(Action action)
    {
        BrowserThread? thread = _browserThread;
        if (thread == null || NativeMethods.GetCurrentThreadId() == thread.ThreadId)
            return false;

        thread.Invoke(action);
        return true;
    }

    private T RunOnBrowserThread<T>(Func<T> func)
    {
        BrowserThread? thread = _browserThread;
        return thread != null && NativeMethods.GetCurrentThreadId() != thread.ThreadId
            ? thread.Invoke(func)
            : func();
    }

    /// <summary>Navigates to the parent of the current folder (no-op at a drive root).</summary>
    public void NavigateUp()
    {
        if (SwitchToBrowserThread(NavigateUp)) return;
        string current = GetCurrentPath().TrimEnd(Path.DirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(current);
        if (parent == null) return;

        // Directory.Exists fails for UNC paths on many network configurations
        // (e.g. \\server as the parent of \\server\share), which would make "up"
        // silently no-op. Let SHParseDisplayName inside NavigateTo validate UNC
        // paths instead, matching the handling in NavigateTo.
        bool isUnc = parent.StartsWith(@"\\", StringComparison.Ordinal);
        if (isUnc || Directory.Exists(parent))
            NavigateTo(parent);
    }

    /// <summary>Navigates the browser to path (no-op if path does not exist).</summary>
    public void NavigateTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (InvokeRequired)
        {
            PostToUi(() => NavigateTo(path));
            return;
        }

        if (IsNetworkPath(path))
        {
            if (_networkPathStatus == NetworkPathStatus.Unavailable
                && PathsReferToSameFolder(_currentPath, path))
                return;
            _currentPath = path;
            BeginNetworkProbe(path);
            return;
        }

        if (!Directory.Exists(path)) return;
        Interlocked.Increment(ref _networkProbeGeneration);
        Volatile.Write(ref _networkBrowseGeneration, 0);
        _currentPath = path;
        SetNetworkPathStatus(NetworkPathStatus.NotNetwork);
        if (_browserThread is { } thread)
            thread.Post(() => BrowseTo(path));
        else
            LaunchExplorerAfterProbe();
    }

    internal NetworkPathStatus NetworkStatus => _networkPathStatus;

    private void SetNetworkPathStatus(NetworkPathStatus status)
    {
        if (_networkPathStatus == status) return;
        _networkPathStatus = status;
        if (status == NetworkPathStatus.Available)
        {
            _consecutiveNetworkMonitorFailures = 0;
            _nextNetworkCheckTick = Environment.TickCount64 + 5000;
            if (_pendingNetworkThemeRebuild && _browser is not null)
                PostToUi(() =>
                {
                    if (!_pendingNetworkThemeRebuild
                        || _networkPathStatus != NetworkPathStatus.Available)
                        return;
                    _pendingNetworkThemeRebuild = false;
                    RecreateForTheme();
                });
        }
        NetworkPathStatusChanged?.Invoke(this, EventArgs.Empty);
        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginNetworkProbe(string path)
    {
        if (InvokeRequired)
        {
            PostToUi(() => BeginNetworkProbe(path));
            return;
        }

        int generation = Interlocked.Increment(ref _networkProbeGeneration);
        Volatile.Write(ref _networkBrowseGeneration, 0);
        SetNetworkPathStatus(NetworkPathStatus.Checking);
        _ = ProbeAndNavigateNetworkAsync(path, generation);
    }

    private async Task ProbeAndNavigateNetworkAsync(string path, int generation)
    {
        bool available;
        try
        {
            available = await NetworkPathAvailability.CheckAsync(
                path, _itemCountWorkerCancellation.Token);
        }
        catch (OperationCanceledException) when (_itemCountWorkerCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ProbeAndNavigateNetworkAsync),
                $"Could not check network location '{path}'.");
            available = false;
        }

        if (IsDisposed || Disposing || generation != Volatile.Read(ref _networkProbeGeneration)
            || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        if (!available)
        {
            SetNetworkPathStatus(NetworkPathStatus.Unavailable);
            ReportInitialNavigationCompleted();
            return;
        }

        Volatile.Write(ref _networkBrowseGeneration, generation);
        try
        {
            if (_browserThread is { } thread)
                thread.Post(() =>
                {
                    if (generation == Volatile.Read(ref _networkProbeGeneration))
                        BrowseTo(path);
                });
            else
                LaunchExplorerAfterProbe();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ProbeAndNavigateNetworkAsync),
                $"Could not open network location '{path}'.");
            SetNetworkPathStatus(NetworkPathStatus.Unavailable);
            ReportInitialNavigationCompleted();
            return;
        }

        _ = WatchNetworkNavigationAsync(path, generation);
    }

    private async Task WatchNetworkNavigationAsync(string path, int generation,
        TimeSpan? timeout = null)
    {
        try
        {
            await Task.Delay(timeout ?? NetworkNavigationTimeout,
                _itemCountWorkerCancellation.Token);
        }
        catch (OperationCanceledException) when (_itemCountWorkerCancellation.IsCancellationRequested)
        {
            return;
        }

        if (IsDisposed || Disposing || generation != Volatile.Read(ref _networkProbeGeneration)
            || _networkPathStatus != NetworkPathStatus.Checking
            || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        // The Shell has not completed its navigation. Keep it covered and let
        // startup continue; only F5 may start another attempt for this tab.
        SetNetworkPathStatus(NetworkPathStatus.Unavailable);
        ReportInitialNavigationCompleted();
    }

    internal void CheckNetworkAvailabilityIfDue()
    {
        if (_networkPathStatus != NetworkPathStatus.Available) return;
        string path = _currentPath;
        long now = Environment.TickCount64;
        if (now < _nextNetworkCheckTick) return;
        _nextNetworkCheckTick = now + 5000;
        int generation = Volatile.Read(ref _networkProbeGeneration);
        _ = MonitorNetworkAvailabilityAsync(path, generation);
    }

    private async Task MonitorNetworkAvailabilityAsync(string path, int generation)
    {
        bool available;
        try
        {
            available = await NetworkPathAvailability.CheckAsync(
                path, _itemCountWorkerCancellation.Token);
        }
        catch (OperationCanceledException) when (_itemCountWorkerCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(MonitorNetworkAvailabilityAsync),
                $"Could not monitor network location '{path}'.");
            available = false;
        }

        ApplyNetworkAvailabilityObservation(path, generation, available);
    }

    internal void ApplyNetworkAvailabilityObservation(
        string path, int generation, bool available)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsDisposed || Disposing
            || generation != Volatile.Read(ref _networkProbeGeneration)
            || _networkPathStatus != NetworkPathStatus.Available
            || !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (available)
        {
            _consecutiveNetworkMonitorFailures = 0;
            return;
        }

        // One slow SMB response must not hide a still-live file view. Require
        // two consecutive failed background checks before covering it.
        if (++_consecutiveNetworkMonitorFailures >= 2)
            SetNetworkPathStatus(NetworkPathStatus.Unavailable);
    }

    /// <summary>Applies the current palette to this host and every native shell child.</summary>
    public void ApplyTheme()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ApplyTheme);
            return;
        }

        BackColor = ThemeManager.Window;
        ForeColor = ThemeManager.Text;
        if (!IsHandleCreated) return;

        QueueBrowserBoundsUpdate();

        ThemeManager.ApplyNativeWindow(Handle);
        // Shell navigation creates parts of the view asynchronously. Re-apply after
        // the current message has completed so newly-created DirectUI/list/tree HWNDs
        // cannot retain the previous theme.
        if (!Disposing && !IsDisposed)
            BeginInvoke(() =>
            {
                if (IsHandleCreated && !Disposing && !IsDisposed)
                {
                    ThemeManager.ApplyNativeWindow(Handle);
                }
            });
    }

    /// <summary>
    /// Recreates the native ExplorerBrowser after an application-theme change.
    /// DirectUI shell views choose their light/dark resources when they are created
    /// and do not reliably replace those resources in response to WM_THEMECHANGED.
    /// </summary>
    internal void RecreateForTheme()
    {
        BackColor = ThemeManager.Window;
        ForeColor = ThemeManager.Text;

        // DirectUI chooses the file list's light/dark resources when its Shell
        // view is created. Recoloring an existing network view leaves that list
        // in the old theme, so rebuild it after checking that the share responds.
        if (IsNetworkPath(_currentPath))
        {
            ApplyTheme();
            if (_browser is not null)
            {
                if (_networkPathStatus == NetworkPathStatus.Available)
                {
                    _pendingNetworkThemeRebuild = false;
                    int generation = Interlocked.Increment(
                        ref _networkViewRebuildGeneration);
                    _ = RecreateNetworkBrowserAsync(_currentPath,
                        _showNavPane, generation);
                }
                else
                {
                    _pendingNetworkThemeRebuild = true;
                }
            }
            return;
        }

        // Tabs that have never been activated do not have a browser yet. Their
        // first LaunchExplorer call will automatically use the current theme.
        if (_browser == null) return;

        string path = GetCurrentPath();
        string? selectedPath = GetSelectedItemPath();
        bool restoreFocus = ContainsFocus;

        DestroyBrowser();
        _currentPath = path;
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        LaunchExplorer();

        // The folder contents arrive asynchronously after BrowseToIDList. Restore
        // the selection on a later message so switching appearance does not make
        // the user's current item appear to have been deselected.
        if (selectedPath != null || restoreFocus)
        {
            StartOneShotTimer(200, () =>
            {
                if (selectedPath != null) SelectItem(selectedPath);
                if (restoreFocus) FocusShellView();
            });
        }
    }

    /// <summary>Moves Win32 keyboard focus into the shell view's file-list window.</summary>
    public void FocusShellView()
    {
        if (!IsHandleCreated) return;
        if (_browser != null && RunOnBrowserThread(() => ActivateShellView(takeFocus: true))) return;
        Focus();
    }

    /// <summary>
    /// Gets the client-area bounds occupied by the native file view. This is
    /// used only to align the application-owned responsive Details view; the
    /// native view remains alive underneath it for Shell integration.
    /// </summary>
    internal bool TryGetFileViewBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!IsHandleCreated) return false;

        IntPtr defView = FindDescendant(Handle, "SHELLDLL_DefView");
        if (defView == IntPtr.Zero
            || !NativeMethods.GetWindowRect(defView, out NativeMethods.RECT rect))
            return false;

        var topLeft = new NativeMethods.POINT(rect.Left, rect.Top);
        var bottomRight = new NativeMethods.POINT(rect.Right, rect.Bottom);
        if (!NativeMethods.ScreenToClient(Handle, ref topLeft)
            || !NativeMethods.ScreenToClient(Handle, ref bottomRight))
            return false;

        bounds = Rectangle.FromLTRB(topLeft.x, topLeft.y, bottomRight.x, bottomRight.y);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    /// <summary>
    /// Activates both layers of the embedded browser. IInputObject activates the
    /// Explorer frame; IShellView activates the actual file list and controls its
    /// focus/selection painting state.
    /// </summary>
    private bool ActivateShellView(bool takeFocus)
    {
        if (_browser == null) return false;
        if (_browserThread is not { } thread
            || NativeMethods.GetCurrentThreadId() != thread.ThreadId)
        {
            AppLog.Warn(null, nameof(ActivateShellView),
                "Ignored an Explorer focus request from the wrong thread.");
            return false;
        }

        try
        {
            if (_browser is NativeMethods.IInputObject io)
                io.UIActivateIO(1, IntPtr.Zero);

            var svId = new Guid("000214E3-0000-0000-C000-000000000046");
            if (_browser.GetCurrentView(ref svId, out IntPtr ppv) < 0 || ppv == IntPtr.Zero)
                return false;
            try
            {
                var view = (NativeMethods.IShellView)Marshal.GetObjectForIUnknown(ppv);
                // SVUIA_ACTIVATE_NOFOCUS = 1; SVUIA_ACTIVATE_FOCUS = 2.
                bool activated = view.UIActivate(takeFocus ? 2u : 1u) >= 0;
                if (activated && takeFocus)
                {
                    // UIActivate(FOCUS) does not consistently move Win32 focus to
                    // the DirectUI item surface when IExplorerBrowser is embedded.
                    // Focus it explicitly; otherwise items are selected in the shell
                    // model but Windows paints no selection background.
                    IntPtr defView = FindDescendant(Handle, "SHELLDLL_DefView");
                    IntPtr itemView = defView == IntPtr.Zero
                        ? IntPtr.Zero
                        : FindDescendant(defView, "DirectUIHWND");
                    NativeMethods.SetFocus(itemView != IntPtr.Zero ? itemView : defView);
                }
                return activated;
            }
            finally { Marshal.Release(ppv); }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ActivateShellView));
            return false;
        }
    }

    private void QueueShellViewFocusActivation()
    {
        BrowserThread? thread = _browserThread;
        if (thread is null || _browser is null || !Visible
            || IsDisposed || Disposing || TabBar.IsTabDragInProgress)
            return;

        // WM_SETFOCUS can arrive inside the UI thread's OLE tab-drag loop.
        // Never enter the browser COM object from that thread or block this
        // focus message waiting for the browser STA.
        thread.Post(() =>
        {
            if (ReferenceEquals(thread, _browserThread)
                && _browser is not null
                && !TabBar.IsTabDragInProgress)
                ActivateShellView(takeFocus: true);
        });
    }

    /// <summary>
    /// Returns the latest folder reported by the browser navigation callback.
    /// This intentionally does not synchronously query the browser STA: a Shell
    /// drag/drop operation may occupy that STA until a long copy/move completes.
    /// </summary>
    public string GetCurrentPath()
    {
        return _currentPath;
    }

    /// <summary>
    /// Returns the cached path immediately and periodically queues the original live
    /// COM query as a fallback. Navigation callbacks normally keep the snapshot current;
    /// throttling the fallback avoids redundant Shell calls on every 300 ms UI tick.
    /// </summary>
    internal string GetCurrentPathForPolling()
    {
        if (_networkPathStatus is NetworkPathStatus.Checking
            or NetworkPathStatus.Unavailable)
            return _currentPath;

        BrowserThread? thread = _browserThread;
        long now = Environment.TickCount64;
        long nextRefresh = Volatile.Read(ref _nextPathRefreshTick);
        if (thread != null
            && now >= nextRefresh
            && Interlocked.CompareExchange(ref _nextPathRefreshTick, now + 2000, nextRefresh) == nextRefresh
            && Interlocked.Exchange(ref _pathRefreshPending, 1) == 0)
        {
            thread.Post(() =>
            {
                try
                {
                    string? livePath = QueryLivePath();
                    if (livePath != null) _currentPath = livePath;
                }
                finally { Volatile.Write(ref _pathRefreshPending, 0); }
            });
        }

        return _currentPath;
    }

    /// <summary>
    /// Returns the most recent first-selected-item snapshot without entering the
    /// browser STA. Call <see cref="RequestSelectionSnapshotRefresh"/> to update it.
    /// </summary>
    internal string? GetSelectedItemPathSnapshot() =>
        Volatile.Read(ref _cachedSelectedItemPath);

    /// <summary>
    /// Returns the most recent complete selection snapshot without entering the
    /// browser STA. Call <see cref="RequestSelectionSnapshotRefresh"/> to update it.
    /// </summary>
    internal IReadOnlyList<string> GetSelectedFileSystemPathsSnapshot() =>
        Volatile.Read(ref _cachedSelectedFileSystemPaths);

    /// <summary>
    /// Coalesces a fresh Shell selection query onto the browser STA. The WinForms
    /// thread never waits for a native view that may be occupied by drag/drop or
    /// another modal Shell operation.
    /// </summary>
    internal void RequestSelectionSnapshotRefresh()
    {
        BrowserThread? thread = _browserThread;
        if (thread == null || Interlocked.Exchange(ref _selectionRefreshPending, 1) != 0)
            return;

        thread.Post(RefreshSelectionSnapshots);
    }

    private void RefreshSelectionSnapshots()
    {
        try
        {
            string? selectedItemPath;
            string[] selectedPaths;
            try
            {
                selectedItemPath = GetSelectedItemPath();
                selectedPaths = GetSelectedFileSystemPaths();
            }
            catch (Exception ex)
            {
                AppLog.Debug(ex, nameof(RefreshSelectionSnapshots),
                    "Could not refresh the selected-item snapshot.");
                selectedItemPath = null;
                selectedPaths = [];
            }

            string? previousItemPath = Volatile.Read(ref _cachedSelectedItemPath);
            string[] previousPaths = Volatile.Read(ref _cachedSelectedFileSystemPaths);
            bool changed = !string.Equals(
                    previousItemPath, selectedItemPath, StringComparison.OrdinalIgnoreCase)
                || !previousPaths.SequenceEqual(
                    selectedPaths, StringComparer.OrdinalIgnoreCase);

            Volatile.Write(ref _cachedSelectedItemPath, selectedItemPath);
            Volatile.Write(ref _cachedSelectedFileSystemPaths, selectedPaths);
            if (changed)
                PostToUi(() => SelectionChanged?.Invoke(this, EventArgs.Empty));
        }
        finally
        {
            Volatile.Write(ref _selectionRefreshPending, 0);
        }
    }

    // ── Keyboard routing ──────────────────────────────────────────────────────

    protected override void WndProc(ref Message m)
    {
        const int WM_DESTROY  = 0x0002;
        const int WM_SETFOCUS = 0x0007;

        if (m.Msg == WM_DESTROY)
            DestroyBrowser();

        base.WndProc(ref m);

        if (m.Msg == WM_SETFOCUS)
            QueueShellViewFocusActivation();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        // Returning true tells WinForms' PreProcessMessage that this control
        // wants the key as raw input, so it skips ProcessCmdKey and lets
        // DispatchMessage deliver the WM_KEYDOWN to the focused native window.
        // This covers arrows, F2, Delete, and every other shell shortcut.
        return _browser != null || base.IsInputKey(keyData);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Belt-and-suspenders: never consume an accelerator while the browser
        // is active — pass everything through to the native shell windows.
        if (_browser != null) return false;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── IMessageFilter — Ctrl+A select-all ───────────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyNativeWindow(Handle, includeChildren: false);
        Application.AddMessageFilter(this);

        // The initial handle is launched explicitly by PanelView after layout.
        // A later handle belongs to a host that was already launched, so defer
        // recreation until WinForms has finished constructing the new HWND.
        if (_browserWasLaunched && _browser == null && !Disposing && !IsDisposed)
        {
            BeginInvoke(() =>
            {
                if (IsHandleCreated && !Disposing && !IsDisposed && _browser == null)
                    LaunchExplorer();
            });
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// Passes Ctrl+key messages through IShellView::TranslateAccelerator.
    ///
    /// A standard Win32 message loop calls TranslateAccelerator before DispatchMessage;
    /// WinForms never does.  Shell accelerators like Ctrl+A (select all), Ctrl+C (copy),
    /// and Backspace (navigate up) live in the shell view's accelerator table — they are
    /// NOT plain WM_KEYDOWN handlers — so they silently do nothing without this call.
    ///
    /// Arrow keys are NOT shell accelerators, so TranslateAccelerator returns S_FALSE for
    /// them and they continue to reach the native ListView window unmodified.
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        // Intercept the posted button-up as well as WM_CONTEXTMENU. Some Shell
        // builds create the latter with SendMessage, which bypasses this thread's
        // message-pump hook. Swallowing button-up prevents the native menu from
        // also appearing after the shared application-owned menu is requested.
        bool isNavigationContextMenu =
            m.Msg is NativeMethods.WM_CONTEXTMENU or WM_RBUTTONUP
            && _browser != null
            && Visible
            && (m.HWnd == Handle || NativeMethods.IsChild(Handle, m.HWnd))
            && IsInNavigationTree(m.HWnd)
            && (m.Msg != WM_RBUTTONUP || IsNavigationTreeItemAtCursor());
        if (isNavigationContextMenu)
        {
            Point screenLocation = m.Msg == NativeMethods.WM_CONTEXTMENU
                ? GetContextMenuScreenLocation(m.LParam, Cursor.Position)
                : Cursor.Position;
            bool keyboardRequest = m.Msg == NativeMethods.WM_CONTEXTMENU
                && m.LParam.ToInt64() == -1;
            if (TryRequestNavigationContextMenu(
                    screenLocation, allowSelectedFallback: keyboardRequest))
                return true;
        }

        bool isFileContextMenu = m.Msg is NativeMethods.WM_CONTEXTMENU
            or WM_RBUTTONUP
            && _browser != null
            && Visible
            && (m.HWnd == Handle || NativeMethods.IsChild(Handle, m.HWnd))
            && IsInFileListArea(m.HWnd);
        if (isFileContextMenu)
        {
            Point screenLocation = m.Msg == NativeMethods.WM_CONTEXTMENU
                ? GetContextMenuScreenLocation(m.LParam, Cursor.Position)
                : Cursor.Position;
            if (TryRequestFileContextMenu(screenLocation))
                return true;
        }

        bool isBrowserFocus = m.Msg == WM_SETFOCUS
            && _browser != null
            && Visible
            && (m.HWnd == Handle || NativeMethods.IsChild(Handle, m.HWnd));
        if (isBrowserFocus)
            PostToUi(() => ShellFocused?.Invoke(this, EventArgs.Empty));

        bool isBrowserMouseDown =
            m.Msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN
            && _browser != null
            && Visible
            && (m.HWnd == Handle || NativeMethods.IsChild(Handle, m.HWnd));
        if (isBrowserMouseDown)
            PostToUi(() => ShellMouseDown?.Invoke(this, EventArgs.Empty));

        if (TryKeepNetworkFolderOrderOnTypeClick(m))
            return true;

        // Quick access pins are Shell links. Let this browser handle their
        // activation so a click does not launch a separate Explorer window.
        if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK
            && IsInNavigationTree(m.HWnd)
            && !IsNavigationTreeExpandButtonAtCursor()
            && GetNavigationPathAtScreenPoint(Cursor.Position) is { } pinnedPath
            && QuickAccessService.IsPinned(pinnedPath))
        {
            if (!PathsReferToSameFolder(_currentPath, pinnedPath))
                PostToUi(() => NavigateTo(pinnedPath));
            return true;
        }

        // ExplorerBrowser does not provide a general-purpose selection-changed
        // event to its host. Queue one coalesced snapshot after input has been
        // dispatched to the native view; the browser thread's posted work runs
        // after this message, so the resulting snapshot sees the new selection.
        bool isBrowserSelectionInput = _browser != null
            && Visible
            && (m.HWnd == Handle || NativeMethods.IsChild(Handle, m.HWnd))
            && IsSelectionChangingInput(m.Msg);
        bool isNavigationTreeInput = isBrowserSelectionInput
            && IsInNavigationTree(m.HWnd);
        if (ShouldRefreshSelectionSnapshot(
                m.Msg, isBrowserSelectionInput, isNavigationTreeInput))
            RequestSelectionSnapshotRefresh();

        bool isBrowserNavigationMouseDown = m.Msg == WM_LBUTTONDOWN
            && isNavigationTreeInput;
        if (isBrowserNavigationMouseDown)
        {
            bool isExpandButton = IsNavigationTreeExpandButtonAtCursor();
            if (!isExpandButton)
                QueueNavigationTreeFallback();
        }

        // Double-click on blank space → navigate to parent folder.
        //
        // We handle both WM_LBUTTONDBLCLK and WM_LBUTTONDOWN because IExplorerBrowser
        // child windows often lack the CS_DBLCLKS class style.  Without CS_DBLCLKS the
        // system never posts WM_LBUTTONDBLCLK — it just posts two WM_LBUTTONDOWN messages —
        // so WM_LBUTTONDBLCLK alone never fires for those windows.  The manual detector
        // fires on the second WM_LBUTTONDOWN that lands within the system double-click time
        // and distance of a previous blank-area click.  When CS_DBLCLKS IS present the
        // second press arrives as WM_LBUTTONDBLCLK (not a second WM_LBUTTONDOWN), so both
        // paths are mutually exclusive and neither triggers twice.
        if ((m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK)
            && _browser != null
            && Visible
            && (m.HWnd == Handle || NativeMethods.IsChild(Handle, m.HWnd)))
        {
            Point cur = Cursor.Position;
            if (m.Msg == WM_LBUTTONDBLCLK)
            {
                // CS_DBLCLKS path: the first click was already dispatched before
                // this message, so IsBlankAreaClick can check selection state.
                if (IsBlankAreaClick())
                {
                    _lastBlankClickTick = 0;
                    NavigateUp();
                    return true;
                }
            }
            else // WM_LBUTTONDOWN
            {
                long now = Environment.TickCount64;
                bool withinThreshold =
                    _lastBlankClickTick > 0
                    && (now - _lastBlankClickTick) <= SystemInformation.DoubleClickTime
                    && Math.Abs(cur.X - _lastBlankClickPt.X) <= SystemInformation.DoubleClickSize.Width
                    && Math.Abs(cur.Y - _lastBlankClickPt.Y) <= SystemInformation.DoubleClickSize.Height;

                if (withinThreshold)
                {
                    // Second click: first click already dispatched → selection state is
                    // current. IsBlankAreaClick queries IFolderView.ItemCount to decide.
                    _lastBlankClickTick = 0;
                    if (IsBlankAreaClick())
                    {
                        NavigateUp();
                        return true;
                    }
                }
                else if (IsInFileListArea(m.HWnd))
                {
                    // First click in the file-list area: start the double-click timer.
                    // We record every click (item or blank) here; the blank/item
                    // distinction is made on the second click via selection state.
                    _lastBlankClickTick = now;
                    _lastBlankClickPt   = cur;
                }
                else
                {
                    _lastBlankClickTick = 0;
                }
            }
        }

        if ((m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN)
            && _browser != null
            && (m.HWnd == Handle || NativeMethods.IsChild(Handle, m.HWnd)))
        {
            if (m.Msg == WM_KEYDOWN && m.WParam == (IntPtr)Keys.Enter
                && ModifierKeys == Keys.None && IsInNavigationTree(m.HWnd)
                && GetSelectedNavigationPath() is { } selectedPinPath
                && QuickAccessService.IsPinned(selectedPinPath))
            {
                PostToUi(() => NavigateTo(selectedPinPath));
                return true;
            }

            // The native Explorer windows live on this browser STA, so the main
            // WinForms IMessageFilter never sees their keystrokes. Forward the
            // application-wide shortcuts to the UI thread explicitly.
            CommandBar.Cmd? shortcut = MainForm.GetApplicationShortcut(
                m.Msg, m.WParam, ModifierKeys);
            if (shortcut.HasValue)
            {
                PostToUi(() => ApplicationShortcutRequested?.Invoke(this, shortcut.Value));
                return true;
            }

            // ── Type-to-filter key interception ──────────────────────────────
            if (m.Msg == WM_KEYDOWN && IsInFileListArea(m.HWnd) && !IsEditControl())
            {
                uint vk = (uint)m.WParam.ToInt32();

                if (IsFiltering)
                {
                    if (vk == 0x08 /* VK_BACK */ && ModifierKeys == Keys.None)
                    {
                        PostToUi(() => FilterBackspaceTyped?.Invoke(this, EventArgs.Empty));
                        return true;
                    }
                    if (vk == 0x1B /* VK_ESCAPE */ && ModifierKeys == Keys.None)
                    {
                        PostToUi(() => FilterEscapePressed?.Invoke(this, EventArgs.Empty));
                        return true;
                    }
                }

                // Start or extend filter on any printable character with no modifiers.
                // Space is excluded when no filter is active so it falls through to
                // the QuickLook handler below; once filtering is active, spaces are
                // accepted so the user can search for names with spaces.
                if (ModifierKeys == Keys.None)
                {
                    char c = GetCharFromVk(m.WParam);
                    if (c >= 0x20 && c != 0x7F && (IsFiltering || c != ' '))
                    {
                        PostToUi(() => FilterCharInput?.Invoke(this, c));
                        return true;
                    }
                }
            }
            // ─────────────────────────────────────────────────────────────────

            // Do not let the shell start its modal paste loop on our UI thread.
            // Text-edit controls still receive Ctrl+V normally (for example while
            // renaming an item).
            if (m.Msg == WM_KEYDOWN
                && m.WParam == (IntPtr)0x56 // VK_V
                && ModifierKeys == Keys.Control
                && !IsEditControl())
            {
                Paste();
                return true;
            }

            // Route deletion through the tracked operation host rather than allowing
            // the embedded shell to enter its own modal delete loop.
            if (m.Msg == WM_KEYDOWN && m.WParam == (IntPtr)0x2E /* VK_DELETE */
                && (ModifierKeys == Keys.None || ModifierKeys == Keys.Shift))
            {
                Delete(permanently: ModifierKeys == Keys.Shift);
                return true;
            }

            // QuickLook: Space sends the focused/selected item to QuickLook for preview.
            if (m.Msg == WM_KEYDOWN && m.WParam == (IntPtr)0x20
                && QuickLookEnabled && ModifierKeys == Keys.None)
            {
                string? sel = GetSelectedItemPath();
                if (sel != null)
                {
                    QuickLookFailure? failure = InvokeQuickLook(sel);
                    if (failure != null
                        && Interlocked.Exchange(ref _quickLookAlertPending, 1) == 0)
                    {
                        PostToUi(() =>
                        {
                            try { QuickLookUnavailable?.Invoke(this, failure.Value); }
                            finally { Volatile.Write(ref _quickLookAlertPending, 0); }
                        });
                    }
                    return true;
                }
            }

            return TryShellTranslateAccelerator(ref m);
        }
        return false;
    }

    public void CreateNewFolder()
    {
        if (SwitchToBrowserThread(CreateNewFolder)) return;
        string currentPath = GetCurrentPath();
        if (!Directory.Exists(currentPath)) return;

        // Find a unique name: "New folder", "New folder (2)", …
        string name = "New folder";
        string path = Path.Combine(currentPath, name);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++)
        {
            name = $"New folder ({i})";
            path = Path.Combine(currentPath, name);
        }

        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { AppLog.Warn(ex, nameof(CreateNewFolder)); return; }
        BeginRenameCreatedItem(path);
    }

    internal void BeginRenameCreatedItem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (SwitchToBrowserThread(() => BeginRenameCreatedItem(path))) return;

        // Shell change notifications populate the view asynchronously. Once
        // the item is selectable, ask the Shell to open its normal inline edit
        // field. The managed Details view handles its own rename editor.
        var timer = new System.Windows.Forms.Timer { Interval = 150 };
        int attempts = 0;
        bool notified = false;
        timer.Tick += (_, _) =>
        {
            bool stop = IsDisposed || Disposing || _browser is null
                || !PathsReferToSameFolder(Path.GetDirectoryName(path),
                    GetCurrentPath()) || ++attempts >= 40;
            if (!stop && (File.Exists(path) || Directory.Exists(path)))
            {
                if (!notified)
                {
                    ShellNewMenu.NotifyCreatedItem(path, Directory.Exists(path));
                    notified = true;
                }
                if (SelectItem(path)
                    && PathsReferToSameFolder(GetSelectedItemPath(), path)
                    && SelectItem(path, edit: true))
                    stop = true;
            }

            if (!stop) return;
            timer.Stop();
            timer.Dispose();
        };
        timer.Start();
    }

    /// <summary>Selects a file-system item in the current shell view.</summary>
    internal void SelectItemPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Volatile.Write(ref _cachedSelectedItemPath, path);
        SelectItem(path);
    }

    private bool SelectItem(string path, bool edit = false)
    {
        if (_browserThread is { } thread
            && NativeMethods.GetCurrentThreadId() != thread.ThreadId)
            return thread.Invoke(() => SelectItem(path, edit));
        if (_browser == null) return false;
        bool selected = false;
        try
        {
            var svId = new Guid("000214E3-0000-0000-C000-000000000046"); // IShellView
            if (_browser.GetCurrentView(ref svId, out IntPtr ppv) < 0 || ppv == IntPtr.Zero)
                return false;
            try
            {
                var sv = (NativeMethods.IShellView)Marshal.GetObjectForIUnknown(ppv);

                int hr = NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidlAbs, 0, out _);
                if (hr < 0 || pidlAbs == IntPtr.Zero) return false;
                try
                {
                    // ILFindLastID returns a pointer INTO pidlAbs for the last SHITEMID.
                    // This single-item + null-terminator slice is a valid child PIDL.
                    // IShellView::SelectItem expects a child (relative) PIDL, not absolute.
                    IntPtr pidlChild = NativeMethods.ILFindLastID(pidlAbs);
                    if (pidlChild == IntPtr.Zero) return false;

                    // Restored selections receive keyboard focus and the selection
                    // mark, making their highlight clear in either pane.
                    const uint SVSI_SELECT          = 0x01;
                    const uint SVSI_EDIT            = 0x03;
                    const uint SVSI_DESELECTOTHERS  = 0x04;
                    const uint SVSI_ENSUREVISIBLE   = 0x08;
                    const uint SVSI_FOCUSED         = 0x10;
                    const uint SVSI_SELECTIONMARK   = 0x40;
                    uint flags = (edit ? SVSI_EDIT : SVSI_SELECT)
                               | SVSI_DESELECTOTHERS
                               | SVSI_ENSUREVISIBLE
                               | SVSI_FOCUSED
                               | SVSI_SELECTIONMARK;
                    selected = sv.SelectItem(pidlChild, flags) >= 0;
                }
                finally { NativeMethods.CoTaskMemFree(pidlAbs); }
            }
            finally { Marshal.Release(ppv); }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(SelectItem));
            return false;
        }
        if (selected) RequestSelectionSnapshotRefresh();
        return selected;
    }

    private bool TryShellTranslateAccelerator(ref Message m)
    {
        IntPtr pMsg = IntPtr.Zero;
        try
        {
            var msg = new NativeMethods.MSG
            {
                hwnd    = m.HWnd,
                message = (uint)m.Msg,
                wParam  = m.WParam,
                lParam  = m.LParam,
            };
            pMsg = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSG>());
            Marshal.StructureToPtr(msg, pMsg, false);

            // Primary path: IInputObject::TranslateAcceleratorIO on the browser.
            // IExplorerBrowser implements IInputObject and routes the message to
            // both the navigation frame (Ctrl+Shift+N, Ctrl+F, etc.) and the inner
            // shell view (Ctrl+A, F2, Alt+Enter, Shift+Delete, etc.).
            if (_browser is NativeMethods.IInputObject io && io.TranslateAcceleratorIO(pMsg) == 0)
                return true;

            // Fallback: IShellView::TranslateAccelerator for view-only accelerators
            // if the browser's IInputObject doesn't handle the key.
            var svId = new Guid("000214E3-0000-0000-C000-000000000046");
            if (_browser!.GetCurrentView(ref svId, out IntPtr ppv) >= 0 && ppv != IntPtr.Zero)
            {
                try
                {
                    var sv = (NativeMethods.IShellView)Marshal.GetObjectForIUnknown(ppv);
                    return sv.TranslateAccelerator(pMsg) == 0;
                }
                finally { Marshal.Release(ppv); }
            }
            return false;
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(TryShellTranslateAccelerator)); return false; }
        finally { if (pMsg != IntPtr.Zero) Marshal.FreeHGlobal(pMsg); }
    }

    // ── Resize ────────────────────────────────────────────────────────────────

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        QueueBrowserBoundsUpdate();
    }

    private void QueueBrowserBoundsUpdate()
    {
        if (_browserThread == null || Width <= 0 || Height <= 0) return;

        // A splitter drag can generate a resize for every pointer pixel. Updating
        // the native host synchronously from the WinForms thread while queuing
        // SetRect to the browser STA lets the namespace tree paint at mismatched
        // sizes. Coalesce updates to roughly 30 fps and apply both operations on
        // the browser STA in one turn of its message pump.
        if (!_browserBoundsTimer.Enabled)
            _browserBoundsTimer.Start();
    }

    private void ApplyQueuedBrowserBounds()
    {
        _browserBoundsTimer.Stop();

        BrowserThread? thread = _browserThread;
        if (thread == null || Width <= 0 || Height <= 0) return;

        int width = Width;
        int height = Height;
        NativeMethods.RECT rect = BrowserBounds(width, height);
        thread.Post(() =>
        {
            NativeMethods.MoveWindow(thread.Handle, 0, 0, width, height, repaint: true);
            _browser?.SetRect(IntPtr.Zero, rect);
            NativeMethods.RedrawWindow(thread.Handle, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.RDW_INVALIDATE
                | NativeMethods.RDW_ALLCHILDREN
                | NativeMethods.RDW_UPDATENOW);
        });
    }

    private NativeMethods.RECT BrowserBounds()
        => BrowserBounds(Width, Height);

    private NativeMethods.RECT BrowserBounds(int width, int height)
    {
        // ExplorerBrowser's DirectUI status bar immediately repaints its generic
        // "N items" value after SetStatusTextSB. Extend the browser below this
        // host so the native strip is clipped; PanelView supplies a stable,
        // theme-aware replacement directly below it.
        int bottom = height + LogicalToDeviceUnits(NativeStatusBarHeight);
        return new NativeMethods.RECT(0, 0, width, bottom);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _itemCountMonitoringActive = false;
            _itemCountDebounceTimer.Stop();
            _itemCountDebounceTimer.Dispose();
            DisposeItemCountWatcher();
            _itemCountWorkerCancellation.Cancel();
            _itemCountWorkerCancellation.Dispose();
            _browserBoundsTimer.Stop();
            _browserBoundsTimer.Dispose();
            foreach (var timer in _pendingTimers.ToArray())
            {
                timer.Stop();
                timer.Dispose();
            }
            _pendingTimers.Clear();
            DestroyBrowser();
        }
        base.Dispose(disposing);
    }

    private void StartOneShotTimer(int interval, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = interval };
        _pendingTimers.Add(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _pendingTimers.Remove(timer);
            timer.Dispose();
            if (!IsDisposed && !Disposing)
                action();
        };
        timer.Start();
    }

    // ── Command bar helpers ───────────────────────────────────────────────────

    /// <summary>Changes the view mode (FVM_* constant: 1=icons 2=small 3=list 4=details 5=thumbnail 6=tile).</summary>
    public void SetViewMode(uint viewMode)
    {
        if (SwitchToBrowserThread(() => SetViewMode(viewMode))) return;
        if (_browser == null) return;
        try
        {
            var fvId = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
            if (_browser.GetCurrentView(ref fvId, out IntPtr ppv) < 0 || ppv == IntPtr.Zero) return;
            int result;
            try
            {
                result = ((NativeMethods.IFolderView)
                    Marshal.GetObjectForIUnknown(ppv)).SetCurrentViewMode(viewMode);
            }
            finally { Marshal.Release(ppv); }

            if (result >= 0)
                UpdateCurrentFolderViewState(viewMode, GetDefaultIconSize(viewMode));

            // SetCurrentViewMode alone does not clear a saved
            // FWF_NOCOLUMNHEADER flag.  SetFolderSettings does, and also makes
            // the corrected settings the default for subsequently browsed folders.
            if (viewMode == FVM_DETAILS)
            {
                ApplyFolderSettings(viewMode);
                FixColumnHeadersOnCurrentView();
            }
        }
        catch (Exception ex) { AppLog.Warn(ex, nameof(SetViewMode)); }
    }

    internal void EnsureColumnHeaders()
    {
        BrowserThread? thread = _browserThread;
        if (thread != null && NativeMethods.GetCurrentThreadId() != thread.ThreadId)
        {
            thread.Post(EnsureColumnHeaders);
            return;
        }

        if (_browser == null) return;
        if (TryGetCurrentFolderViewState(out ExplorerFolderViewState state)
            && state.ViewMode == FVM_DETAILS)
            FixColumnHeadersOnCurrentView();
    }

    private void FixColumnHeadersOnCurrentView()
    {
        if (_browser == null) return;
        try
        {
            var fv2Id = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            if (_browser.GetCurrentView(ref fv2Id, out IntPtr ppv) >= 0 && ppv != IntPtr.Zero)
            {
                try
                {
                    var fv2 = (NativeMethods.IFolderView2)Marshal.GetObjectForIUnknown(ppv);
                    fv2.SetCurrentFolderFlags(FWF_NOCOLUMNHEADER | FWF_NOHEADERINALLVIEWS, 0);
                }
                finally { Marshal.Release(ppv); }
            }
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(FixColumnHeadersOnCurrentView)); }
    }

    /// <summary>Sets view mode and icon size via IFolderView2 (for Medium Icons = FVM_ICON + 48px).</summary>
    public void SetViewModeAndIconSize(uint viewMode, int iconSize)
    {
        if (SwitchToBrowserThread(() => SetViewModeAndIconSize(viewMode, iconSize))) return;
        if (_browser == null) return;
        try
        {
            var fv2Id = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            if (_browser.GetCurrentView(ref fv2Id, out IntPtr ppv) < 0 || ppv == IntPtr.Zero) return;
            int result;
            try
            {
                result = ((NativeMethods.IFolderView2)
                    Marshal.GetObjectForIUnknown(ppv))
                    .SetViewModeAndIconSize(viewMode, iconSize);
            }
            finally { Marshal.Release(ppv); }
            if (result >= 0)
                UpdateCurrentFolderViewState(viewMode, iconSize);
        }
        catch (Exception ex) { AppLog.Warn(ex, nameof(SetViewModeAndIconSize)); }
    }

    /// <summary>Destroys and recreates the browser to apply the new nav-pane visibility.</summary>
    internal void ToggleNavPane()
    {
        _showNavPane = !_showNavPane;
        string path = GetCurrentPath();
        if (IsNetworkPath(path))
        {
            int generation = Interlocked.Increment(
                ref _networkViewRebuildGeneration);
            if (_browser is not null
                && _networkPathStatus == NetworkPathStatus.Available
                && _showNavPane != _renderedNavPaneVisible)
                _ = RecreateNetworkBrowserAsync(path, _showNavPane, generation);
            return;
        }
        DestroyBrowser();
        _currentPath = path;
        if (IsHandleCreated && Width > 0 && Height > 0)
            LaunchExplorer();
    }

    private async Task RecreateNetworkBrowserAsync(
        string path, bool showNavigationPane, int generation)
    {
        string? selectedPath = Volatile.Read(ref _cachedSelectedItemPath);
        bool restoreFocus = ContainsFocus;
        bool available;
        try
        {
            // Keep the current file view on screen during the check. A slightly
            // longer limit avoids treating a busy, already-open share as missing.
            available = await NetworkPathAvailability.CheckAsync(path,
                _itemCountWorkerCancellation.Token,
                timeout: TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (_itemCountWorkerCancellation.IsCancellationRequested)
        {
            return;
        }

        if (IsDisposed || Disposing
            || generation != Volatile.Read(ref _networkViewRebuildGeneration)
            || _showNavPane != showNavigationPane)
            return;

        if (!PathsReferToSameFolder(path, _currentPath))
        {
            _showNavPane = _renderedNavPaneVisible;
            NavigationPaneVisibilityChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!available || _networkPathStatus != NetworkPathStatus.Available)
        {
            if (_showNavPane != _renderedNavPaneVisible)
            {
                _showNavPane = _renderedNavPaneVisible;
                NavigationPaneVisibilityChanged?.Invoke(this, EventArgs.Empty);
            }
            AppLog.Warn(null, nameof(RecreateNetworkBrowserAsync),
                "The network location did not respond; the view change was not completed.");
            return;
        }

        EventHandler? restoreView = null;
        restoreView = (_, _) =>
        {
            if (_networkPathStatus is not (NetworkPathStatus.Available
                or NetworkPathStatus.Unavailable))
                return;
            NetworkPathStatusChanged -= restoreView;
            if (_networkPathStatus != NetworkPathStatus.Available
                || generation != Volatile.Read(ref _networkViewRebuildGeneration))
                return;
            if (selectedPath is not null || restoreFocus)
                StartOneShotTimer(200, () =>
                {
                    if (selectedPath is not null)
                        SelectItem(selectedPath);
                    if (restoreFocus)
                        FocusShellView();
                });
        };
        NetworkPathStatusChanged += restoreView;
        DestroyBrowser();
        _currentPath = path;
        int browseGeneration = Interlocked.Increment(ref _networkProbeGeneration);
        Volatile.Write(ref _networkBrowseGeneration, browseGeneration);
        SetNetworkPathStatus(NetworkPathStatus.Checking);
        LaunchExplorerAfterProbe();
        _ = WatchNetworkNavigationAsync(path, browseGeneration,
            NavigationPaneNetworkNavigationTimeout);
    }

    internal bool IsNavPaneVisible => _showNavPane;

    internal uint CurrentViewMode =>
        unchecked((uint)Volatile.Read(ref _currentViewMode));

    internal async Task<IReadOnlyList<ExplorerShellColumn>> GetVisibleShellColumnsAsync(
        CancellationToken cancellationToken = default)
    {
        BrowserThread? thread = _browserThread;
        if (thread is null) return [];

        IReadOnlyList<ExplorerShellColumn> columns = [];
        await thread.InvokeAsync(
            () => columns = ReadVisibleShellColumns(), cancellationToken);
        return columns;
    }

    internal async Task<NativeMethods.SORTCOLUMN?> GetCurrentShellSortColumnAsync(
        CancellationToken cancellationToken = default)
    {
        BrowserThread? thread = _browserThread;
        if (thread is null) return null;

        NativeMethods.SORTCOLUMN? sortColumn = null;
        await thread.InvokeAsync(() =>
        {
            if (_browser is null) return;
            Guid viewId = typeof(NativeMethods.IFolderView2).GUID;
            if (_browser.GetCurrentView(ref viewId, out IntPtr viewPointer) < 0
                || viewPointer == IntPtr.Zero)
                return;

            IntPtr sortPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<NativeMethods.SORTCOLUMN>());
            try
            {
                var view = (NativeMethods.IFolderView2)
                    Marshal.GetObjectForIUnknown(viewPointer);
                if (view.GetSortColumns(sortPointer, 1) >= 0)
                    sortColumn = Marshal.PtrToStructure<NativeMethods.SORTCOLUMN>(
                        sortPointer);
            }
            finally
            {
                Marshal.FreeHGlobal(sortPointer);
                Marshal.Release(viewPointer);
            }
        }, cancellationToken);
        return sortColumn;
    }

    private IReadOnlyList<ExplorerShellColumn> ReadVisibleShellColumns()
    {
        if (_browser is null) return [];

        try
        {
            Guid managerId = typeof(NativeMethods.IColumnManager).GUID;
            if (_browser.GetCurrentView(ref managerId, out IntPtr pointer) < 0
                || pointer == IntPtr.Zero)
                return [];

            try
            {
                var manager = (NativeMethods.IColumnManager)
                    Marshal.GetObjectForIUnknown(pointer);
                if (manager.GetColumnCount(NativeMethods.CM_ENUM_VISIBLE,
                        out uint count) < 0 || count == 0)
                    return [];

                count = Math.Min(count, 1024);
                var keys = new NativeMethods.PROPERTYKEY[count];
                if (manager.GetColumns(NativeMethods.CM_ENUM_VISIBLE,
                        keys, count) < 0)
                    return [];

                var columns = new List<ExplorerShellColumn>(keys.Length);
                foreach (NativeMethods.PROPERTYKEY item in keys)
                {
                    NativeMethods.PROPERTYKEY key = item;
                    var info = new NativeMethods.CM_COLUMNINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.CM_COLUMNINFO>(),
                        dwMask = NativeMethods.CM_MASK_WIDTH
                            | NativeMethods.CM_MASK_NAME,
                        wszName = string.Empty,
                    };
                    if (manager.GetColumnInfo(ref key, ref info) < 0)
                        continue;

                    string canonicalName = string.Empty;
                    if (NativeMethods.PSGetNameFromPropertyKey(ref key,
                            out IntPtr namePointer) >= 0
                        && namePointer != IntPtr.Zero)
                    {
                        try
                        {
                            canonicalName = Marshal.PtrToStringUni(namePointer)
                                ?? string.Empty;
                        }
                        finally
                        {
                            NativeMethods.CoTaskMemFree(namePointer);
                        }
                    }
                    string displayName = string.IsNullOrWhiteSpace(info.wszName)
                        ? canonicalName : info.wszName;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    columns.Add(new ExplorerShellColumn(
                        key, canonicalName, displayName, (int)info.uWidth));
                }
                return columns;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException
            or ExternalException)
        {
            AppLog.Debug(ex, nameof(ReadVisibleShellColumns),
                "Could not read the visible native file columns.");
            return [];
        }
    }

    internal bool CompactViewEnabled => _viewSettings.CompactViewEnabled;
    internal bool ItemCheckBoxesEnabled => _viewSettings.ItemCheckBoxesEnabled;
    internal bool FileExtensionsVisible => _viewSettings.FileExtensionsVisible;
    internal bool HiddenItemsVisible => _viewSettings.HiddenItemsVisible;

    internal void ToggleCompactView() => _viewSettings.ToggleCompactView();

    internal void ToggleItemCheckBoxes()
    {
        _viewSettings.ToggleItemCheckBoxes();
        ApplyItemCheckBoxSettingToNativeView();
    }

    internal void ToggleFileExtensions() => _viewSettings.ToggleFileExtensions();

    internal void ToggleHiddenItems() => _viewSettings.ToggleHiddenItems();

    private void ApplyItemCheckBoxSettingToNativeView()
    {
        if (SwitchToBrowserThread(ApplyItemCheckBoxSettingToNativeView)) return;
        if (_browser == null) return;

        try
        {
            var folderViewId = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            if (_browser.GetCurrentView(ref folderViewId, out IntPtr viewPointer) < 0
                || viewPointer == IntPtr.Zero)
                return;

            try
            {
                var folderView = (NativeMethods.IFolderView2)
                    Marshal.GetObjectForIUnknown(viewPointer);
                folderView.SetCurrentFolderFlags(
                    FWF_CHECKSELECT,
                    ItemCheckBoxesEnabled ? FWF_CHECKSELECT : 0);
            }
            finally
            {
                Marshal.Release(viewPointer);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ApplyItemCheckBoxSettingToNativeView));
        }
    }

    /// <summary>
    /// Returns the file-system path of the first selected item, or the focused
    /// (arrow-keyed) item if nothing is formally selected, or null if the view
    /// is empty or the path cannot be resolved (virtual/network shell items).
    ///
    /// IShellItemArray (from IFolderView2::GetSelection) is NOT used here.
    /// On some Windows 11 builds the RCW caches a negative QI result for
    /// IShellItemArray even when the raw COM pointer supports it, making every
    /// managed cast throw InvalidCastException.  IFolderView2::GetSelectedItem
    /// returns an index instead and is fully reliable.
    /// </summary>
    internal string? GetSelectedItemPath()
    {
        if (_browserThread != null
            && NativeMethods.GetCurrentThreadId() != _browserThread.ThreadId)
            return _browserThread.Invoke(GetSelectedItemPath);
        if (_browser == null) return null;
        try
        {
            var fv2Id = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            if (_browser.GetCurrentView(ref fv2Id, out IntPtr ppv) < 0 || ppv == IntPtr.Zero) return null;
            try
            {
                var fv2  = (NativeMethods.IFolderView2)Marshal.GetObjectForIUnknown(ppv);
                var siId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"); // IShellItem

                // Primary: click-selected item (selection mark).
                // GetSelectedItem(0) returns the index of the selection-mark item (-1 = none).
                if (fv2.GetSelectedItem(0, out int selIdx) >= 0 && selIdx >= 0
                    && fv2.GetItem(selIdx, ref siId, out IntPtr ppvSel) >= 0 && ppvSel != IntPtr.Zero)
                {
                    try
                    {
                        var item = (NativeMethods.IShellItem)Marshal.GetObjectForIUnknown(ppvSel);
                        item.GetDisplayName(0x80058000 /*SIGDN_FILESYSPATH*/, out string p);
                        if (!string.IsNullOrEmpty(p)) return p;
                    }
                    finally { Marshal.Release(ppvSel); }
                }

                // Fallback: keyboard-focused item (arrow-key navigation without a click).
                if (fv2.GetFocusedItem(out int focusIdx) >= 0 && focusIdx >= 0)
                {
                    if (fv2.GetItem(focusIdx, ref siId, out IntPtr ppvItem) >= 0 && ppvItem != IntPtr.Zero)
                    {
                        try
                        {
                            var item = (NativeMethods.IShellItem)Marshal.GetObjectForIUnknown(ppvItem);
                            item.GetDisplayName(0x80058000, out string path);
                            if (!string.IsNullOrEmpty(path)) return path;
                        }
                        finally { Marshal.Release(ppvItem); }
                    }
                }

                return null;
            }
            finally { Marshal.Release(ppv); }
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(GetSelectedItemPath)); return null; }
    }

    /// <summary>Refreshes the current shell view (used after system settings changes).</summary>
    internal void RefreshShellView()
    {
        if (IsNetworkPath(_currentPath))
        {
            if (_networkPathStatus != NetworkPathStatus.Available)
            {
                BeginNetworkProbe(_currentPath);
                return;
            }

            // A successful delete on a share may not produce a Shell change
            // notification. Refresh the existing view instead of browsing to the
            // same path, which can leave its old items on screen. Post this work
            // so a slow network refresh cannot hold up the WinForms UI thread.
            if (_browserThread is { } thread
                && NativeMethods.GetCurrentThreadId() != thread.ThreadId)
            {
                string path = _currentPath;
                thread.Post(() =>
                {
                    if (!IsDisposed && !Disposing
                        && _networkPathStatus == NetworkPathStatus.Available
                        && PathsReferToSameFolder(_currentPath, path))
                        RefreshShellView();
                });
                return;
            }
        }
        if (SwitchToBrowserThread(RefreshShellView)) return;
        if (_browser == null) return;
        try
        {
            var svId = new Guid("000214E3-0000-0000-C000-000000000046");
            if (_browser.GetCurrentView(ref svId, out IntPtr ppv) < 0 || ppv == IntPtr.Zero) return;
            try { ((NativeMethods.IShellView)Marshal.GetObjectForIUnknown(ppv)).Refresh(); }
            finally { Marshal.Release(ppv); }
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(RefreshShellView)); }
    }

    /// <summary>
    /// Enables filesystem change monitoring while this tab is active. Inactive
    /// tabs retain their last count and refresh when they are selected again.
    /// </summary>
    internal void SetItemCountMonitoringActive(bool active)
    {
        if (_itemCountMonitoringActive == active)
        {
            if (active)
                ConfigureItemCountWatcher(GetCurrentPath());
            return;
        }

        _itemCountMonitoringActive = active;
        _itemCountDebounceTimer.Stop();
        if (!active)
        {
            DisposeItemCountWatcher();
            return;
        }

        ConfigureItemCountWatcher(GetCurrentPath());
        RefreshItemCountStatus(force: true);
    }

    /// <summary>Rebinds monitoring and recounts after the active tab navigates.</summary>
    internal void NotifyItemCountPathChanged()
    {
        if (!_itemCountMonitoringActive) return;

        _itemCountDebounceTimer.Stop();
        ConfigureItemCountWatcher(GetCurrentPath());
        RefreshItemCountStatus(force: true);
    }

    /// <summary>Debounces an application-initiated folder refresh with watcher events.</summary>
    internal void ScheduleItemCountRefresh()
    {
        if (!_itemCountMonitoringActive || IsNetworkPath(GetCurrentPath())) return;

        _itemCountDebounceTimer.Stop();
        _itemCountDebounceTimer.Start();
    }

    /// <summary>
    /// Requests separate file and folder totals. Requests are coalesced and a
    /// single worker performs enumeration, preventing overlapping scans when a
    /// burst of filesystem or Shell notifications arrives.
    /// </summary>
    internal void RefreshItemCountStatus(bool force = false)
    {
        if (IsDisposed || Disposing || !_itemCountMonitoringActive) return;

        long now = Environment.TickCount64;
        long nextRefresh = Volatile.Read(ref _nextItemCountRefreshTick);
        if (!force && now < nextRefresh) return;

        Volatile.Write(ref _nextItemCountRefreshTick,
            now + ItemCountFallbackRefreshIntervalMilliseconds);

        string path = GetCurrentPath();
        if (IsNetworkPath(path)) return;

        bool showHiddenItems = HiddenItemsVisible;
        bool showProtectedSystemItems = AreProtectedSystemItemsVisible();
        int generation = Interlocked.Increment(ref _itemCountRequestGeneration);
        var request = new ItemCountRefreshRequest(
            path, showHiddenItems, showProtectedSystemItems, generation);

        bool startWorker = false;
        lock (_itemCountRefreshLock)
        {
            _pendingItemCountRefresh = request;
            if (!_itemCountWorkerRunning)
            {
                _itemCountWorkerRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
            _ = ProcessItemCountRefreshRequestsAsync();
    }

    private async Task ProcessItemCountRefreshRequestsAsync()
    {
        CancellationToken cancellationToken = _itemCountWorkerCancellation.Token;
        bool restartWorker = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ItemCountRefreshRequest request;
                lock (_itemCountRefreshLock)
                {
                    if (_pendingItemCountRefresh is not { } pending)
                        return;

                    request = pending;
                    _pendingItemCountRefresh = null;
                }

                DirectoryItemCount? count;
                try
                {
                    DirectorySnapshot snapshot = await DirectorySnapshotCache.GetAsync(
                        request.Path, cancellationToken).ConfigureAwait(false);
                    count = snapshot.CountItems(
                        request.ShowHiddenItems,
                        request.ShowProtectedSystemItems);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (
                    ex is DirectoryNotFoundException or
                    IOException or
                    UnauthorizedAccessException)
                {
                    AppLog.Debug(ex, nameof(ProcessItemCountRefreshRequestsAsync),
                        $"Could not count the contents of '{request.Path}'.");
                    continue;
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, nameof(ProcessItemCountRefreshRequestsAsync),
                        $"Could not update the item count for '{request.Path}'.");
                    continue;
                }

                if (count is null
                    || cancellationToken.IsCancellationRequested
                    || Volatile.Read(ref _itemCountRequestGeneration) != request.Generation
                    || !PathsReferToSameFolder(GetCurrentPath(), request.Path))
                    continue;

                PostToUi(() =>
                {
                    if (!cancellationToken.IsCancellationRequested
                        && _itemCountMonitoringActive
                        && Volatile.Read(ref _itemCountRequestGeneration) == request.Generation
                        && PathsReferToSameFolder(GetCurrentPath(), request.Path))
                        ItemCountChanged?.Invoke(this, count.Value);
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            lock (_itemCountRefreshLock)
            {
                _itemCountWorkerRunning = false;
                if (_pendingItemCountRefresh is not null
                    && !cancellationToken.IsCancellationRequested)
                {
                    _itemCountWorkerRunning = true;
                    restartWorker = true;
                }
            }

            if (restartWorker)
                _ = ProcessItemCountRefreshRequestsAsync();
        }
    }

    private void ConfigureItemCountWatcher(string path)
    {
        if (!_itemCountMonitoringActive || IsNetworkPath(path))
        {
            DisposeItemCountWatcher();
            return;
        }

        if (_itemCountWatchSubscription is not null
            && string.Equals(_itemCountWatcherPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        DisposeItemCountWatcher();
        if (!Directory.Exists(path)) return;

        try
        {
            _itemCountWatchSubscription = DirectorySnapshotCache.Watch(
                path, OnItemCountSnapshotChanged);
            _itemCountWatcherPath = path;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            DirectoryNotFoundException or
            IOException or
            UnauthorizedAccessException)
        {
            AppLog.Debug(ex, nameof(ConfigureItemCountWatcher),
                $"Could not monitor '{path}' for item-count changes.");
        }
    }

    private void DisposeItemCountWatcher()
    {
        IDisposable? subscription = _itemCountWatchSubscription;
        _itemCountWatchSubscription = null;
        _itemCountWatcherPath = string.Empty;
        subscription?.Dispose();
    }

    private void OnItemCountSnapshotChanged() =>
        QueueItemCountSnapshotUpdate();

    private void QueueItemCountSnapshotUpdate()
    {
        if (Interlocked.Exchange(ref _itemCountWatcherUiUpdatePending, 1) != 0)
            return;

        PostToUi(() =>
        {
            Interlocked.Exchange(ref _itemCountWatcherUiUpdatePending, 0);
            if (!_itemCountMonitoringActive) return;

            _itemCountDebounceTimer.Stop();
            _itemCountDebounceTimer.Start();
        });
    }

    private void OnItemCountDebounceTick(object? sender, EventArgs e)
    {
        _itemCountDebounceTimer.Stop();
        if (!_itemCountMonitoringActive) return;

        ConfigureItemCountWatcher(GetCurrentPath());
        RefreshItemCountStatus(force: true);
        // External deletion or rename can also clear/change the Shell selection.
        RequestSelectionSnapshotRefresh();
    }

    internal static DirectoryItemCount? CountDirectoryItems(
        string path,
        bool showHiddenItems,
        bool showProtectedSystemItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IsNetworkPath(path)) return null;
        if (!Directory.Exists(path)) return null;

        DirectorySnapshot snapshot = DirectorySnapshotCache.LoadSnapshot(
            path, cancellationToken);
        return snapshot.CountItems(showHiddenItems, showProtectedSystemItems);
    }

    internal static bool IsNetworkPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;

        string drivePath = path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path[4..]
            : path;
        if (drivePath.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            string? root = Path.GetPathRoot(drivePath);
            return !string.IsNullOrEmpty(root)
                && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            UnauthorizedAccessException)
        {
            AppLog.Debug(ex, nameof(IsNetworkPath),
                $"Could not determine the drive type for '{path}'.");
            return false;
        }
    }

    internal static string FormatItemCountStatus(int fileCount, int folderCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(folderCount);

        return $"{fileCount} {(fileCount == 1 ? "file" : "files")}, " +
               $"{folderCount} {(folderCount == 1 ? "folder" : "folders")}";
    }

    /// <summary>
    /// Opens the context menu for the current view's blank background. This uses
    /// IShellView::GetItemObject(SVGIO_BACKGROUND), rather than the folder's own
    /// context menu, so it exposes the Explorer commands and populated New cascade.
    /// </summary>
    internal void ShowBackgroundContextMenu(Point screenLocation)
    {
        if (SwitchToBrowserThread(() => ShowBackgroundContextMenu(screenLocation)))
            return;
        if (_browser is null || _browserThread is null) return;

        var shellViewId = typeof(NativeMethods.IShellView).GUID;
        int getViewResult = _browser.GetCurrentView(ref shellViewId, out IntPtr shellViewPointer);
        if (getViewResult < 0
            || shellViewPointer == IntPtr.Zero)
        {
            AppLog.Debug(nameof(ShowBackgroundContextMenu),
                $"IExplorerBrowser.GetCurrentView(IShellView) returned 0x{getViewResult:X8}.");
            return;
        }

        try
        {
            var shellView = (NativeMethods.IShellView)Marshal.GetObjectForIUnknown(shellViewPointer);
            int getWindowResult = shellView.GetWindow(out IntPtr shellViewWindow);
            if (getWindowResult < 0 || shellViewWindow == IntPtr.Zero)
                shellViewWindow = _browserThread.Handle;

            var contextMenuId = typeof(NativeMethods.IContextMenu).GUID;
            int getItemObjectResult = shellView.GetItemObject(0, ref contextMenuId,
                out IntPtr contextMenuPointer);
            if (getItemObjectResult < 0
                || contextMenuPointer == IntPtr.Zero)
            {
                AppLog.Debug(nameof(ShowBackgroundContextMenu),
                    $"IShellView.GetItemObject(SVGIO_BACKGROUND, IContextMenu) returned " +
                    $"0x{getItemObjectResult:X8}.");
                return;
            }

            try
            {
                var contextMenu = (NativeMethods.IContextMenu)Marshal.GetObjectForIUnknown(
                    contextMenuPointer);
                IReadOnlyDictionary<int, ShellNewMenu.ShellNewItem> shellNewCommands =
                    new Dictionary<int, ShellNewMenu.ShellNewItem>();
                ShellContextMenu.Show(contextMenu, shellViewWindow, screenLocation,
                    customizeMenu: menu => shellNewCommands = ShellNewMenu.Populate(menu),
                    executeCustomCommand: commandId =>
                    {
                        bool handled = ShellNewMenu.TryExecute(commandId,
                            shellNewCommands, _currentPath, out string? createdPath);
                        if (createdPath is not null)
                            BeginRenameCreatedItem(createdPath);
                        return handled;
                    });
            }
            finally
            {
                Marshal.Release(contextMenuPointer);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ShowBackgroundContextMenu));
        }
        finally
        {
            Marshal.Release(shellViewPointer);
        }
    }

    /// <summary>
    /// Executes a command through the selected-item context supplied by the live
    /// embedded Shell view. Some stateful Windows commands reject a context menu
    /// reconstructed from the parent folder even though the menu item is visible.
    /// </summary>
    internal bool TryInvokeSelectedShellCommand(string path,
        string canonicalVerb, Point screenLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalVerb);

        if (_browserThread != null
            && NativeMethods.GetCurrentThreadId() != _browserThread.ThreadId)
        {
            return _browserThread.Invoke(() =>
            {
                SelectItem(path);
                return TryInvokeSelectedShellCommand(
                    path, canonicalVerb, screenLocation);
            });
        }

        if (_browser is null
            || !PathsReferToSameFolder(Path.GetDirectoryName(path), _currentPath))
            return false;

        string[] selectedPaths = GetSelectedFileSystemPaths();
        if (selectedPaths.Length != 1
            || !PathsReferToSameFolder(selectedPaths[0], path))
            return false;

        return TryInvokeShellSelectionCommand(canonicalVerb, screenLocation);
    }

    private bool TryInvokeShellSelectionCommand(string canonicalVerb,
        Point screenLocation)
    {
        if (_browser is null) return false;

        IntPtr shellViewPointer = IntPtr.Zero;
        IntPtr contextMenuPointer = IntPtr.Zero;
        NativeMethods.IShellView? shellView = null;
        NativeMethods.IContextMenu? contextMenu = null;
        try
        {
            Guid shellViewId = typeof(NativeMethods.IShellView).GUID;
            if (_browser.GetCurrentView(ref shellViewId,
                    out shellViewPointer) < 0
                || shellViewPointer == IntPtr.Zero)
                return false;

            shellView = (NativeMethods.IShellView)
                Marshal.GetObjectForIUnknown(shellViewPointer);
            int getWindowResult = shellView.GetWindow(out IntPtr shellViewWindow);
            if (getWindowResult < 0 || shellViewWindow == IntPtr.Zero)
                shellViewWindow = _browserThread?.Handle ?? Handle;

            Guid contextMenuId = typeof(NativeMethods.IContextMenu).GUID;
            const uint SvgioSelection = 1;
            if (shellView.GetItemObject(SvgioSelection, ref contextMenuId,
                    out contextMenuPointer) < 0
                || contextMenuPointer == IntPtr.Zero)
                return false;

            contextMenu = (NativeMethods.IContextMenu)
                Marshal.GetObjectForIUnknown(contextMenuPointer);
            return ShellContextMenu.TryInvokeCanonicalShellCommand(
                       contextMenu, shellViewWindow, canonicalVerb, screenLocation)
                   || (canonicalVerb.Equals("properties", StringComparison.OrdinalIgnoreCase)
                       && ShellContextMenu.TryInvokeNamedShellCommand(
                           contextMenu, shellViewWindow, "properties"));
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(TryInvokeShellSelectionCommand),
                $"The live Shell view could not run '{canonicalVerb}'.");
            return false;
        }
        finally
        {
            if (contextMenu is not null)
                Marshal.ReleaseComObject(contextMenu);
            if (contextMenuPointer != IntPtr.Zero)
                Marshal.Release(contextMenuPointer);
            if (shellView is not null)
                Marshal.ReleaseComObject(shellView);
            if (shellViewPointer != IntPtr.Zero)
                Marshal.Release(shellViewPointer);
        }
    }

    // New tabs start in the application's compact layout. The remaining display
    // preferences come from Windows Explorer, then all values remain independent
    // in-memory settings for the tab's lifetime.
    private static bool ReadHiddenItemsDefault()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue("Hidden") is int v && v == 1;
    }

    private static bool AreProtectedSystemItemsVisible()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue("ShowSuperHidden") is int value && value == 1;
    }

    private static bool ReadFileExtensionsDefault()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue("HideFileExt") is int v && v == 0;
    }

    private static bool ReadItemCheckBoxesDefault()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue("AutoCheckSelect") is int value && value == 1;
    }

    // Sends the file to an already-running QuickLook instance. MultiExplorer does
    // not silently start a separate third-party application; the UI explains how
    // to start or install it when the pipe is unavailable.
    internal static QuickLookFailure? InvokeQuickLook(string filePath)
    {
        // Grant QuickLook permission to bring its window to the foreground.
        // Windows blocks SetForegroundWindow from background processes; only the
        // current foreground process can delegate that right via AllowSetForegroundWindow.
        int qlPid = GetQuickLookPid();
        if (qlPid <= 0)
            return FindQuickLookExe() == null
                ? QuickLookFailure.NotInstalled
                : QuickLookFailure.NotRunning;

        NativeMethods.AllowSetForegroundWindow(qlPid);
        return TrySendViaQuickLookPipe(filePath) ? null : QuickLookFailure.Unavailable;
    }

    private static int GetQuickLookPid()
    {
        foreach (var p in Process.GetProcessesByName("QuickLook"))
        {
            try   { return p.Id; }
            finally { p.Dispose(); }
        }
        return 0;
    }

    // QuickLook's pipe name uses the current user's SID (not the session ID).
    // Confirmed format: QuickLook.App.Pipe.<WindowsIdentity SID>
    //
    // Race condition: QuickLook rotates to a new NamedPipeServerStream after each
    // handled request.  If Space is pressed while the previous instance is closing,
    // pipe.Connect() succeeds (we get the tail of the closing instance) but
    // pipe.Write() throws IOException "Pipe is broken".  One retry is enough because
    // the server creates a fresh instance the moment the old one closes.
    private static bool TrySendViaQuickLookPipe(string filePath)
    {
        string sid      = WindowsIdentity.GetCurrent().User!.Value;
        string pipeName = $"QuickLook.App.Pipe.{sid}";

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                pipe.Connect(1000);
                // QuickLook v4.x pipe protocol: "PipeMessages.Toggle|path|options\n"
                byte[] data = Encoding.UTF8.GetBytes($"QuickLook.App.PipeMessages.Toggle|{filePath}|\n");
                pipe.Write(data, 0, data.Length);
                return true;
            }
            catch (IOException ex) when (attempt == 1)
            {
                // Broken-pipe race: QuickLook rotates its pipe server after each request.
                // The fresh instance is ready immediately — retry once.
                AppLog.Debug(ex, nameof(TrySendViaQuickLookPipe),
                    "QuickLook pipe closed during the request; retrying once.");
            }
            catch (Exception ex) when (
                ex is TimeoutException or
                IOException or
                UnauthorizedAccessException)
            {
                // TimeoutException   → QuickLook isn't running → fall through to process launch
                // UnauthorizedAccess → privilege mismatch (elevated vs non-elevated)
                AppLog.Debug(ex, nameof(TrySendViaQuickLookPipe),
                    "QuickLook pipe was unavailable; falling back to process launch.");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, nameof(TrySendViaQuickLookPipe),
                    "Unexpected failure communicating with QuickLook.");
                return false;
            }
        }
        return false;
    }

    private static string? FindQuickLookExe()
    {
        // 1. Running instance — most reliable, covers non-standard install paths.
        foreach (var p in Process.GetProcessesByName("QuickLook"))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception or
                InvalidOperationException or
                NotSupportedException)
            {
                AppLog.Debug(ex, nameof(FindQuickLookExe),
                    "Could not inspect a running QuickLook process; continuing discovery.");
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, nameof(FindQuickLookExe),
                    "Unexpected failure while inspecting a running QuickLook process.");
            }
            finally { p.Dispose(); }
        }

        // 2. Standard per-user install locations.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string c in new[]
        {
            Path.Combine(local, @"Programs\QuickLook\QuickLook.exe"),    // NSIS installer
            Path.Combine(local, @"Microsoft\WindowsApps\QuickLook.exe"), // Windows Store
        })
        {
            if (File.Exists(c)) return c;
        }

        // 3. NSIS uninstall registry key (covers custom install directories).
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuickLook");
            string? dir = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.Combine(dir, "QuickLook.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
            System.Security.SecurityException or
            IOException)
        {
            AppLog.Debug(ex, nameof(FindQuickLookExe),
                "Could not inspect the QuickLook registry entry; continuing discovery.");
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(FindQuickLookExe),
                "Unexpected failure while reading the QuickLook registry entry.");
        }

        // 4. PATH variable — covers Scoop/Chocolatey installs.
        foreach (string segment in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                string candidate = Path.Combine(segment.Trim(), "QuickLook.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException)
            {
                AppLog.Debug(ex, nameof(FindQuickLookExe),
                    $"Skipping malformed PATH entry \"{segment}\".");
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, nameof(FindQuickLookExe),
                    $"Unexpected failure inspecting PATH entry \"{segment}\".");
            }
        }

        return null;
    }

    private NativeMethods.FOLDERSETTINGS CreateHeaderEnabledFolderSettings(uint viewMode)
    {
        return new NativeMethods.FOLDERSETTINGS
        {
            ViewMode = viewMode,
            fFlags = ItemCheckBoxesEnabled ? FWF_CHECKSELECT : 0,
        };
    }

    private void ApplyFolderSettings(uint viewMode)
    {
        if (_browser == null) return;

        var settings = CreateHeaderEnabledFolderSettings(viewMode);
        int hr = _browser.SetFolderSettings(ref settings);
    }

    /// <summary>Selects all items in the current shell view.</summary>
    public void SelectAll() => SendAccelWithCtrl(0x41); // Ctrl+A (VK_A)

    /// <summary>Opens the focused selection using the Shell view's default command.</summary>
    public void OpenSelection()
    {
        if (SwitchToBrowserThread(OpenSelection)) return;
        var message = Message.Create(Handle, 0x0100 /* WM_KEYDOWN */,
            (IntPtr)0x0D /* VK_RETURN */, (IntPtr)1);
        TryShellTranslateAccelerator(ref message);
    }

    /// <summary>Shows properties for the selected shell item(s).</summary>
    public void ShowProperties()
    {
        if (SwitchToBrowserThread(ShowProperties)) return;
        // Use the live Shell selection so Properties also handles multiple items
        // and works regardless of which child window currently has focus.
        if (TryInvokeShellSelectionCommand("properties", Cursor.Position)) return;
        if (!OleExec(10 /* OLECMDID_PROPERTIES */))
            SendAccelWithAlt(0x0D); // Alt+Enter (VK_RETURN)
    }

    // ── Direct shell commands (bypass SendKeys / focus issues) ──────────────────

    // All four commands use the same path as a real keypress: TryShellTranslateAccelerator
    // calls IInputObject::TranslateAcceleratorIO, which is exactly what PreFilterMessage
    // calls when the user presses the key manually.
    //
    // For Ctrl+key commands the accelerator table match requires GetKeyState(VK_CONTROL)
    // to return "pressed".  SetKeyboardState writes to the per-thread key-state table
    // that GetKeyState reads, so we can set Ctrl pressed, fire the accelerator, then
    // restore — all synchronously, no message-queue round-trip needed.

    public void Cut()    => SendAccelWithCtrl(0x58); // Ctrl+X  (VK_X)
    public void Copy()   => SendAccelWithCtrl(0x43); // Ctrl+C  (VK_C)

    /// <summary>
    /// Starts a clipboard file copy/move on its own STA thread. Shell file
    /// operations run a modal message loop, so executing one on the WinForms
    /// thread would prevent the user from starting another operation or using
    /// any other part of MultiExplorer until it completed.
    /// </summary>
    public void Paste()
    {
        string destination = GetCurrentPath();
        if (!Directory.Exists(destination)) return;

        try
        {
            IDataObject? data = Clipboard.GetDataObject();
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
            {
                // Preserve support for non-file-system shell clipboard formats.
                // These are uncommon and cannot be represented by SHFileOperation.
                SendAccelWithCtrl(0x56); // Ctrl+V (VK_V)
                return;
            }

            // Clipboard.GetFileDropList normalises the native CF_HDROP payload to
            // a StringCollection. IDataObject.GetData may instead return a
            // string[], depending on which application populated the clipboard.
            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (paths.Length == 0) return;

            bool move = ClipboardRequestsMove(data);
            OperationManager.Current?.Start(
                move ? FileOperationKind.Move : FileOperationKind.Copy,
                paths, destination);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(Paste), "Could not start the clipboard file operation.");
        }
    }

    private static bool ClipboardRequestsMove(IDataObject data)
    {
        const string preferredDropEffect = "Preferred DropEffect";
        if (!data.GetDataPresent(preferredDropEffect)) return false;

        object? value = data.GetData(preferredDropEffect);
        byte[]? bytes = value switch
        {
            MemoryStream stream => stream.ToArray(),
            byte[] array         => array,
            _                    => null,
        };

        // DROPEFFECT_MOVE is bit 1. A regular Copy places DROPEFFECT_COPY (bit 0).
        return bytes is { Length: >= 4 }
            && (BitConverter.ToUInt32(bytes, 0) & 0x00000002u) != 0;
    }

    private void InstallAsynchronousFileDropTarget()
    {
        // Windows 11's ExplorerBrowser file list is DirectUIHWND beneath
        // SHELLDLL_DefView (not UIItemsView). Register on that deepest window so
        // OLE hit-testing selects us ahead of the DefView's synchronous target.
        // Older Shell versions use SysListView32 directly.
        IntPtr defView = FindDescendant(Handle, "SHELLDLL_DefView");
        IntPtr listWindow = defView == IntPtr.Zero
            ? IntPtr.Zero
            : FindDescendant(defView, "DirectUIHWND");
        if (listWindow == IntPtr.Zero)
            listWindow = FindDescendant(Handle, "SysListView32");
        if (listWindow == IntPtr.Zero || listWindow == _fileDropTargetWindow) return;

        RemoveAsynchronousFileDropTarget();

        var target = new ShellFileDropTarget(
            () => _currentPath,
            (paths, destination, move) =>
                OperationManager.Current?.Start(
                    move ? FileOperationKind.Move : FileOperationKind.Copy,
                    paths, destination) != null);

        // ExplorerBrowser registers a synchronous Shell target on its item view.
        // Replace it with a target that merely captures CF_HDROP and schedules the
        // operation, allowing IDropTarget.Drop (and therefore DoDragDrop) to return.
        NativeMethods.RevokeDragDrop(listWindow);
        int hr = NativeMethods.RegisterDragDrop(listWindow, target);
        if (hr >= 0)
        {
            _fileDropTarget = target; // keep the CCW alive while OLE holds it
            _fileDropTargetWindow = listWindow;
        }
        else
        {
            AppLog.Warn(null, nameof(InstallAsynchronousFileDropTarget),
                $"RegisterDragDrop failed with HRESULT 0x{hr:X8}.");
        }
    }

    private void RemoveAsynchronousFileDropTarget()
    {
        if (_fileDropTargetWindow != IntPtr.Zero)
            NativeMethods.RevokeDragDrop(_fileDropTargetWindow);
        _fileDropTargetWindow = IntPtr.Zero;
        _fileDropTarget = null;
    }

    /// <summary>
    /// Copies every selected file-system path to the clipboard, one per line.
    /// Virtual shell items without a file-system path are skipped.
    /// </summary>
    public void CopySelectedPaths()
    {
        if (SwitchToBrowserThread(CopySelectedPaths)) return;
        try
        {
            string[] paths = GetSelectedFileSystemPaths();
            if (paths.Length > 0)
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(CopySelectedPaths),
                "Could not copy the selected item paths to the clipboard.");
        }
    }

    private string[] GetSelectedFileSystemPaths()
    {
        if (_browserThread != null
            && NativeMethods.GetCurrentThreadId() != _browserThread.ThreadId)
            return _browserThread.Invoke(GetSelectedFileSystemPaths);
        if (_browser == null) return [];

        var paths = new List<string>();
        var fv2Id = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
        if (_browser.GetCurrentView(ref fv2Id, out IntPtr ppv) < 0 || ppv == IntPtr.Zero)
            return [];
        try
        {
            var fv2 = (NativeMethods.IFolderView2)Marshal.GetObjectForIUnknown(ppv);
            if (fv2.GetSelection(0, out IntPtr selection) < 0 || selection == IntPtr.Zero)
                return [];
            try
            {
                if (NativeMethods.ShellItemArrayGetCount(selection, out uint count) < 0)
                    return [];
                for (uint index = 0; index < count; index++)
                {
                    if (NativeMethods.ShellItemArrayGetItemAt(
                            selection, index, out IntPtr itemPointer) < 0
                        || itemPointer == IntPtr.Zero)
                        continue;
                    try
                    {
                        var item = (NativeMethods.IShellItem)Marshal.GetObjectForIUnknown(itemPointer);
                        if (item.GetDisplayName(0x80058000, out string path) >= 0
                            && !string.IsNullOrEmpty(path))
                            paths.Add(path);
                    }
                    finally { Marshal.Release(itemPointer); }
                }
            }
            finally { Marshal.Release(selection); }
        }
        finally { Marshal.Release(ppv); }
        return paths.ToArray();
    }

    public void Delete(bool permanently = false)
    {
        if (SwitchToBrowserThread(() => Delete(permanently))) return;
        try
        {
            string[] paths = GetSelectedFileSystemPaths();
            if (paths.Length == 0) return;

            // The selection belongs to the browser STA, but confirmation and
            // operation tracking belong to the WinForms UI thread. In particular,
            // showing a dialog owned by MainForm from this STA can reenter the
            // browser through focus activation while a Shell message is in flight.
            PostToUi(() =>
            {
                if (IsDisposed || Disposing) return;
                try
                {
                    OperationManager.Current?.Start(
                        permanently ? FileOperationKind.DeletePermanently : FileOperationKind.Delete,
                        paths);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, nameof(Delete), "Could not start the delete operation.");
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(Delete), "Could not start the delete operation.");
        }
    }

    // Rename: pass a synthetic VK_F2 WM_KEYDOWN through IInputObject::TranslateAcceleratorIO.
    public void Rename()
    {
        if (SwitchToBrowserThread(Rename)) return;
        IntPtr target = FindListView();
        var m = Message.Create(target != IntPtr.Zero ? target : Handle,
            0x0100 /*WM_KEYDOWN*/, (IntPtr)0x71 /*VK_F2*/, (IntPtr)1);
        TryShellTranslateAccelerator(ref m);
    }

    private void SendAccelWithCtrl(int vk)
    {
        if (SwitchToBrowserThread(() => SendAccelWithCtrl(vk))) return;
        byte[] saved = new byte[256];
        NativeMethods.GetKeyboardState(saved);

        byte[] withCtrl = (byte[])saved.Clone();
        withCtrl[0x11] |= 0x80; // VK_CONTROL — set high bit = key down
        NativeMethods.SetKeyboardState(withCtrl);
        try
        {
            var m = Message.Create(Handle, 0x0100 /*WM_KEYDOWN*/, (IntPtr)vk, (IntPtr)1);
            TryShellTranslateAccelerator(ref m);
        }
        finally
        {
            NativeMethods.SetKeyboardState(saved);
        }
    }

    private void SendAccelWithAlt(int vk)
    {
        if (SwitchToBrowserThread(() => SendAccelWithAlt(vk))) return;
        byte[] saved = new byte[256];
        NativeMethods.GetKeyboardState(saved);

        byte[] withAlt = (byte[])saved.Clone();
        withAlt[0x12] |= 0x80; // VK_MENU (Alt) - set high bit = key down
        NativeMethods.SetKeyboardState(withAlt);
        try
        {
            // Bit 29 marks a system-key message whose Alt context is active.
            var m = Message.Create(
                Handle, 0x0104 /* WM_SYSKEYDOWN */, (IntPtr)vk, (IntPtr)0x20000001);
            TryShellTranslateAccelerator(ref m);
        }
        finally
        {
            NativeMethods.SetKeyboardState(saved);
        }
    }

    /// <summary>Sends a keystroke to the shell view via SendKeys (used for Properties only).</summary>
    public void ExecuteShellCommand(string sendKeysStr)
    {
        BeginInvoke(() =>
        {
            FocusShellView();
            SendKeys.SendWait(sendKeysStr);
        });
    }

    // Executes a standard OLE command on the current shell view via IOleCommandTarget.
    private bool OleExec(uint cmdId)
    {
        if (_browser == null) return false;
        try
        {
            var svId = new Guid("000214E3-0000-0000-C000-000000000046"); // IShellView
            if (_browser.GetCurrentView(ref svId, out IntPtr ppv) < 0 || ppv == IntPtr.Zero)
                return false;
            try
            {
                var obj = Marshal.GetObjectForIUnknown(ppv);
                if (obj is NativeMethods.IOleCommandTarget oct)
                    return oct.Exec(IntPtr.Zero, cmdId, 0, IntPtr.Zero, IntPtr.Zero) >= 0;
            }
            finally { Marshal.Release(ppv); }
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(OleExec)); }
        return false;
    }

    // Walks the window tree under 'root' depth-first to find the first window
    // whose class matches className.  Returns IntPtr.Zero if not found.
    private static IntPtr FindDescendant(IntPtr root, string className)
    {
        IntPtr child = NativeMethods.GetWindow(root, 5); // GW_CHILD
        while (child != IntPtr.Zero)
        {
            var sb = new StringBuilder(64);
            NativeMethods.GetClassName(child, sb, sb.Capacity);
            if (sb.ToString().Equals(className, StringComparison.OrdinalIgnoreCase))
                return child;
            IntPtr found = FindDescendant(child, className);
            if (found != IntPtr.Zero) return found;
            child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
        }
        return IntPtr.Zero;
    }

    private bool IsInNavigationTree(IntPtr hwnd)
    {
        IntPtr tree = _navigationTreeContextMenuHook?.TreeHandle ?? IntPtr.Zero;
        if (tree == IntPtr.Zero)
            tree = FindDescendant(Handle, "SysTreeView32");
        return tree != IntPtr.Zero
            && (hwnd == tree || NativeMethods.IsChild(tree, hwnd));
    }

    internal static Point GetContextMenuScreenLocation(
        IntPtr messageParameter, Point cursorPosition)
    {
        long packedPoint = messageParameter.ToInt64();
        if (packedPoint == -1) return cursorPosition;

        return new Point(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
    }

    private string? GetSelectedNavigationPath()
    {
        if (_browser == null) return null;

        NativeMethods.INameSpaceTreeControl? tree = null;
        IntPtr selectedItems = IntPtr.Zero;
        IntPtr selectedItem = IntPtr.Zero;
        try
        {
            tree = TryGetNameSpaceTreeControl(
                _browser as NativeMethods.IComServiceProvider);
            if (tree == null
                || tree.GetSelectedItems(out selectedItems) < 0
                || selectedItems == IntPtr.Zero
                || NativeMethods.ShellItemArrayGetCount(selectedItems, out uint count) < 0
                || count == 0
                || NativeMethods.ShellItemArrayGetItemAt(
                    selectedItems, 0, out selectedItem) < 0
                || selectedItem == IntPtr.Zero)
            {
                return GetSelectedNavigationPathFromTreeText();
            }

            var shellItem = (NativeMethods.IShellItem)
                Marshal.GetObjectForIUnknown(selectedItem);
            return shellItem.GetDisplayName(
                       0x80058000 /* SIGDN_FILESYSPATH */, out string path) >= 0
                   && !string.IsNullOrWhiteSpace(path)
                ? path
                : GetSelectedNavigationPathFromTreeText();
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(GetSelectedNavigationPath));
            return GetSelectedNavigationPathFromTreeText();
        }
        finally
        {
            if (selectedItem != IntPtr.Zero) Marshal.Release(selectedItem);
            if (selectedItems != IntPtr.Zero) Marshal.Release(selectedItems);
            if (tree != null) Marshal.ReleaseComObject(tree);
        }
    }

    private string? GetNavigationPathAtScreenPoint(Point screenLocation)
    {
        if (_browser == null) return null;

        NativeMethods.INameSpaceTreeControl? tree = null;
        IntPtr shellItemPointer = IntPtr.Zero;
        try
        {
            tree = TryGetNameSpaceTreeControl(
                _browser as NativeMethods.IComServiceProvider);
            if (tree == null) return null;

            IntPtr namespaceTreeWindow = FindDescendant(
                Handle, "NamespaceTreeControl");
            if (namespaceTreeWindow == IntPtr.Zero) return null;

            var point = new NativeMethods.POINT(
                screenLocation.X, screenLocation.Y);
            if (!NativeMethods.ScreenToClient(namespaceTreeWindow, ref point))
                return null;

            if (tree.HitTest(ref point, out shellItemPointer) < 0
                || shellItemPointer == IntPtr.Zero)
                return null;

            var shellItem = (NativeMethods.IShellItem)
                Marshal.GetObjectForIUnknown(shellItemPointer);
            return shellItem.GetDisplayName(
                       0x80058000 /* SIGDN_FILESYSPATH */, out string path) >= 0
                   && !string.IsNullOrWhiteSpace(path)
                ? path
                : null;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(GetNavigationPathAtScreenPoint));
            return null;
        }
        finally
        {
            if (shellItemPointer != IntPtr.Zero) Marshal.Release(shellItemPointer);
            if (tree != null) Marshal.ReleaseComObject(tree);
        }
    }

    internal static string? SelectNavigationContextPath(
        string? hitPath, string? selectedPath, bool allowSelectedFallback)
    {
        if (!string.IsNullOrWhiteSpace(hitPath)) return hitPath;

        return allowSelectedFallback && !string.IsNullOrWhiteSpace(selectedPath)
            ? selectedPath
            : null;
    }

    private bool TryRequestNavigationContextMenu(
        Point screenLocation, bool allowSelectedFallback)
    {
        string? hitPath = GetNavigationPathAtScreenPoint(screenLocation);
        string? selectedPath = SelectNavigationContextPath(
            hitPath,
            allowSelectedFallback ? GetSelectedNavigationPath() : null,
            allowSelectedFallback);
        if (string.IsNullOrWhiteSpace(selectedPath)
            || !Directory.Exists(selectedPath))
            return false;

        PostToUi(() => NavigationContextMenuRequested?.Invoke(this,
            new NavigationContextMenuEventArgs(selectedPath, screenLocation)));
        return true;
    }

    private bool TryRequestFileContextMenu(Point screenLocation)
    {
        string[] selectedPaths;
        try
        {
            selectedPaths = GetSelectedFileSystemPaths();
            if (selectedPaths.Length == 0
                && GetSelectedItemPath() is { } focusedPath
                && (File.Exists(focusedPath) || Directory.Exists(focusedPath)))
                selectedPaths = [focusedPath];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException
                                   or InvalidOperationException)
        {
            AppLog.Debug(ex, nameof(TryRequestFileContextMenu));
            return false;
        }
        if (selectedPaths.Length == 0) return false;
        PostToUi(() => FileContextMenuRequested?.Invoke(this,
            new FileContextMenuEventArgs(selectedPaths, screenLocation)));
        return true;
    }

    private void InstallNavigationTreeContextMenuHook()
    {
        IntPtr tree = FindDescendant(Handle, "SysTreeView32");
        IntPtr notificationWindow = tree == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.GetParent(tree);
        if (tree == IntPtr.Zero || notificationWindow == IntPtr.Zero) return;

        if (_navigationTreeContextMenuHook is { } existing
            && existing.TreeHandle == tree
            && existing.NotificationWindowHandle == notificationWindow)
            return;

        _navigationTreeContextMenuHook?.Dispose();
        _navigationTreeContextMenuHook = new NavigationTreeNotificationHook(
            this, notificationWindow, tree);
    }

    private void InstallFileViewContextMenuHook()
    {
        IntPtr defView = FindDescendant(Handle, "SHELLDLL_DefView");
        if (defView == IntPtr.Zero) return;
        if (_fileViewContextMenuHook?.FileViewHandle == defView) return;

        _fileViewContextMenuHook?.Dispose();
        _fileViewContextMenuHook = new FileViewContextMenuHook(this, defView);
    }

    private void InstallExplorerSeparatorHook()
    {
        IntPtr directUiWindow = FindDescendant(Handle, "DirectUIHWND");
        IntPtr navigationTree = directUiWindow == IntPtr.Zero
            ? IntPtr.Zero
            : FindDescendant(directUiWindow, "SysTreeView32");
        IntPtr fileView = directUiWindow == IntPtr.Zero
            ? IntPtr.Zero
            : FindDescendant(directUiWindow, "SHELLDLL_DefView");
        if (directUiWindow == IntPtr.Zero
            || navigationTree == IntPtr.Zero
            || fileView == IntPtr.Zero)
            return;

        if (_explorerSeparatorHook is { } existing
            && existing.WindowHandle == directUiWindow
            && existing.NavigationTreeHandle == navigationTree
            && existing.FileViewHandle == fileView)
        {
            existing.Repaint();
            return;
        }

        _explorerSeparatorHook?.Dispose();
        _explorerSeparatorHook = new ExplorerSeparatorHook(
            directUiWindow, navigationTree, fileView);
        _explorerSeparatorHook.Repaint();
    }

    private string? GetSelectedNavigationPathFromTreeText()
    {
        IntPtr tree = FindDescendant(Handle, "SysTreeView32");
        if (tree == IntPtr.Zero) return null;

        return GetSelectedNavigationPathFromTreeText(tree);
    }

    private static string? GetSelectedNavigationPathFromTreeText(IntPtr tree)
    {
        if (tree == IntPtr.Zero) return null;

        IntPtr selected = NativeMethods.SendMessageI(
            tree, NativeMethods.TVM_GETNEXTITEM,
            (IntPtr)NativeMethods.TVGN_CARET, IntPtr.Zero);
        if (selected == IntPtr.Zero) return null;

        var reverseParts = new List<string>();
        for (IntPtr item = selected; item != IntPtr.Zero;
             item = NativeMethods.SendMessageI(
                 tree, NativeMethods.TVM_GETNEXTITEM,
                 (IntPtr)NativeMethods.TVGN_PARENT, item))
        {
            reverseParts.Add(TreeItemText(tree, item));
        }

        reverseParts.Reverse();
        return TryBuildNavigationTreePath(reverseParts);
    }

    internal void BeginNavigationRename()
    {
        if (SwitchToBrowserThread(BeginNavigationRename)) return;

        IntPtr tree = FindDescendant(Handle, "SysTreeView32");
        if (tree == IntPtr.Zero) return;

        IntPtr selected = NativeMethods.SendMessageI(
            tree, NativeMethods.TVM_GETNEXTITEM,
            (IntPtr)NativeMethods.TVGN_CARET, IntPtr.Zero);
        if (selected == IntPtr.Zero) return;

        NativeMethods.SetFocus(tree);
        NativeMethods.SendMessageI(
            tree, NativeMethods.TVM_EDITLABEL, IntPtr.Zero, selected);
    }

    private bool IsNavigationTreeExpandButtonAtCursor()
    {
        return TryGetNavigationTreeHitAtCursor(out NativeMethods.TVHITTESTINFO hit)
            && (hit.flags & NativeMethods.TVHT_ONITEMBUTTON) != 0;
    }

    private bool IsNavigationTreeItemAtCursor() =>
        TryGetNavigationTreeHitAtCursor(out NativeMethods.TVHITTESTINFO hit)
        && hit.hItem != IntPtr.Zero;

    private bool TryGetNavigationTreeHitAtCursor(
        out NativeMethods.TVHITTESTINFO hit)
    {
        hit = default;
        IntPtr tree = FindDescendant(Handle, "SysTreeView32");
        if (tree == IntPtr.Zero) return false;

        hit = new NativeMethods.TVHITTESTINFO
        {
            pt = new NativeMethods.POINT(Cursor.Position.X, Cursor.Position.Y),
        };
        if (!NativeMethods.ScreenToClient(tree, ref hit.pt)) return false;

        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.TVHITTESTINFO>());
        try
        {
            Marshal.StructureToPtr(hit, buffer, false);
            NativeMethods.SendMessageI(tree, NativeMethods.TVM_HITTEST, IntPtr.Zero, buffer);
            hit = Marshal.PtrToStructure<NativeMethods.TVHITTESTINFO>(buffer);
            return hit.hItem != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void QueueNavigationTreeFallback()
    {
        BrowserThread? thread = _browserThread;
        if (thread == null) return;

        int generation = Interlocked.Increment(ref _navigationClickGeneration);
        // ExplorerBrowser commits a tree selection asynchronously.  Some Shell
        // builds have it ready almost immediately while others can take longer
        // after the mouse-up, so one fixed-delay probe is unreliable.
        foreach (int delay in new[] { 100, 350, 750 })
        {
            _ = Task.Delay(delay).ContinueWith(_ =>
            {
                if (Volatile.Read(ref _navigationClickGeneration) == generation)
                    thread.Post(() => VerifyNavigationTreePath(generation));
            }, TaskScheduler.Default);
        }
    }

    private void VerifyNavigationTreePath(int generation)
    {
        if (_browser == null
            || Volatile.Read(ref _navigationClickGeneration) != generation)
            return;

        // The tree's display labels cannot reconstruct Quick access links. Ask
        // the selected Shell item for its target file-system path first.
        string? selectedPath = GetSelectedNavigationPath();
        if (selectedPath == null)
            return;

        string? livePath = QueryLivePath();
        string? pendingPath = Volatile.Read(ref _pendingNavigationPath);
        if (!ShouldRetryNavigationTreeSelection(
                livePath, pendingPath, selectedPath))
            return;

        // Invalidate the other scheduled probes before navigating. A compare-
        // exchange prevents an old selection from winning if the user clicked a
        // different folder while this callback was waiting on the browser STA.
        if (Interlocked.CompareExchange(ref _navigationClickGeneration,
                generation + 1, generation) == generation)
            PostToUi(() => NavigateTo(selectedPath));
    }

    internal static string? TryBuildNavigationTreePath(IReadOnlyList<string> parts)
    {
        for (int partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            string label = parts[partIndex];
            for (int charIndex = 0; charIndex + 1 < label.Length; charIndex++)
            {
                char drive = label[charIndex];
                if (!char.IsLetter(drive) || label[charIndex + 1] != ':') continue;

                string path = char.ToUpperInvariant(drive) + @":\";
                for (int childIndex = partIndex + 1; childIndex < parts.Count; childIndex++)
                {
                    string child = parts[childIndex].Trim();
                    if (child.Length == 0) return null;
                    path = Path.Combine(path, child);
                }
                return path;
            }
        }
        return null;
    }

    internal static bool PathsReferToSameFolder(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;
        return string.Equals(
            first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            second.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldRefreshSelectionSnapshot(
        int message, bool isBrowserSelectionInput, bool isNavigationTreeInput) =>
        isBrowserSelectionInput
        && !isNavigationTreeInput
        && IsSelectionChangingInput(message);

    internal static bool ShouldRetryNavigationTreeSelection(
        string? livePath, string? pendingPath, string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        return !PathsReferToSameFolder(livePath, selectedPath)
            && !PathsReferToSameFolder(pendingPath, selectedPath);
    }

    private static bool IsSelectionChangingInput(int message) =>
        message is WM_KEYDOWN or WM_SYSKEYDOWN
            or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK
            or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

    private bool TryKeepNetworkFolderOrderOnTypeClick(Message message)
    {
        if (message.Msg == WM_LBUTTONUP
            && _suppressedTypeHeaderMouseUpWindow != IntPtr.Zero)
        {
            bool suppress = message.HWnd == _suppressedTypeHeaderMouseUpWindow;
            _suppressedTypeHeaderMouseUpWindow = IntPtr.Zero;
            return suppress;
        }

        if (message.Msg is not (WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
            || _browserThread is not { } thread
            || NativeMethods.GetCurrentThreadId() != thread.ThreadId
            || _browser is null
            || _networkPathStatus != NetworkPathStatus.Available
            || !IsNetworkPath(_currentPath)
            || Volatile.Read(ref _currentViewMode) != (int)FVM_DETAILS)
            return false;

        IntPtr fileView = FindDescendant(Handle, "SHELLDLL_DefView");
        if (fileView == IntPtr.Zero
            || !NativeMethods.IsChild(fileView, message.HWnd))
            return false;

        long coordinates = message.LParam.ToInt64();
        var screenPoint = new NativeMethods.POINT(
            (short)(coordinates & 0xFFFF),
            (short)((coordinates >> 16) & 0xFFFF));
        if (!NativeMethods.ClientToScreen(message.HWnd, ref screenPoint)
            || !IsTypeColumnHeaderAt(message.HWnd, screenPoint)
            || !CurrentShellViewContainsOnlyFolders())
            return false;

        // Windows sorts equal Type values by Name even with Type as its only
        // sort column. Skip this no-op sort so a folder-only network view keeps
        // exactly the order the user was looking at.
        _suppressedTypeHeaderMouseUpWindow = message.HWnd;
        return true;
    }

    internal static bool IsTypeColumnHeaderAt(
        IntPtr viewWindow, NativeMethods.POINT screenPoint)
    {
        if (viewWindow == IntPtr.Zero) return false;

        const int objIdClient = unchecked((int)0xFFFFFFFC);
        // Windows 11 DirectUI exposes sortable headers as split buttons; the
        // adjacent filter dropdown is a separate push button.
        Guid accessibleId = typeof(Accessibility.IAccessible).GUID;
        if (NativeMethods.AccessibleObjectFromWindow(
                viewWindow, objIdClient, ref accessibleId,
                out IntPtr accessiblePointer) < 0
            || accessiblePointer == IntPtr.Zero)
            return false;

        try
        {
            var accessible = (Accessibility.IAccessible)
                Marshal.GetObjectForIUnknown(accessiblePointer);
            object hit = accessible.accHitTest(screenPoint.x, screenPoint.y);
            string? name;
            object? role;
            if (hit is int childId)
            {
                name = accessible.get_accName(childId);
                role = accessible.get_accRole(childId);
            }
            else if (hit is Accessibility.IAccessible child)
            {
                name = child.get_accName(0);
                role = child.get_accRole(0);
            }
            else
            {
                return false;
            }

            int roleId = role switch
            {
                int value => value,
                short value => value,
                AccessibleRole value => (int)value,
                _ => -1,
            };
            bool isSortableHeader = roleId == (int)AccessibleRole.SplitButton
                || roleId == (int)AccessibleRole.ColumnHeader;
            return isSortableHeader
                && string.Equals(name, "Type", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException
            or ArgumentException)
        {
            AppLog.Debug(ex, nameof(IsTypeColumnHeaderAt));
            return false;
        }
        finally { Marshal.Release(accessiblePointer); }
    }

    internal bool CurrentShellViewContainsOnlyFolders()
    {
        if (_browser is null) return false;

        Guid viewId = typeof(NativeMethods.IFolderView2).GUID;
        if (_browser.GetCurrentView(ref viewId, out IntPtr viewPointer) < 0
            || viewPointer == IntPtr.Zero)
            return false;

        try
        {
            var view = (NativeMethods.IFolderView2)
                Marshal.GetObjectForIUnknown(viewPointer);
            if (view.ItemCount(0, out int count) < 0 || count <= 0)
                return false;

            const uint folderAttribute = 0x20000000; // SFGAO_FOLDER
            for (int index = 0; index < count; index++)
            {
                Guid itemId = typeof(NativeMethods.IShellItem).GUID;
                if (view.GetItem(index, ref itemId,
                        out IntPtr itemPointer) < 0
                    || itemPointer == IntPtr.Zero)
                    return false;

                try
                {
                    var item = (NativeMethods.IShellItem)
                        Marshal.GetObjectForIUnknown(itemPointer);
                    if (item.GetAttributes(folderAttribute,
                            out uint attributes) < 0
                        || (attributes & folderAttribute) == 0)
                        return false;
                }
                finally { Marshal.Release(itemPointer); }
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AppLog.Debug(ex, nameof(CurrentShellViewContainsOnlyFolders));
            return false;
        }
        finally { Marshal.Release(viewPointer); }
    }

    // Returns the list-view HWND inside the embedded browser, preferring the
    // window that currently has Win32 focus (user's last interaction point).
    private IntPtr FindListView()
    {
        // If something already has focus and it's within our window rect, use it.
        IntPtr focused = NativeMethods.GetFocus();
        if (focused != IntPtr.Zero
            && NativeMethods.GetWindowRect(Handle,  out NativeMethods.RECT myRect)
            && NativeMethods.GetWindowRect(focused, out NativeMethods.RECT focRect))
        {
            if (focRect.Left >= myRect.Left && focRect.Right  <= myRect.Right
             && focRect.Top  >= myRect.Top  && focRect.Bottom <= myRect.Bottom)
                return focused;
        }
        // Fall back: traverse the child tree.
        IntPtr lv = FindDescendant(Handle, "SysListView32");
        if (lv == IntPtr.Zero) lv = FindDescendant(Handle, "UIItemsView");
        return lv;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void BrowseTo(string path, uint flags = 0)
    {
        if (_browser == null) return;

        int hr = NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _);
        if (hr < 0 || pidl == IntPtr.Zero) return;

        try
        {
            _browser.BrowseToIDList(pidl, flags);
            ApplyTheme();
        }
        finally { NativeMethods.CoTaskMemFree(pidl); }
    }

    private void DestroyBrowser()
    {
        BrowserThread? thread = _browserThread;
        if (thread != null && NativeMethods.GetCurrentThreadId() != thread.ThreadId)
        {
            try { thread.Invoke(DestroyBrowserCore); }
            finally
            {
                thread.MessageHook = null;
                thread.Dispose();
                _browserThread = null;
            }
            return;
        }

        DestroyBrowserCore();
    }

    private void DestroyBrowserCore()
    {
        if (_browser == null) return;

        _suppressedTypeHeaderMouseUpWindow = IntPtr.Zero;
        _navigationTreeContextMenuHook?.Dispose();
        _navigationTreeContextMenuHook = null;
        _fileViewContextMenuHook?.Dispose();
        _fileViewContextMenuHook = null;
        _explorerSeparatorHook?.Dispose();
        _explorerSeparatorHook = null;
        RemoveAsynchronousFileDropTarget();

        try
        {
            if (_eventsCookie != 0)
                _browser.Unadvise(_eventsCookie);
            _eventsCookie = 0;
            _events = null;
            if (_browser is NativeMethods.IObjectWithSite ows)
                ows.SetSite(null);
            _browser.Destroy();
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(DestroyBrowser),
                "The native Explorer browser failed during teardown.");
        }

        try   { Marshal.ReleaseComObject(_browser); }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(DestroyBrowser),
                "Could not release the Explorer browser COM object.");
        }

        _browser = null;
        _site    = null;
        _events = null;
        _eventsCookie = 0;
    }

    // Called by ExplorerBrowserEventsImpl on the browser STA.
    internal void BrowserNavigationCompleted(bool succeeded)
    {
        if (_browser == null || IsDisposed || Disposing) return;
        int networkGeneration = Volatile.Read(ref _networkBrowseGeneration);
        bool isCurrentNetworkBrowse = networkGeneration != 0
            && networkGeneration == Volatile.Read(ref _networkProbeGeneration)
            && _networkPathStatus == NetworkPathStatus.Checking;
        Volatile.Write(ref _pendingNavigationPath, null);
        if (!succeeded)
            AppLog.Debug(nameof(BrowserNavigationCompleted),
                "The shell reported a failed navigation; continuing initial-view setup.");

        // Capture the live path here, while already on the browser STA.  UI polling
        // can then consume the snapshot without blocking on Shell modal operations.
        if (succeeded)
        {
            string? livePath = QueryLivePath();
            if (livePath != null && _networkPathStatus is not
                (NetworkPathStatus.Checking or NetworkPathStatus.Unavailable))
                _currentPath = livePath;
            Volatile.Write(ref _cachedSelectedItemPath, null);
            Volatile.Write(ref _cachedSelectedFileSystemPaths, []);
        }

        InstallAsynchronousFileDropTarget();
        InstallNavigationTreeContextMenuHook();
        InstallFileViewContextMenuHook();
        InstallExplorerSeparatorHook();

        // A completed BrowseToIDList has installed the final view. ExplorerBrowser
        // has now restored any state held in the MultiExplorer property bag.
        if (TryGetCurrentFolderViewState(out ExplorerFolderViewState viewState))
            UpdateCurrentFolderViewState(viewState.ViewMode, viewState.IconSize);
        ActivateShellView(takeFocus: false);
        ApplyTheme();
        if (succeeded)
            PostToUi(() => NavigationChanged?.Invoke(this, EventArgs.Empty));
        if (isCurrentNetworkBrowse)
            PostToUi(() =>
            {
                if (networkGeneration == Volatile.Read(ref _networkProbeGeneration)
                    && _networkPathStatus == NetworkPathStatus.Checking)
                    SetNetworkPathStatus(succeeded
                        ? NetworkPathStatus.Available
                        : NetworkPathStatus.Unavailable);
            });
        ReportInitialNavigationCompleted();
    }

    internal void BrowserNavigationPending(IntPtr pidlFolder)
    {
        _suppressedTypeHeaderMouseUpWindow = IntPtr.Zero;
        string? path = null;
        if (pidlFolder != IntPtr.Zero)
        {
            var buffer = new StringBuilder(32768);
            if (NativeMethods.SHGetPathFromIDList(pidlFolder, buffer)
                && buffer.Length > 0)
                path = buffer.ToString();
        }

        Volatile.Write(ref _pendingNavigationPath, path);
    }

    private bool TryGetCurrentFolderViewState(out ExplorerFolderViewState state)
    {
        state = default;
        if (_browser == null) return false;

        try
        {
            var folderViewId = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            if (_browser.GetCurrentView(ref folderViewId, out IntPtr viewPointer) < 0
                || viewPointer == IntPtr.Zero)
                return false;

            try
            {
                var folderView = (NativeMethods.IFolderView2)
                    Marshal.GetObjectForIUnknown(viewPointer);
                if (folderView.GetViewModeAndIconSize(
                        out uint viewMode, out int iconSize) < 0
                    || !ExplorerFolderViewState.IsSupportedViewMode(viewMode))
                    return false;

                state = new ExplorerFolderViewState(
                    viewMode, Math.Max(0, iconSize));
                return true;
            }
            finally { Marshal.Release(viewPointer); }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(TryGetCurrentFolderViewState));
            return false;
        }
    }

    private void UpdateCurrentFolderViewState(uint viewMode, int iconSize)
    {
        if (!ExplorerFolderViewState.IsSupportedViewMode(viewMode)) return;

        int normalizedIconSize = Math.Max(0, iconSize);
        int normalizedViewMode = unchecked((int)viewMode);
        bool changed = Interlocked.Exchange(
                ref _currentViewMode, normalizedViewMode) != normalizedViewMode;
        changed |= Interlocked.Exchange(
            ref _currentViewIconSize, normalizedIconSize) != normalizedIconSize;
        if (!changed) return;

        var state = new ExplorerFolderViewState(viewMode, normalizedIconSize);
        PostToUi(() => FolderViewStateChanged?.Invoke(this, state));
    }

    private static int GetDefaultIconSize(uint viewMode) => viewMode switch
    {
        1 => 96,
        2 => 16,
        3 => 16,
        FVM_DETAILS => 16,
        6 => 48,
        8 => 32,
        _ => 16,
    };

    private void ReportInitialNavigationCompleted()
    {
        if (Interlocked.Exchange(ref _initialNavigationReported, 1) != 0) return;
        PostToUi(() => InitialNavigationCompleted?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Queries the current folder from the live browser without using any CCW.
    /// Returns null if anything in the chain fails (virtual folder, error, etc.).
    /// </summary>
    private string? QueryLivePath()
    {
        try
        {
            // Step 1 — get IFolderView from the browser's current view.
            var fvId = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
            if (_browser!.GetCurrentView(ref fvId, out IntPtr ppv) < 0 || ppv == IntPtr.Zero)
                return null;

            NativeMethods.IFolderView fv;
            try
            {
                // GetCurrentView already AddRef'd ppv; GetObjectForIUnknown AddRef's again.
                // We release our explicit reference so only the RCW holds one.
                fv = (NativeMethods.IFolderView)Marshal.GetObjectForIUnknown(ppv);
            }
            finally { Marshal.Release(ppv); }

            // Step 2 — get IPersistFolder2 from the view.
            var pf2Id = new Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
            if (fv.GetFolder(ref pf2Id, out IntPtr pf2Ptr) < 0 || pf2Ptr == IntPtr.Zero)
                return null;

            NativeMethods.IPersistFolder2 pf2;
            try
            {
                pf2 = (NativeMethods.IPersistFolder2)Marshal.GetObjectForIUnknown(pf2Ptr);
            }
            finally { Marshal.Release(pf2Ptr); }

            // Step 3 — get the current folder as a PIDL, convert to path string.
            if (pf2.GetCurFolder(out IntPtr pidl) < 0 || pidl == IntPtr.Zero)
                return null;

            try
            {
                var sb = new StringBuilder(260);
                return NativeMethods.SHGetPathFromIDList(pidl, sb) && sb.Length > 0
                    ? sb.ToString()
                    : null;
            }
            finally { NativeMethods.CoTaskMemFree(pidl); }
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(QueryLivePath)); return null; }
    }

    // ── Double-click blank-area detection ────────────────────────────────────

    // Returns true when the cursor double-clicked blank space (no file/folder).
    //
    // Primary strategy — IFolderView.ItemCount(SVGIO_SELECTION):
    //   Called on the SECOND click, after the first click has been dispatched.
    //   If the first click landed on blank space Explorer deselects all items →
    //   count == 0 → blank space.  If it landed on an item that item is selected →
    //   count > 0 → not blank space.  This works for both DirectUI (Windows 11)
    //   and SysListView32 views because IFolderView is the shell model layer, not
    //   the rendering layer.
    //
    // Fallback — LVM_HITTEST on SysListView32:
    //   Used only when the primary strategy fails (e.g. IFolderView QI error).
    private bool IsBlankAreaClick()
    {
        // Primary: check how many items are selected after the first click.
        if (_browser != null)
        {
            var viewId = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
            if (_browser.GetCurrentView(ref viewId, out IntPtr ppv) >= 0 && ppv != IntPtr.Zero)
            {
                int selectedCount = -1;
                try
                {
                    var fv = (NativeMethods.IFolderView)Marshal.GetObjectForIUnknown(ppv);
                    if (fv.ItemCount(1 /*SVGIO_SELECTION*/, out int cnt) >= 0)
                        selectedCount = cnt;
                }
                catch (Exception ex) { AppLog.Debug(ex, nameof(IsBlankAreaClick)); }
                finally { Marshal.Release(ppv); }

                if (selectedCount >= 0)
                    return selectedCount == 0;
            }
        }

        // Fallback: LVM_HITTEST on SysListView32 (pre-Windows-11 shell rendering).
        IntPtr lv = FindDescendant(Handle, "SysListView32");
        if (lv == IntPtr.Zero) lv = FindDescendant(Handle, "UIItemsView");
        if (lv == IntPtr.Zero) return false;

        var pt = new NativeMethods.POINT(Cursor.Position.X, Cursor.Position.Y);
        if (!NativeMethods.ScreenToClient(lv, ref pt)) return false;
        if (!NativeMethods.GetClientRect(lv, out NativeMethods.RECT cr)) return false;
        if (pt.x < 0 || pt.y < 0 || pt.x >= cr.Right || pt.y >= cr.Bottom) return false;

        var ht   = new NativeMethods.LVHITTESTINFO { pt = pt };
        int item = NativeMethods.SendMessage(lv, NativeMethods.LVM_HITTEST, IntPtr.Zero, ref ht);
        return item < 0 || (ht.flags & NativeMethods.LVHT_NOWHERE) != 0;
    }

    // Converts a virtual-key code (as in m.WParam) to the Unicode character it
    // produces with the current keyboard layout and modifier state.
    // Returns '\0' if the key does not produce a character (arrows, F-keys, etc.).
    private static char GetCharFromVk(IntPtr wParam)
    {
        uint vk       = (uint)wParam.ToInt32();
        uint scanCode = NativeMethods.MapVirtualKey(vk, 0u); // MAPVK_VK_TO_VSC
        var  state    = new byte[256];
        NativeMethods.GetKeyboardState(state);
        var sb = new StringBuilder(4);
        int n  = NativeMethods.ToUnicode(vk, scanCode, state, sb, sb.Capacity, 0);
        return n > 0 ? sb[0] : '\0';
    }

    // Returns true when the currently focused HWND is an Edit control — i.e. the user
    // is in the middle of an inline rename.  We must not start filtering in that case.
    private static bool IsEditControl()
    {
        IntPtr focused = NativeMethods.GetFocus();
        if (focused == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        NativeMethods.GetClassName(focused, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls.Equals("Edit",     StringComparison.OrdinalIgnoreCase)
            || cls.Equals("RichEdit", StringComparison.OrdinalIgnoreCase)
            || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    // Returns true when hwnd is part of the file-list pane (under SHELLDLL_DefView).
    // Used to ensure first-click tracking only fires for file-list clicks, not for
    // clicks in the address bar or navigation buttons which share the same panel bounds.
    private bool IsInFileListArea(IntPtr hwnd)
    {
        for (IntPtr cur = hwnd; cur != IntPtr.Zero && cur != Handle;
             cur = NativeMethods.GetParent(cur))
        {
            var sb = new StringBuilder(64);
            NativeMethods.GetClassName(cur, sb, sb.Capacity);
            if (sb.ToString().Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Navigation pane sync ─────────────────────────────────────────────────────

    /// <summary>
    /// Expands the left-hand navigation pane to show and highlight <paramref name="path"/>.
    /// Called by PanelView.PollPath each time the active folder changes.
    /// Fails silently — the pane sync is best-effort.
    /// </summary>
    internal void SyncNavigationPane(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        int generation = Interlocked.Increment(ref _navigationRestoreGeneration);
        BrowserThread? thread = _browserThread;
        if (thread == null) return;

        void SyncIfCurrent()
        {
            if (Volatile.Read(ref _navigationRestoreGeneration) == generation
                && ReferenceEquals(thread, _browserThread)
                && PathsReferToSameFolder(_currentPath, path))
                SyncNavigationPaneCore(path);
        }

        if (NativeMethods.GetCurrentThreadId() == thread.ThreadId)
            SyncIfCurrent();
        else
            thread.Post(SyncIfCurrent);
    }

    /// <summary>
    /// Restores a saved path in the navigation tree. NamespaceTreeControl loads
    /// descendants asynchronously after each expansion, so deep paths require
    /// more than one synchronization pass during application startup.
    /// </summary>
    internal void RestoreNavigationPane(string path)
    {
        _ = RestoreNavigationPaneForStartupAsync(
            path, _itemCountWorkerCancellation.Token);
    }

    /// <summary>
    /// Performs the first navigation-tree restoration without synchronously
    /// waiting on the Shell STA. Callers can await this first pass when the tree
    /// must be populated before presenting the window; later retry passes remain
    /// asynchronous because NamespaceTreeControl expands descendants lazily.
    /// </summary>
    internal async Task RestoreNavigationPaneForStartupAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        int generation = Interlocked.Increment(ref _navigationRestoreGeneration);
        BrowserThread? thread = _browserThread;
        if (thread is null) return;

        try
        {
            await thread.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _navigationRestoreGeneration) == generation
                    && ReferenceEquals(thread, _browserThread)
                    && PathsReferToSameFolder(_currentPath, path))
                    SyncNavigationPaneCore(path);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(RestoreNavigationPaneForStartupAsync),
                $"Could not restore the navigation path '{path}'.");
            return;
        }

        _ = RetryNavigationPaneRestoreAsync(path, generation);
    }

    private async Task RetryNavigationPaneRestoreAsync(string path, int generation)
    {
        int pathDepth = path
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Length;
        int attempts = Math.Clamp(pathDepth + 5, 7, 16);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int delay = Math.Min(400, 125 + (attempt * 35));
            await Task.Delay(delay).ConfigureAwait(false);

            if (Volatile.Read(ref _navigationRestoreGeneration) != generation
                || IsDisposed || Disposing
                || !PathsReferToSameFolder(_currentPath, path))
                return;

            BrowserThread? thread = _browserThread;
            if (thread == null) return;

            thread.Post(() =>
            {
                if (Volatile.Read(ref _navigationRestoreGeneration) != generation
                    || !ReferenceEquals(thread, _browserThread)
                    || !PathsReferToSameFolder(_currentPath, path))
                    return;

                SyncNavigationPaneCore(path);
                if (PathsReferToSameFolder(GetSelectedNavigationPath(), path))
                {
                    Interlocked.CompareExchange(ref _navigationRestoreGeneration,
                        generation + 1, generation);
                }
            });
        }
    }

    private void SyncNavigationPaneCore(string path)
    {
        if (SwitchToBrowserThread(() => SyncNavigationPaneCore(path))) return;
        if (_browser == null || string.IsNullOrEmpty(path)) return;
        try
        {
            IntPtr hwndTree = FindDescendant(Handle, "SysTreeView32");
            bool isUncPath = path.StartsWith(@"\\", StringComparison.Ordinal);
            string? selectedPath = isUncPath
                ? GetSelectedNavigationPath()
                : GetSelectedNavigationPathFromTreeText(hwndTree);
            if (PathsReferToSameFolder(selectedPath, path))
                return;

            // Resolve by Shell identity first. Some Windows builds return success
            // from EnsureItemVisible before the visible Win32 tree has populated
            // its descendants, so local paths must still run the direct expansion
            // below on every retry pass. UNC paths have no drive node to walk.
            TrySyncNamespaceTree(path);
            if (isUncPath) return;

            if (hwndTree != IntPtr.Zero)
                SyncTreeView(hwndTree, path);
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(SyncNavigationPane)); }
    }

    // Resolves paths that cannot be represented as a drive-letter breadcrumb
    // (notably UNC shares) through the shell namespace rather than display text.
    private bool TrySyncNamespaceTree(string path)
    {
        NativeMethods.INameSpaceTreeControl? tree = null;
        NativeMethods.IShellItem? item = null;
        try
        {
            tree = TryGetNameSpaceTreeControl(
                _browser as NativeMethods.IComServiceProvider);
            if (tree == null) return false;

            var shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            if (NativeMethods.SHCreateItemFromParsingName(
                    path, IntPtr.Zero, ref shellItemId, out IntPtr itemPtr) < 0
                || itemPtr == IntPtr.Zero)
                return false;

            try { item = (NativeMethods.IShellItem)Marshal.GetObjectForIUnknown(itemPtr); }
            finally { Marshal.Release(itemPtr); }

            if (tree.EnsureItemVisible(item) < 0) return false;

            const uint NSTCIS_SELECTED = 0x0001;
            return tree.SetItemState(item,
                NSTCIS_SELECTED, NSTCIS_SELECTED) >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(TrySyncNamespaceTree));
            return false;
        }
        finally
        {
            if (item != null)
            {
                try { Marshal.ReleaseComObject(item); }
                catch (Exception ex)
                {
                    AppLog.Debug(ex, nameof(TrySyncNamespaceTree),
                        "Could not release the navigation Shell item COM object.");
                }
            }
            if (tree != null)
            {
                try { Marshal.ReleaseComObject(tree); }
                catch (Exception ex)
                {
                    AppLog.Debug(ex, nameof(TrySyncNamespaceTree),
                        "Could not release the namespace-tree COM object.");
                }
            }
        }
    }

    private NativeMethods.INameSpaceTreeControl? TryGetNameSpaceTreeControl(
        NativeMethods.IComServiceProvider? sp)
    {
        var treeId = typeof(NativeMethods.INameSpaceTreeControl).GUID;
        const int OBJID_NATIVEOM = unchecked((int)0xFFFFFFF0);

        // The navigation control exposes its native automation object directly
        // from its HWND.  Prefer this route because service exposure differs
        // between Windows Shell builds and configurations.
        IntPtr namespaceTreeHwnd = FindDescendant(Handle, "NamespaceTreeControl");
        if (namespaceTreeHwnd != IntPtr.Zero)
        {
            if (NativeMethods.AccessibleObjectFromWindow(
                    namespaceTreeHwnd, OBJID_NATIVEOM, ref treeId,
                    out IntPtr hwndTreePtr) >= 0
                && hwndTreePtr != IntPtr.Zero)
            {
                try
                {
                    return (NativeMethods.INameSpaceTreeControl)
                        Marshal.GetObjectForIUnknown(hwndTreePtr);
                }
                finally { Marshal.Release(hwndTreePtr); }
            }
        }


        if (sp == null) return null;

        // Some ExplorerBrowser versions expose the namespace tree directly.
        if (sp.QueryService(ref treeId, ref treeId, out IntPtr treePtr) >= 0
            && treePtr != IntPtr.Zero)
        {
            try
            {
                return (NativeMethods.INameSpaceTreeControl)Marshal.GetObjectForIUnknown(treePtr);
            }
            finally { Marshal.Release(treePtr); }
        }

        // Otherwise obtain the tree HWND through IShellBrowser and request its
        // native automation object, which implements INameSpaceTreeControl.
        var shellBrowserId = new Guid("000214E2-0000-0000-C000-000000000046");
        if (sp.QueryService(ref shellBrowserId, ref shellBrowserId, out IntPtr browserPtr) < 0
            || browserPtr == IntPtr.Zero)
            return null;

        NativeMethods.IShellBrowser shellBrowser;
        try { shellBrowser = (NativeMethods.IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr); }
        finally { Marshal.Release(browserPtr); }

        IntPtr treeHwnd;
        int controlResult;
        try { controlResult = shellBrowser.GetControlWindow(4 /* FCW_TREE */, out treeHwnd); }
        finally { Marshal.ReleaseComObject(shellBrowser); }

        if (controlResult < 0 || treeHwnd == IntPtr.Zero) return null;

        if (NativeMethods.AccessibleObjectFromWindow(
                treeHwnd, OBJID_NATIVEOM, ref treeId, out IntPtr nativeTreePtr) < 0
            || nativeTreePtr == IntPtr.Zero)
            return null;

        try
        {
            return (NativeMethods.INameSpaceTreeControl)Marshal.GetObjectForIUnknown(nativeTreePtr);
        }
        finally { Marshal.Release(nativeTreePtr); }
    }

    // Expands the SysTreeView32 to show and select the item for the given path.
    //
    // IExplorerBrowser runs in-process: the SysTreeView32 belongs to our process, so
    // TVM_* messages can use direct heap/stack pointers (no cross-process VirtualAllocEx).
    //
    // Algorithm:
    //   1. Walk root items looking for a node (or child of a node) whose text matches
    //      the drive letter — e.g. "C:" matches "Local Disk (C:)" or "OS (C:)".
    //   2. From the drive node, walk down each path component by display name.
    //   3. Select the deepest match and scroll it into view.
    private void SyncTreeView(IntPtr hwndTree, string path)
    {
        string[] parts = path.TrimEnd(Path.DirectorySeparatorChar)
                             .Split(Path.DirectorySeparatorChar,
                                    StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string driveLetter = parts[0];

        // ── Phase 1: find the drive item ─────────────────────────────────────
        //
        // Windows 11 navigation pane has a single "Desktop" root; "This PC"
        // (and its drive children) are grandchildren of that root:
        //   root[0]="Desktop" → child[n]="This PC" → grandchild="Local Disk (C:)"
        // We therefore search three levels deep: root, child, grandchild.

        IntPtr hDrive = IntPtr.Zero;
        IntPtr hRoot  = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                    (IntPtr)NativeMethods.TVGN_ROOT, IntPtr.Zero);
        //int ri = 0;
        while (hRoot != IntPtr.Zero && hDrive == IntPtr.Zero)
        {
            string rootText = TreeItemText(hwndTree, hRoot);

            // Level 0: root is itself a drive?
            if (IsDriveMatch(rootText, driveLetter)) { hDrive = hRoot; break; }

            ExpandTreeItem(hwndTree, hRoot);
            IntPtr hL1 = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                     (IntPtr)NativeMethods.TVGN_CHILD, hRoot);
            //int ci = 0;
            while (hL1 != IntPtr.Zero && hDrive == IntPtr.Zero)
            {
                string l1 = TreeItemText(hwndTree, hL1);

                // Level 1: direct child is a drive?
                if (IsDriveMatch(l1, driveLetter)) { hDrive = hL1; break; }

                // Level 1→2: child is the "This PC" shell container?
                // Drives live one more level down from here.
                if (IsComputerNode(l1))
                {
                    ExpandTreeItem(hwndTree, hL1);
                    IntPtr hL2 = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                             (IntPtr)NativeMethods.TVGN_CHILD, hL1);
                    //int gi = 0;
                    while (hL2 != IntPtr.Zero)
                    {
                        string l2 = TreeItemText(hwndTree, hL2);
                        if (IsDriveMatch(l2, driveLetter)) { hDrive = hL2; break; }
                        hL2 = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                          (IntPtr)NativeMethods.TVGN_NEXT, hL2);
                    }
                }

                hL1 = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                  (IntPtr)NativeMethods.TVGN_NEXT, hL1);
            }

            if (hDrive == IntPtr.Zero)
                hRoot = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                    (IntPtr)NativeMethods.TVGN_NEXT, hRoot);
        }

        if (hDrive == IntPtr.Zero) return;

        // ── Phase 2: walk down remaining path components by display name ─────

        IntPtr hCurrent = hDrive;
        bool exactMatch = true;
        for (int i = 1; i < parts.Length; i++)
        {
            ExpandTreeItem(hwndTree, hCurrent);
            IntPtr hChild = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                        (IntPtr)NativeMethods.TVGN_CHILD, hCurrent);
            bool found = false;
            //int ci2 = 0;
            while (hChild != IntPtr.Zero)
            {
                string ct = TreeItemText(hwndTree, hChild);
                if (string.Equals(ct, parts[i], StringComparison.OrdinalIgnoreCase))
                { hCurrent = hChild; found = true; break; }
                hChild = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                     (IntPtr)NativeMethods.TVGN_NEXT, hChild);
            }
            if (!found) { exactMatch = false; break; }
        }

        // Only select on an exact match — selecting an ancestor would cause the
        // NamespaceTreeControl to navigate the file list to the wrong folder.
        // Also skip if the item is already the caret to avoid a redundant navigation.
        if (exactMatch)
        {
            IntPtr hCaret = NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_GETNEXTITEM,
                                                        (IntPtr)NativeMethods.TVGN_CARET, IntPtr.Zero);
            if (hCaret != hCurrent)
                NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_SELECTITEM,
                                           (IntPtr)NativeMethods.TVGN_CARET, hCurrent);
        }

        NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_ENSUREVISIBLE,
                                   IntPtr.Zero, hCurrent);
    }

    // Reads the display text of a tree item.  Caller-allocated buffer is on the heap;
    // safe for same-process SendMessage.
    private static string TreeItemText(IntPtr hwndTree, IntPtr hItem)
    {
        const int MaxChars = 512;
        IntPtr pBuf = Marshal.AllocHGlobal(MaxChars * 2); // Unicode: 2 bytes/char
        try
        {
            var tv = new NativeMethods.TVITEM
            {
                mask       = NativeMethods.TVIF_TEXT | NativeMethods.TVIF_HANDLE,
                hItem      = hItem,
                pszText    = pBuf,
                cchTextMax = MaxChars,
            };
            NativeMethods.SendMessageTv(hwndTree, NativeMethods.TVM_GETITEM, IntPtr.Zero, ref tv);
            return Marshal.PtrToStringUni(pBuf) ?? "";
        }
        finally { Marshal.FreeHGlobal(pBuf); }
    }

    private static void ExpandTreeItem(IntPtr hwndTree, IntPtr hItem)
        => NativeMethods.SendMessageI(hwndTree, NativeMethods.TVM_EXPAND,
                                       (IntPtr)NativeMethods.TVE_EXPAND, hItem);

    // Returns true if a tree item's display text refers to the given drive letter.
    // "Local Disk (C:)" / "OS (C:)" / "C:" / "C:\ " all match driveLetter "C:".
    private static bool IsDriveMatch(string text, string driveLetter)
    {
        string d = driveLetter.TrimEnd(':'); // "C"
        return text.IndexOf($"({d}:)", StringComparison.OrdinalIgnoreCase) >= 0
            || text.StartsWith(driveLetter, StringComparison.OrdinalIgnoreCase);
    }

    // Returns true if the text is a known display name for the "This PC" shell folder
    // ({20D04FE0-3AEA-1069-A2D8-08002B30309D}).  Covers common localisations.
    private static bool IsComputerNode(string text)
    {
        string t = text.Trim();
        return string.Equals(t, "This PC",       StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Computer",      StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "My Computer",   StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Dieser PC",     StringComparison.OrdinalIgnoreCase)  // German
            || string.Equals(t, "Ce PC",         StringComparison.OrdinalIgnoreCase)  // French
            || string.Equals(t, "Este equipo",   StringComparison.OrdinalIgnoreCase)  // Spanish
            || string.Equals(t, "Questo PC",     StringComparison.OrdinalIgnoreCase)  // Italian
            || string.Equals(t, "Mijn computer", StringComparison.OrdinalIgnoreCase)  // Dutch
            || string.Equals(t, "此电脑",         StringComparison.OrdinalIgnoreCase); // Chinese
    }

    /// <summary>
    /// NamespaceTreeControl handles NM_RCLICK synchronously inside its own window
    /// procedure, so a message-pump filter cannot reliably replace its menu. Hook
    /// the notification window and consume that notification before the legacy
    /// Shell menu is created.
    /// </summary>
    private sealed class NavigationTreeNotificationHook : NativeWindow, IDisposable
    {
        private const int WmNotify = 0x004E;
        private const int NmRightClick = -5;
        private ExplorerHost? _owner;

        internal IntPtr NotificationWindowHandle { get; }
        internal IntPtr TreeHandle { get; }

        internal NavigationTreeNotificationHook(ExplorerHost owner,
            IntPtr notificationWindow, IntPtr tree)
        {
            ArgumentNullException.ThrowIfNull(owner);
            if (notificationWindow == IntPtr.Zero)
                throw new ArgumentException(
                    "A navigation notification window is required.",
                    nameof(notificationWindow));
            if (tree == IntPtr.Zero)
                throw new ArgumentException(
                    "A navigation tree window is required.", nameof(tree));

            _owner = owner;
            NotificationWindowHandle = notificationWindow;
            TreeHandle = tree;
            AssignHandle(notificationWindow);
        }

        protected override void WndProc(ref Message message)
        {
            ExplorerHost? owner = _owner;
            if (owner != null
                && message.Msg == WmNotify
                && message.LParam != IntPtr.Zero)
            {
                var header = Marshal.PtrToStructure<NotifyHeader>(message.LParam);
                if (header.WindowFrom == TreeHandle
                    && header.Code == NmRightClick
                    && owner.TryRequestNavigationContextMenu(
                        Cursor.Position, allowSelectedFallback: false))
                {
                    message.Result = (IntPtr)1;
                    return;
                }
            }

            if (owner != null && message.Msg == NativeMethods.WM_CONTEXTMENU)
            {
                Point screenLocation = GetContextMenuScreenLocation(
                    message.LParam, Cursor.Position);
                bool keyboardRequest = message.LParam.ToInt64() == -1;
                if (owner.TryRequestNavigationContextMenu(
                        screenLocation, allowSelectedFallback: keyboardRequest))
                {
                    message.Result = IntPtr.Zero;
                    return;
                }
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (_owner == null) return;

            _owner = null;
            ReleaseHandle();
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NotifyHeader
        {
            internal readonly IntPtr WindowFrom;
            internal readonly nuint IdFrom;
            internal readonly int Code;
        }
    }

    /// <summary>
    /// Native file views can send a right-click notification directly to
    /// SHELLDLL_DefView, bypassing the WinForms message filter. Intercept that
    /// notification so all view modes show the same item context menu.
    /// </summary>
    private sealed class FileViewContextMenuHook : NativeWindow, IDisposable
    {
        private const int WmNotify = 0x004E;
        private const int NmRightClick = -5;
        private ExplorerHost? _owner;

        internal IntPtr FileViewHandle { get; }

        internal FileViewContextMenuHook(ExplorerHost owner, IntPtr fileViewHandle)
        {
            ArgumentNullException.ThrowIfNull(owner);
            if (fileViewHandle == IntPtr.Zero)
                throw new ArgumentException("A file view handle is required.",
                    nameof(fileViewHandle));

            _owner = owner;
            FileViewHandle = fileViewHandle;
            AssignHandle(fileViewHandle);
        }

        protected override void WndProc(ref Message message)
        {
            ExplorerHost? owner = _owner;
            if (owner != null && message.Msg == WmNotify
                && message.LParam != IntPtr.Zero)
            {
                var header = Marshal.PtrToStructure<NotifyHeader>(message.LParam);
                if (header.Code == NmRightClick
                    && (header.WindowFrom == FileViewHandle
                        || NativeMethods.IsChild(FileViewHandle, header.WindowFrom))
                    && owner.TryRequestFileContextMenu(Cursor.Position))
                {
                    message.Result = (IntPtr)1;
                    return;
                }
            }

            if (owner != null && message.Msg == NativeMethods.WM_CONTEXTMENU)
            {
                Point screenLocation = GetContextMenuScreenLocation(
                    message.LParam, Cursor.Position);
                if (owner.TryRequestFileContextMenu(screenLocation))
                {
                    message.Result = IntPtr.Zero;
                    return;
                }
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (_owner == null) return;

            _owner = null;
            ReleaseHandle();
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NotifyHeader
        {
            internal readonly IntPtr WindowFrom;
            internal readonly nuint IdFrom;
            internal readonly int Code;
        }
    }

    /// <summary>
    /// ExplorerBrowser paints the navigation/file-view splitter with a fixed
    /// light system colour, even when its children use the dark Explorer theme.
    /// Repaint only that gap after DirectUI has finished drawing so mouse input
    /// and native splitter behaviour remain unchanged.
    /// </summary>
    private sealed class ExplorerSeparatorHook : NativeWindow, IDisposable
    {
        private const int WmPaint = 0x000F;
        private const int WmNcPaint = 0x0085;
        private const int WmSize = 0x0005;
        private const int WmWindowPositionChanged = 0x0047;
        private const int WmThemeChanged = 0x031A;
        private bool _disposed;

        internal IntPtr WindowHandle { get; }
        internal IntPtr NavigationTreeHandle { get; }
        internal IntPtr FileViewHandle { get; }

        internal ExplorerSeparatorHook(IntPtr window, IntPtr navigationTree,
            IntPtr fileView)
        {
            if (window == IntPtr.Zero)
                throw new ArgumentException(
                    "A DirectUI host window is required.", nameof(window));
            if (navigationTree == IntPtr.Zero)
                throw new ArgumentException(
                    "A navigation tree window is required.", nameof(navigationTree));
            if (fileView == IntPtr.Zero)
                throw new ArgumentException(
                    "A file-view window is required.", nameof(fileView));

            WindowHandle = window;
            NavigationTreeHandle = navigationTree;
            FileViewHandle = fileView;
            AssignHandle(window);
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);

            if (message.Msg is WmPaint
                or WmNcPaint
                or WmSize
                or WmWindowPositionChanged
                or WmThemeChanged)
                Repaint();
        }

        internal void Repaint()
        {
            if (_disposed
                || !ThemeManager.IsDark
                || !NativeMethods.GetWindowRect(
                    WindowHandle, out NativeMethods.RECT hostBounds)
                || !NativeMethods.GetWindowRect(
                    NavigationTreeHandle, out NativeMethods.RECT navigationBounds)
                || !NativeMethods.GetWindowRect(
                    FileViewHandle, out NativeMethods.RECT fileViewBounds))
                return;

            int left = navigationBounds.Right - hostBounds.Left;
            int right = fileViewBounds.Left - hostBounds.Left;
            int top = Math.Max(navigationBounds.Top, fileViewBounds.Top)
                - hostBounds.Top;
            int bottom = Math.Min(navigationBounds.Bottom, fileViewBounds.Bottom)
                - hostBounds.Top;
            if (right <= left || bottom <= top) return;

            IntPtr deviceContext = NativeMethods.GetWindowDC(WindowHandle);
            if (deviceContext == IntPtr.Zero) return;

            try
            {
                using Graphics graphics = Graphics.FromHdc(deviceContext);
                using var brush = new SolidBrush(ThemeManager.Border);
                graphics.FillRectangle(brush,
                    Rectangle.FromLTRB(left, top, right, bottom));
            }
            finally
            {
                NativeMethods.ReleaseDC(WindowHandle, deviceContext);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            ReleaseHandle();
        }
    }

}

/// <summary>
/// OLE drop target for ordinary file-system objects. Drop returns as soon as the
/// paths and intended effect have been captured; the owning ExplorerHost performs
/// the actual Shell operation on a separate STA.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ShellFileDropTarget : NativeMethods.IDropTarget
{
    private const short CfHDrop = 15;
    private readonly Func<string> _getDestination;
    private readonly Func<string[], string, bool, bool> _startOperation;
    private string[]? _dragPaths;

    public ShellFileDropTarget(
        Func<string> getDestination,
        Func<string[], string, bool, bool> startOperation)
    {
        _getDestination = getDestination;
        _startOperation = startOperation;
    }

    public int DragEnter(
        System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
        uint keyState, NativeMethods.POINTL point, ref uint effect)
    {
        _dragPaths = TryGetFileDropPaths(dataObject);
        effect = ChooseEffect(effect, keyState, _dragPaths, _getDestination());
        return 0;
    }

    public int DragOver(uint keyState, NativeMethods.POINTL point, ref uint effect)
    {
        effect = ChooseEffect(effect, keyState, _dragPaths, _getDestination());
        return 0;
    }

    public int DragLeave()
    {
        _dragPaths = null;
        return 0;
    }

    public int Drop(
        System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
        uint keyState, NativeMethods.POINTL point, ref uint effect)
    {
        string[]? paths = _dragPaths ?? TryGetFileDropPaths(dataObject);
        string destination = _getDestination();
        uint chosen = ChooseEffect(effect, keyState, paths, destination);
        _dragPaths = null;

        if (paths is not { Length: > 0 } || chosen == NativeMethods.DROPEFFECT_NONE)
        {
            effect = NativeMethods.DROPEFFECT_NONE;
            return 0;
        }

        bool move = chosen == NativeMethods.DROPEFFECT_MOVE;
        if (!_startOperation(paths, destination, move))
        {
            effect = NativeMethods.DROPEFFECT_NONE;
            return 0;
        }

        // The helper owns the complete move. Report an optimized move so the drag
        // source does not delete the source a second time after Drop returns.
        effect = move ? NativeMethods.DROPEFFECT_NONE : chosen;
        return 0;
    }

    internal static uint ChooseEffect(
        uint allowedEffects, uint keyState, string[]? paths, string destination)
    {
        if (paths is not { Length: > 0 }
            || !Directory.Exists(destination)
            || (keyState & NativeMethods.MK_RBUTTON) != 0)
            return NativeMethods.DROPEFFECT_NONE;

        uint preferred;
        if ((keyState & NativeMethods.MK_CONTROL) != 0)
        {
            preferred = NativeMethods.DROPEFFECT_COPY;
        }
        else if ((keyState & NativeMethods.MK_SHIFT) != 0)
        {
            preferred = NativeMethods.DROPEFFECT_MOVE;
        }
        else
        {
            preferred = FileDragDropPolicy.ShouldMoveByDefault(
                paths, destination)
                ? NativeMethods.DROPEFFECT_MOVE
                : NativeMethods.DROPEFFECT_COPY;
        }

        if ((allowedEffects & preferred) != 0) return preferred;
        if ((allowedEffects & NativeMethods.DROPEFFECT_COPY) != 0)
            return NativeMethods.DROPEFFECT_COPY;
        if ((allowedEffects & NativeMethods.DROPEFFECT_MOVE) != 0)
            return NativeMethods.DROPEFFECT_MOVE;
        return NativeMethods.DROPEFFECT_NONE;
    }

    private static string[]? TryGetFileDropPaths(
        System.Runtime.InteropServices.ComTypes.IDataObject dataObject)
    {
        var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = CfHDrop,
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
        };

        try
        {
            if (dataObject.QueryGetData(ref format) != 0) return null;
            dataObject.GetData(ref format, out System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);
            try
            {
                if (medium.tymed != System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                    || medium.unionmember == IntPtr.Zero)
                    return null;

                uint count = NativeMethods.DragQueryFileW(
                    medium.unionmember, uint.MaxValue, null, 0);
                if (count == 0) return null;

                var paths = new string[count];
                for (uint index = 0; index < count; index++)
                {
                    uint length = NativeMethods.DragQueryFileW(
                        medium.unionmember, index, null, 0);
                    var path = new StringBuilder(checked((int)length + 1));
                    NativeMethods.DragQueryFileW(
                        medium.unionmember, index, path, (uint)path.Capacity);
                    paths[index] = path.ToString();
                }
                return paths;
            }
            finally { NativeMethods.ReleaseStgMedium(ref medium); }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ShellFileDropTarget),
                "Could not read CF_HDROP from the OLE data object.");
            return null;
        }
    }
}

// Non-nested internal class — private nested class CCW does not reliably answer
// QueryInterface for IServiceProvider in .NET 8, so moving it here ensures the
// browser can QI the site for IServiceProvider and reach QueryService.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class BrowserSiteImpl : NativeMethods.IServiceProvider
{
    // SID == IID for IFolderViewSettings.
    private static readonly Guid _sidFolderViewSettings =
        new Guid("AE8C987D-8797-4ED3-BE72-2A47DD938DB0");

    // Kept as a field so the object survives GC for the shell view's lifetime.
    private readonly FolderViewSettingsImpl _fvs = new FolderViewSettingsImpl();

    public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
    {
        if (guidService == _sidFolderViewSettings && riid == _sidFolderViewSettings)
        {
            ppvObject = Marshal.GetComInterfaceForObject(
                _fvs, typeof(NativeMethods.IFolderViewSettings));
            return 0;
        }
        ppvObject = IntPtr.Zero;
        // QueryService requires E_NOINTERFACE for an unsupported service/IID pair.
        // E_NOTIMPL is not equivalent and is handled differently by some Shell builds.
        return unchecked((int)0x80004002);
    }

}

internal readonly record struct ItemCountRefreshRequest(
    string Path,
    bool ShowHiddenItems,
    bool ShowProtectedSystemItems,
    int Generation);

internal readonly record struct DirectoryItemCount(int FileCount, int FolderCount);

internal readonly record struct ExplorerShellColumn(
    NativeMethods.PROPERTYKEY Key, string CanonicalName,
    string DisplayName, int Width);

internal readonly record struct ExplorerFolderViewState(uint ViewMode, int IconSize)
{
    internal static bool IsSupportedViewMode(uint viewMode) =>
        viewMode is >= 1 and <= 8;
}

// Non-nested for the same reason as BrowserSiteImpl: private nested callback classes
// do not reliably expose their requested COM interface from a .NET 8 CCW.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ExplorerBrowserEventsImpl : NativeMethods.IExplorerBrowserEvents
{
    private readonly WeakReference<ExplorerHost> _owner;

    internal ExplorerBrowserEventsImpl(ExplorerHost owner)
        => _owner = new WeakReference<ExplorerHost>(owner);

    public int OnNavigationPending(IntPtr pidlFolder)
    {
        try
        {
            if (_owner.TryGetTarget(out ExplorerHost? owner))
                owner.BrowserNavigationPending(pidlFolder);
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(OnNavigationPending)); }
        return 0;
    }
    public int OnViewCreated(IntPtr psv) => 0;

    public int OnNavigationComplete(IntPtr pidlFolder)
    {
        try
        {
            if (_owner.TryGetTarget(out ExplorerHost? owner))
                owner.BrowserNavigationCompleted(succeeded: true);
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(OnNavigationComplete)); }
        return 0;
    }

    public int OnNavigationFailed(IntPtr pidlFolder)
    {
        try
        {
            if (_owner.TryGetTarget(out ExplorerHost? owner))
                owner.BrowserNavigationCompleted(succeeded: false);
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(OnNavigationFailed)); }
        return 0;
    }
}

// Non-nested internal class so the CCW responds to QueryInterface correctly.
// (Private nested classes have unreliable CCW QI in .NET 8.)
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class FolderViewSettingsImpl : NativeMethods.IFolderViewSettings
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private static readonly Guid ItemNameDisplayFormatId =
        new("B725F130-47EF-101A-A5F1-02608C9EEBAC");

    public int GetColumnPropertyList(ref Guid riid, out IntPtr ppv)
    { ppv = IntPtr.Zero; return E_NOTIMPL; }

    public int GetGroupByProperty(out NativeMethods.PROPERTYKEY pkey, out int pfGroupAscending)
    { pkey = default; pfGroupAscending = 0; return E_NOTIMPL; }

    public int GetViewMode(out uint puViewMode)
    {
        // Let ExplorerBrowser restore this folder from its named property bag.
        // Initialize's FOLDERSETTINGS still provides Details for an unseen folder.
        puViewMode = 0;
        return E_NOTIMPL;
    }

    public int GetIconSize(out uint puIconSize)
    {
        puIconSize = 0;
        return E_NOTIMPL;
    }

    public int GetFolderFlags(out uint pfolderMask, out uint pfolderFlags)
    { pfolderMask = 0; pfolderFlags = 0; return 0; }

    public int GetSortColumns(IntPtr rgSortColumns, uint cColumnsIn, out uint cColumnsOut)
    {
        if (rgSortColumns == IntPtr.Zero || cColumnsIn == 0)
        {
            cColumnsOut = 0;
            return E_NOTIMPL;
        }

        var nameAscending = new NativeMethods.SORTCOLUMN
        {
            propkey = new NativeMethods.PROPERTYKEY
            {
                fmtid = ItemNameDisplayFormatId,
                pid = 10,
            },
            direction = 1,
        };
        Marshal.StructureToPtr(nameAscending, rgSortColumns, fDeleteOld: false);
        cColumnsOut = 1;
        return 0;
    }

    public int GetGroupSubsetCount(out uint pcVisibleRows)
    { pcVisibleRows = 0; return E_NOTIMPL; }
}
