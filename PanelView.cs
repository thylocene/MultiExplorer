using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiExplorer;

internal enum PaneDividerEdge
{
    Left,
    Right,
}

internal sealed class OpenInFileExplorerRequestedEventArgs(
    ExplorerHost sourceTab, string path, Point dropPosition) : EventArgs
{
    internal ExplorerHost SourceTab { get; } = sourceTab;
    internal string Path { get; } = path;
    internal Point DropPosition { get; } = dropPosition;
}

internal sealed record OppositePaneTransferRequest(
    FileOperationKind Kind, IReadOnlyList<string> Paths);

/// <summary>
/// One panel of the dual-pane window.  Contains a tab bar, path bar, command bar,
/// and one ExplorerHost per open tab.  Only the active tab's host is visible.
/// Optional Details Pane (bottom) and Preview Pane (right) are toggled from the View menu.
/// </summary>
public sealed class PanelView : UserControl
{
    internal const string HelpFileName = "MultiExplorer.Help.html";
    private const string HelpResourceName = "MultiExplorer.EmbeddedHelp.html";
    internal const uint QuickAccessMenuCommandId = 0xE101;
    internal const uint CreateShortcutMenuCommandId = 0xE102;
    internal const uint CopyToOtherPaneMenuCommandId = 0xE103;
    internal const uint MoveToOtherPaneMenuCommandId = 0xE104;

    private static readonly SemaphoreSlim HelpFileGate = new(1, 1);

    private enum ViewMenuGlyph
    {
        ExtraLargeIcons,
        LargeIcons,
        MediumIcons,
        SmallIcons,
        List,
        Details,
        Tiles,
        Content,
    }

    // The shell paints active and inactive selections in similar blue shades.
    // Use the shared divider edge as a focused-pane indicator instead of a full frame.
    private const int FocusBorderThickness = 5;
    internal const int PreviewSplitterWidth = PreviewSplitterBar.BarWidth;
    private const int MinimumPreviewPaneWidth = 160;
    private const int MinimumFileViewWidth = 240;
    internal const string QuickLookProjectUrl = "https://github.com/ql-win/quicklook";
    internal const string QuickLookAcknowledgementText =
        "QuickLook is a separate third-party application that provides an " +
        "instant-preview experience to Windows. See " + QuickLookProjectUrl;

    private readonly TabBar     _tabBar;
    private readonly PathBar    _pathBar;
    private readonly CommandBar _commandBar;
    private readonly Panel      _content;
    private readonly StatusStrip _itemCountBar;
    private readonly ToolStripStatusLabel _itemCountLabel;
    private readonly ToolStripStatusLabel _selectedSizeLabel;

    // Content sub-layout: panes + host container (DockStyle.Fill fills remaining space)
    private readonly Panel        _hostContainer;
    private readonly ExplorerCommandBand _explorerCommandBand;
    private readonly DetailsPanel _detailsPanel;
    private readonly Panel        _previewRegion;
    private readonly PreviewPane  _previewPanel;
    private readonly PreviewSplitterBar _previewSplitter;
    private int _preferredPreviewPaneWidth = AppSettings.DefaultPreviewPaneWidth;
    private int _previewDragStartWidth;
    private readonly ManagedDetailsListView _managedDetailsListView;
    private readonly Panel _networkUnavailableOverlay;
    private readonly Label _networkUnavailableMessage;
    private ExplorerHost? _networkRetryHost;
    private readonly System.Windows.Forms.Timer _managedDetailsRefreshTimer;
    private bool _managedDetailsOverlaySuppressed;
    private string _managedDetailsPath = string.Empty;
    private IReadOnlyList<ManagedDetailsListView.ColumnDefinition>
        _localManagedColumns = [];
    private bool _usingNetworkManagedColumns;
    private int _networkManagedColumnsGeneration;
    private bool _initialBrowserNavigationReady;
    private bool _initialBrowserReadyRaised;

    /// <summary>Raised when the user clicks the Mirror button; payload is the current folder path.</summary>
    public event EventHandler<string>? MirrorToOtherRequested;

    /// <summary>Raised when the user asks to compare the active left and right tabs.</summary>
    internal event EventHandler? ComparePanesRequested;

    /// <summary>Raised for a single file or folder chosen from its context menu.</summary>
    internal event EventHandler<string>? CreateShortcutRequested;

    internal event EventHandler<OppositePaneTransferRequest>?
        TransferToOtherPaneRequested;

    /// <summary>Raised when the user toggles the QuickLook integration on or off.</summary>
    public event EventHandler? QuickLookToggled;

    /// <summary>Raised after the user finishes resizing this explorer's preview pane.</summary>
    internal event EventHandler<int>? PreviewPaneWidthChanged;

    /// <summary>Raised when the user chooses Exit from the command bar (or Ctrl+Q).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user chooses "Set window hotkey…" from the command bar.</summary>
    public event EventHandler? SetHotkeyRequested;

    /// <summary>Raised when the user opens the unified Settings form.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user asks to toggle launch at Windows sign-in.</summary>
    public event EventHandler? StartWithWindowsToggled;

    /// <summary>Raised when the user toggles minimize-on-close behavior.</summary>
    public event EventHandler? MinimizeToTrayToggled;

    /// <summary>Raised when either panel's Appearance menu selects a theme.</summary>
    public event EventHandler<ApplicationTheme>? ThemeSelected;

    /// <summary>Raised when the initially active tab has completed its first navigation.</summary>
    internal event EventHandler? InitialBrowserReady;

    /// <summary>
    /// Raised once the initial native browser and its first navigation-tree
    /// restoration are ready. The managed Details list may still be preparing.
    /// </summary>
    internal event EventHandler? InitialNavigationReady;

    // ── Filter bar ────────────────────────────────────────────────────────────
    private readonly Panel    _filterBar;
    private readonly Label    _filterPrefix;
    private readonly TextBox  _filterTextBox;
    private readonly RoundedButton _clearBtn;
    private readonly ListView _filterListView;
    private readonly ImageList _filterIcons = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly Dictionary<string, int> _filterIconIndexes =
        new(StringComparer.OrdinalIgnoreCase);
    private TextBox? _filterRenameEditor;
    private string? _filterRenamePath;
    private bool _filterRenameIsDirectory;
    private int _hoveredFilterItemIndex = -1;
    private IReadOnlyList<FilterItemData> _filterItems = [];
    private int _filterSortColumn;
    private bool _filterSortAscending = true;
    private bool _sortFoldersWithFilesByName;
    private IReadOnlyList<ManagedDetailsListView.ColumnDefinition> _filterColumns = [];
    private bool _filterUsesNativeColumns;
    private bool _nativeFilterColumnsLoaded;
    private readonly VirtualListItemCache _filterVirtualItemCache = new();
    private string _filterText = "";
    private readonly System.Windows.Forms.Timer _filterDebounce;
    private CancellationTokenSource? _filterCancellation;

    private readonly List<ExplorerHost> _hosts = new();
    private readonly Dictionary<ExplorerHost, DirectoryItemCount> _itemCounts = new();
    private readonly SemaphoreSlim _selectedSizeGate = new(1, 1);
    private CancellationTokenSource? _selectedSizeCancellation;
    private ExplorerHost? _selectedSizeHost;
    private string[] _selectedSizePaths = [];
    private int _selectedSizeGeneration;
    private bool _selectedSizeNetworkDisabled;
    private bool _selectionUiRefreshPending;
    private int    _active         = -1;
    private string _lastActivePath = "";
    private bool _hasFocusIndicator;
    private PaneDividerEdge _focusBorderEdge = PaneDividerEdge.Right;

    /// <summary>Raised when input enters this explorer instance.</summary>
    internal event EventHandler? FocusReceived;
    internal event EventHandler<OpenInFileExplorerRequestedEventArgs>?
        OpenInFileExplorerRequested;

    /// <summary>
    /// Raised when browser or managed-list activity should temporarily accelerate
    /// the fallback refresh watchdog.
    /// </summary>
    internal event EventHandler? RefreshActivityObserved;

    public PanelView()
    {
        Padding = GetFocusBorderPadding();
        BackColor = ThemeManager.Border;
        _tabBar     = new TabBar     { Dock = DockStyle.Top };
        _pathBar    = new PathBar    { Dock = DockStyle.Top };
        _commandBar = new CommandBar { Dock = DockStyle.Top };
        _content    = new Panel      { Dock = DockStyle.None, BackColor = ThemeManager.Window };
        _itemCountLabel = new ToolStripStatusLabel("Counting…")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _selectedSizeLabel = new ToolStripStatusLabel("Selected: 0 files (0 B)")
        {
            Alignment = ToolStripItemAlignment.Right,
            TextAlign = ContentAlignment.MiddleRight,
            ToolTipText = "Total size of the selected files and folder contents",
        };
        _itemCountBar = new StatusStrip
        {
            Dock = DockStyle.Bottom,
            SizingGrip = false,
        };
        _itemCountBar.Items.AddRange([_itemCountLabel, _selectedSizeLabel]);

        // WinForms places the LAST-added DockStyle.Top control at the TOP of the
        // visual stack.  Desired order: TabBar → PathBar → CommandBar → Content.
        Controls.Add(_commandBar);  // added first → docked bottommost
        Controls.Add(_pathBar);
        Controls.Add(_tabBar);      // added last  → docked topmost
        Controls.Add(_content);     // DockStyle.None — positioned by PositionContent()

        // ── Filter bar (Bottom-docked strip inside _content) ──────────────────
        // Must be added to _content BEFORE _detailsPanel so it docks ABOVE it.
        _filterBar = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 28,   // placeholder; recalculated in ScaleFilterControls
            Visible   = false,
            BackColor = ThemeManager.Filter,
        };

        _filterPrefix = new Label
        {
            Text      = "Contains:",
            AutoSize  = false,
            Width     = 82,   // recalculated in ScaleFilterControls
            Dock      = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(6, 0, 0, 0),
            ForeColor = ThemeManager.MutedText,
        };

        _filterTextBox = new TextBox
        {
            Dock        = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor   = ThemeManager.Filter,
            ForeColor   = ThemeManager.Text,
        };
        _filterTextBox.TextChanged += OnFilterTextBoxChanged;
        _filterTextBox.KeyDown     += OnFilterTextBoxKeyDown;

        _clearBtn = new RoundedButton
        {
            Text      = "×",
            Dock      = DockStyle.Right,
            Width     = 28,   // recalculated in ScaleFilterControls
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
            TabStop   = false,
        };
        _clearBtn.FlatAppearance.BorderSize = 0;
        _clearBtn.Click += (_, _) => ClearFilter();

        _filterBar.Controls.Add(_filterTextBox); // Fill
        _filterBar.Controls.Add(_clearBtn);     // Right (added before Fill so Fill sees remaining)
        _filterBar.Controls.Add(_filterPrefix); // Left

        // Debounce: wait 180 ms after the last keystroke before repopulating the list.
        _filterDebounce = new System.Windows.Forms.Timer { Interval = 180 };
        _filterDebounce.Tick += OnFilterDebounce;

        // ── Filter overlay ListView (inside _hostContainer) ───────────────────
        // Shown on top of ExplorerHost when a filter is active. Rows are served
        // virtually from the shared directory snapshot, so a broad match does not
        // allocate a managed ListViewItem for every file before the first paint.
        _filterListView = new FilterListView
        {
            View          = View.Details,
            VirtualMode   = true,
            FullRowSelect = true,
            HideSelection = false,
            Sorting       = SortOrder.None,
            Visible       = false,
            BorderStyle   = BorderStyle.None,
            SmallImageList = _filterIcons,
        };
        UpdateFilterIconSize();
        _filterListView.DoubleClick += OnFilterListDoubleClick;
        _filterListView.KeyPress    += OnFilterListKeyPress;
        _filterListView.KeyDown     += OnFilterListKeyDown;
        _filterListView.MouseDown   += OnFilterListMouseDown;
        _filterListView.Enter += (_, _) => FocusReceived?.Invoke(this, EventArgs.Empty);
        _filterListView.MouseMove   += OnFilterListMouseMove;
        _filterListView.MouseLeave  += OnFilterListMouseLeave;
        _filterListView.ItemSelectionChanged += OnFilterListItemSelectionChanged;
        _filterListView.RetrieveVirtualItem += OnRetrieveFilterItem;
        _filterListView.DrawColumnHeader += OnFilterListDrawColumnHeader;
        _filterListView.ColumnClick += OnFilterListColumnClick;
        _filterListView.DrawItem         += OnFilterListDrawItem;
        _filterListView.DrawSubItem      += OnFilterListDrawSubItem;
        _filterListView.ColumnWidthChanged += (_, _) => CancelFilterRename();
        _filterListView.MouseWheel += (_, _) => CancelFilterRename();

        _managedDetailsListView = new ManagedDetailsListView { Visible = false };
        _localManagedColumns = _managedDetailsListView.VisibleColumnDefinitions;
        _managedDetailsListView.ItemActivatedPath += OnManagedDetailsItemActivated;
        _managedDetailsListView.ContextMenuRequested += OnManagedDetailsContextMenuRequested;
        _managedDetailsListView.BackgroundContextMenuRequested +=
            OnManagedDetailsBackgroundContextMenuRequested;
        _managedDetailsListView.RenameRequested += OnManagedDetailsRenameRequested;
        _managedDetailsListView.QuickLookRequested += OnManagedDetailsQuickLookRequested;
        _managedDetailsListView.FilterCharInput += OnFilterCharInput;
        _managedDetailsListView.CommandRequested += (_, command) => ExecuteCommand(command);
        _managedDetailsListView.Enter += (_, _) => FocusReceived?.Invoke(this, EventArgs.Empty);
        _managedDetailsListView.MouseDown += (_, _) =>
            FocusReceived?.Invoke(this, EventArgs.Empty);
        _managedDetailsListView.ItemSelectionChanged += OnManagedDetailsItemSelectionChanged;
        _managedDetailsListView.DirectoryLoadCompleted +=
            OnManagedDetailsDirectoryLoadCompleted;
        _managedDetailsListView.VisibleColumnsChanged +=
            OnManagedDetailsVisibleColumnsChanged;
        _managedDetailsListView.ColumnWidthChanged += (_, _) =>
            SyncFilterColumnWidths();
        SyncFilterColumns();
        _managedDetailsRefreshTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _managedDetailsRefreshTimer.Tick += (_, _) =>
        {
            _managedDetailsRefreshTimer.Stop();
            RefreshManagedDetailsView(force: true);
        };

        // Inner _content layout. WinForms processes docking in reverse control
        // order, so the fill control must be added first. This keeps the host
        // beside the preview pane instead of allowing the preview to cover it.
        // ItemCountBar (outermost bottom) → DetailsPanel → FilterBar (inner bottom,
        // hidden by default) → PreviewPane (right) → HostContainer (fill).
        // FilterBar lives in _content, not in _hostContainer, so ExplorerBrowser native HWNDs
        // inside _hostContainer can never paint over it regardless of z-order.
        _detailsPanel = new DetailsPanel { Dock = DockStyle.Bottom, Visible = false };
        _previewPanel = new PreviewPane { Visible = true };
        _previewSplitter = new PreviewSplitterBar();
        _previewRegion = new Panel
        {
            Dock = DockStyle.Right,
            Visible = false,
            TabStop = false,
            BackColor = ThemeManager.Surface,
        };
        _previewPanel.Controls.Add(_previewSplitter);
        _previewRegion.Controls.Add(_previewPanel);
        _previewRegion.Resize += (_, _) => LayoutPreviewRegion();
        _previewSplitter.DragStarted += OnPreviewResizeStarted;
        _previewSplitter.DragMoved += OnPreviewResizeMoved;
        _previewSplitter.DragCompleted += OnPreviewResizeCompleted;
        _hostContainer = new Panel        { Dock = DockStyle.Fill, BackColor = ThemeManager.Window };
        _networkUnavailableOverlay = new Panel
        {
            Name = "networkUnavailableOverlay",
            Dock = DockStyle.Fill,
            Visible = false,
            TabStop = true,
            BackColor = ThemeManager.Window,
        };
        _networkUnavailableMessage = new Label
        {
            Name = "networkUnavailableMessage",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            Padding = new Padding(16),
            ForeColor = ThemeManager.Text,
        };
        _networkUnavailableOverlay.Controls.Add(_networkUnavailableMessage);
        _networkUnavailableOverlay.Click += (_, _) => _networkUnavailableOverlay.Focus();
        _networkUnavailableMessage.Click += (_, _) => _networkUnavailableOverlay.Focus();
        _networkUnavailableOverlay.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F5 || e.Modifiers != Keys.None) return;
            RefreshCurrentFolder();
            e.SuppressKeyPress = true;
        };
        _explorerCommandBand = new ExplorerCommandBand { Visible = false };
        _explorerCommandBand.CommandIssued += (_, command) => ExecuteCommand(command);
        _explorerCommandBand.OpenRequested += (_, _) => OpenActiveSelection();
        _content.Controls.Add(_hostContainer);
        _content.Controls.Add(_previewRegion);
        _content.Controls.Add(_filterBar);      // DockStyle.Bottom, hidden; sits above _detailsPanel
        _content.Controls.Add(_detailsPanel);
        _content.Controls.Add(_itemCountBar);
        _content.Resize += (_, _) => ConstrainPreviewPaneWidth();

        // Both application-owned lists are overlays. The managed Details list
        // covers only the native file view; the filter list still fills the
        // entire host container and remains topmost while filtering.
        _hostContainer.Controls.Add(_managedDetailsListView);
        _filterListView.Dock = DockStyle.Fill;
        _hostContainer.Controls.Add(_filterListView);
        _hostContainer.Controls.Add(_explorerCommandBand);
        _hostContainer.Controls.Add(_networkUnavailableOverlay);

        _hostContainer.Resize += (_, _) =>
        {
            ResizeAllHosts();
            PositionExplorerCommandBand();
            PositionManagedDetailsView();
        };

        _tabBar.TabSelected       += (_, i) => ActivateTab(i);
        _tabBar.TabCloseRequested += (_, i) => CloseTab(i);
        _tabBar.AddTabRequested   += (_, _) => AddTab(CurrentPath());
        _tabBar.TabDropRequested  += OnTabDropRequested;
        _tabBar.TabDroppedOnPaneRequested += (index, targetPane, copy) =>
        {
            if (index >= 0 && index < _hosts.Count
                && !targetPane.IsDisposed && !targetPane.Disposing)
                HandleTabDrop(targetPane, index, targetPane._hosts.Count, copy);
        };
        _tabBar.TabDroppedOutsideRequested += (index, dropPosition) =>
        {
            if (index >= 0 && index < _hosts.Count)
            {
                ExplorerHost sourceTab = _hosts[index];
                OpenInFileExplorerRequested?.Invoke(this,
                    new OpenInFileExplorerRequestedEventArgs(
                        sourceTab, sourceTab.GetCurrentPath(), dropPosition));
            }
        };

        _pathBar.Navigate += (_, path) =>
        {
            ActiveHost?.NavigateTo(path);
            FocusActiveFileView();
        };

        _commandBar.CommandIssued     += OnCommandIssued;
        _commandBar.ViewDropDownOpening += (_, _) => UpdateCommandBarToggles();
        _commandBar.ThemeSelected += (_, theme) => ThemeSelected?.Invoke(this, theme);
        Enter += (_, _) => FocusReceived?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyTheme()
    {
        BackColor = _hasFocusIndicator ? ThemeManager.Accent : ThemeManager.Border;
        ForeColor = ThemeManager.Text;
        _tabBar.ApplyTheme();
        _pathBar.ApplyTheme();
        _content.BackColor = ThemeManager.Window;
        _hostContainer.BackColor = ThemeManager.Window;
        _networkUnavailableOverlay.BackColor = ThemeManager.Window;
        _networkUnavailableMessage.ForeColor = ThemeManager.Text;
        _detailsPanel.ApplyTheme();
        _previewRegion.BackColor = ThemeManager.Surface;
        _previewPanel.ApplyTheme();
        _previewSplitter.Invalidate();
        _filterBar.BackColor = ThemeManager.Filter;
        _filterPrefix.BackColor = ThemeManager.Filter;
        _filterPrefix.ForeColor = ThemeManager.MutedText;
        _filterTextBox.BackColor = ThemeManager.Filter;
        _filterTextBox.ForeColor = ThemeManager.Text;
        _clearBtn.BackColor = ThemeManager.Filter;
        _clearBtn.ForeColor = ThemeManager.Text;
        _filterListView.BackColor = ThemeManager.Window;
        _filterListView.ForeColor = ThemeManager.Text;
        if (_filterRenameEditor is { } renameEditor)
        {
            renameEditor.BackColor = ThemeManager.Window;
            renameEditor.ForeColor = ThemeManager.Text;
        }
        _filterListView.Invalidate();
        _managedDetailsListView.ApplyTheme();
        _explorerCommandBand.ApplyTheme();
        ThemeManager.ApplyToolStrip(_itemCountBar);
        _commandBar.SetThemeSelection(ThemeManager.Current);
        _commandBar.ApplyTheme();
        PositionExplorerCommandBand();
        Invalidate(true);
    }

    internal void SetStartWithWindowsChecked(bool enabled) =>
        _commandBar.SetStartWithWindowsChecked(enabled);

    internal void SetMinimizeToTrayChecked(bool enabled) =>
        _commandBar.SetMinimizeToTrayChecked(enabled);

    /// <summary>Sets which edge of the pane forms the shared focus divider.</summary>
    internal void SetFocusBorderEdge(PaneDividerEdge edge)
    {
        if (_focusBorderEdge == edge) return;

        _focusBorderEdge = edge;
        Padding = GetFocusBorderPadding();
        PerformLayout();
        Invalidate();
    }

    /// <summary>Shows whether this is the explorer instance that owns input focus.</summary>
    internal void SetFocusIndicator(bool hasFocus)
    {
        if (_hasFocusIndicator == hasFocus) return;

        _hasFocusIndicator = hasFocus;
        BackColor = hasFocus ? ThemeManager.Accent : ThemeManager.Border;
        _managedDetailsListView.SetInputFocus(hasFocus);
        _filterListView.Invalidate();
        Invalidate();
    }

    private Padding GetFocusBorderPadding() => _focusBorderEdge switch
    {
        PaneDividerEdge.Left => new Padding(FocusBorderThickness, 0, 0, 0),
        PaneDividerEdge.Right => new Padding(0, 0, FocusBorderThickness, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(_focusBorderEdge)),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _selectedSizeCancellation?.Cancel();
            _selectedSizeCancellation?.Dispose();
            _selectedSizeCancellation = null;
            _filterCancellation?.Cancel();
            _filterCancellation?.Dispose();
            _filterDebounce.Dispose();
            CancelFilterRename();
            _filterIcons.Dispose();
            _managedDetailsRefreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Recreates already-open native shell views for a live theme change.</summary>
    public void RecreateShellViewsForTheme()
    {
        foreach (var host in _hosts)
            host.RecreateForTheme();
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        PositionContent();
    }

    // _content uses DockStyle.None so WinForms never gives it the full panel area.
    // We position it manually to start just below the lowest bar control.
    private void PositionContent()
    {
        if (_content == null) return;
        int barsBottom = Math.Max(
            Math.Max(_tabBar.Bottom, _pathBar.Bottom),
            _commandBar.Bottom);
        Rectangle displayBounds = DisplayRectangle;
        _content.SetBounds(displayBounds.Left, barsBottom,
            Math.Max(1, displayBounds.Width),
            Math.Max(1, displayBounds.Bottom - barsBottom));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public string CurrentPath()
        => ActiveHost?.GetCurrentPath() ?? @"C:\";

    internal string? AvailableFileOperationDestination
    {
        get
        {
            if (ActiveHost is not { } host
                || host.NetworkStatus is NetworkPathStatus.Checking
                    or NetworkPathStatus.Unavailable)
                return null;

            string path = host.GetCurrentPath();
            return ExplorerHost.IsNetworkPath(path) || Directory.Exists(path)
                ? path : null;
        }
    }

    /// <summary>Navigates the active tab to the given path (called by MainForm for Mirror).</summary>
    public void NavigateTo(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        ActiveHost?.NavigateTo(path);
        FocusActiveFileView();
    }

    public List<string> GetAllPaths()
        => _hosts.Select(h => h.GetCurrentPath()).ToList();

    internal ExplorerHost GetTabHost(int index) => _hosts[index];

    internal void SetOpenExplorerWhenTabDroppedOutside(bool enabled) =>
        _tabBar.OpenExplorerWhenTabDroppedOutside = enabled;

    internal void SetSortFoldersWithFilesByName(bool enabled)
    {
        if (_sortFoldersWithFilesByName == enabled) return;
        _sortFoldersWithFilesByName = enabled;
        _managedDetailsListView.SetSortFoldersWithFilesByName(enabled);
        if (_filterListView.Visible)
            ResortFilterItemsPreservingSelection();
        if (ActiveHost is { } host
            && ExplorerHost.IsNetworkPath(host.GetCurrentPath()))
        {
            _managedDetailsPath = string.Empty;
            PositionManagedDetailsView();
            RefreshManagedDetailsView(force: true);
        }
    }

    public void SetPathHistory(IEnumerable<string>? paths) => _pathBar.SetHistory(paths);

    public List<string> GetPathHistory() => _pathBar.GetHistory();

    /// <summary>The independently selected preview pane width for this explorer.</summary>
    internal int PreviewPaneWidth => _preferredPreviewPaneWidth;

    internal void SetPreviewPaneWidth(int width)
    {
        _preferredPreviewPaneWidth = Math.Max(MinimumPreviewPaneWidth, width);
        ConstrainPreviewPaneWidth();
    }

    private void OnPreviewResizeStarted(object? sender, EventArgs e)
    {
        _previewDragStartWidth = Math.Max(
            MinimumPreviewPaneWidth,
            _previewRegion.ClientSize.Width - PreviewSplitterDeviceWidth);
    }

    private void OnPreviewResizeMoved(object? sender, int horizontalDelta)
    {
        int maximumWidth = MaximumPreviewPaneWidth;
        if (maximumWidth < MinimumPreviewPaneWidth)
            return;

        _preferredPreviewPaneWidth = Math.Clamp(
            _previewDragStartWidth - horizontalDelta,
            MinimumPreviewPaneWidth,
            maximumWidth);
        ConstrainPreviewPaneWidth();
    }

    private void OnPreviewResizeCompleted(object? sender, EventArgs e)
    {
        PreviewPaneWidthChanged?.Invoke(this, _preferredPreviewPaneWidth);
    }

    private void ConstrainPreviewPaneWidth()
    {
        int maximumWidth = MaximumPreviewPaneWidth;
        if (maximumWidth < MinimumPreviewPaneWidth)
            return;

        int constrainedWidth = Math.Clamp(
            _preferredPreviewPaneWidth,
            MinimumPreviewPaneWidth,
            maximumWidth);
        int regionWidth = constrainedWidth + PreviewSplitterDeviceWidth;
        if (_previewRegion.Width != regionWidth)
            _previewRegion.Width = regionWidth;
        LayoutPreviewRegion();
    }

    private int MaximumPreviewPaneWidth => _content.ClientSize.Width
        - PreviewSplitterDeviceWidth
        - MinimumFileViewWidth;

    private int PreviewSplitterDeviceWidth => LogicalToDeviceUnits(PreviewSplitterWidth);

    private void LayoutPreviewRegion()
    {
        int splitterWidth = Math.Min(PreviewSplitterDeviceWidth, _previewRegion.ClientSize.Width);
        _previewPanel.Bounds = _previewRegion.ClientRectangle;
        _previewPanel.SetContentInsetLeft(splitterWidth);
        _previewSplitter.Bounds = new Rectangle(
            0,
            0,
            splitterWidth,
            _previewPanel.ClientSize.Height);
        _previewSplitter.BringToFront();
    }

    internal void RefreshCurrentFolder()
    {
        if (ActiveHost is not { } activeHost) return;

        if (activeHost.NetworkStatus == NetworkPathStatus.Unavailable)
            _networkRetryHost = activeHost;
        DirectorySnapshotCache.Invalidate(activeHost.GetCurrentPath());
        activeHost.RefreshShellView();
        if (ReferenceEquals(_networkRetryHost, activeHost)
            && activeHost.NetworkStatus == NetworkPathStatus.Checking)
            _networkUnavailableOverlay.Refresh();
        activeHost.ScheduleItemCountRefresh();
        RefreshManagedDetailsView(force: true);

        if (_filterListView.Visible && !string.IsNullOrEmpty(_filterText))
            RestartFilterDebounce();
    }

    internal async Task RestoreActiveNavigationPaneAsync(
        CancellationToken cancellationToken = default)
    {
        if (ActiveHost is not { } host) return;

        string path = host.GetCurrentPathForPolling();
        if (!string.IsNullOrWhiteSpace(path)
            && host.NetworkStatus is not (NetworkPathStatus.Checking
                or NetworkPathStatus.Unavailable))
            await host.RestoreNavigationPaneForStartupAsync(
                path, cancellationToken);
    }

    /// <summary>Called from MainForm.OnShown after the panel has its final layout dimensions.</summary>
    public void Launch(List<string> paths)
    {
        if (paths.Count == 0) paths = new List<string> { @"C:\" };
        foreach (string p in paths) AddHostInternal(p);
        ActivateTab(0);
        UpdateCommandBarToggles();
    }

    /// <summary>
    /// Runs the fallback refresh watchdog. Normal navigation, selection, layout,
    /// and filesystem updates arrive through events; this catches missed native
    /// notifications without synchronously entering the browser STA.
    /// </summary>
    public void PollPath()
    {
        if (_active < 0) return;
        ExplorerHost host = _hosts[_active];
        if (host.NetworkStatus is not (NetworkPathStatus.Checking
            or NetworkPathStatus.Unavailable))
            host.RequestSelectionSnapshotRefresh();
        string p = host.GetCurrentPathForPolling();
        _pathBar.SetPath(p);
        _tabBar.UpdateLabel(_active, FolderLabel(p), p);

        RefreshSelectionDependentUi(host);

        if (!string.IsNullOrEmpty(p) && p != _lastActivePath)
        {
            _lastActivePath = p;
            _itemCounts.Remove(host);
            UpdateItemCountStatus(host, null);
            if (host.NetworkStatus is not (NetworkPathStatus.Checking
                or NetworkPathStatus.Unavailable))
            {
                host.EnsureColumnHeaders();
                host.SyncNavigationPane(p);
            }
            host.NotifyItemCountPathChanged();
        }

        PositionExplorerCommandBand();
        PositionManagedDetailsView();
        RefreshManagedDetailsView();
        host.RefreshItemCountStatus();
        host.CheckNetworkAvailabilityIfDue();
    }

    private void RefreshSelectionDependentUi(ExplorerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ReferenceEquals(host, ActiveHost)) return;

        if (_detailsPanel.Visible || _previewRegion.Visible)
        {
            string? selectedPath = GetSelectedPathForPreview();
            if (_detailsPanel.Visible) _detailsPanel.ShowItem(selectedPath);
            if (_previewRegion.Visible) _previewPanel.Preview(selectedPath);
        }

        UpdateSelectedSizeStatus(host);
    }

    private void QueueManagedSelectionUiRefresh()
    {
        RefreshActivityObserved?.Invoke(this, EventArgs.Empty);
        if (_selectionUiRefreshPending || !IsHandleCreated || IsDisposed || Disposing)
            return;

        _selectionUiRefreshPending = true;
        BeginInvoke((MethodInvoker)(() =>
        {
            _selectionUiRefreshPending = false;
            if (ActiveHost is { } host)
                RefreshSelectionDependentUi(host);
        }));
    }

    private void OnFilterListItemSelectionChanged(
        object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        RedrawFilterItem(e.ItemIndex);
        QueueManagedSelectionUiRefresh();
    }

    private void OnManagedDetailsItemSelectionChanged(
        object? sender, ListViewItemSelectionChangedEventArgs e) =>
        QueueManagedSelectionUiRefresh();

    private void OnManagedDetailsDirectoryLoadCompleted(
        object? sender, string path)
    {
        if (ActiveHost is { } currentHost
            && string.Equals(path, currentHost.GetCurrentPathForPolling(),
                StringComparison.OrdinalIgnoreCase))
            PositionManagedDetailsView();

        if (!_initialBrowserNavigationReady
            || _initialBrowserReadyRaised
            || ActiveHost is not { } host
            || !string.Equals(path, host.GetCurrentPathForPolling(),
                StringComparison.OrdinalIgnoreCase))
            return;

        TryRaiseInitialBrowserReady(host);
    }

    // ── Tab management ────────────────────────────────────────────────────────

    private void AddTab(string path)
    {
        AddHostInternal(path);
        ActivateTab(_hosts.Count - 1);
    }

    private void AddHostInternal(string path)
    {
        int w = Math.Max(1, _hostContainer.ClientSize.Width);
        int h = Math.Max(1, _hostContainer.ClientSize.Height);
        var host = new ExplorerHost(path);
        host.SetBounds(0, 0, w, h);
        host.Visible = false;
        AttachHostEvents(host);
        _hostContainer.Controls.Add(host);
        _hosts.Add(host);
    }

    private void AttachHostEvents(ExplorerHost host)
    {
        host.FilterCharInput      += OnFilterCharInput;
        host.FilterBackspaceTyped += OnFilterBackspaceTyped;
        host.FilterEscapePressed += OnHostFilterEscapePressed;
        host.ApplicationShortcutRequested += OnHostApplicationShortcutRequested;
        host.ShellMouseDown += OnHostShellMouseDown;
        host.ShellFocused += OnHostShellFocused;
        host.NavigationContextMenuRequested += OnHostNavigationContextMenuRequested;
        host.FileContextMenuRequested += OnHostFileContextMenuRequested;
        host.QuickLookUnavailable += OnHostQuickLookUnavailable;
        host.ItemCountChanged += OnHostItemCountChanged;
        host.FolderViewStateChanged += OnHostFolderViewStateChanged;
        host.NavigationChanged += OnHostNavigationChanged;
        host.NetworkPathStatusChanged += OnHostNetworkPathStatusChanged;
        host.NavigationPaneVisibilityChanged += OnHostNavigationPaneVisibilityChanged;
        host.SelectionChanged += OnHostSelectionChanged;
        host.InitialNavigationCompleted += OnHostInitialNavigationCompleted;
    }

    private void DetachHostEvents(ExplorerHost host)
    {
        if (ReferenceEquals(host, _networkRetryHost))
            _networkRetryHost = null;
        host.SetItemCountMonitoringActive(false);
        host.FilterCharInput -= OnFilterCharInput;
        host.FilterBackspaceTyped -= OnFilterBackspaceTyped;
        host.FilterEscapePressed -= OnHostFilterEscapePressed;
        host.ApplicationShortcutRequested -= OnHostApplicationShortcutRequested;
        host.ShellMouseDown -= OnHostShellMouseDown;
        host.ShellFocused -= OnHostShellFocused;
        host.NavigationContextMenuRequested -= OnHostNavigationContextMenuRequested;
        host.FileContextMenuRequested -= OnHostFileContextMenuRequested;
        host.QuickLookUnavailable -= OnHostQuickLookUnavailable;
        host.ItemCountChanged -= OnHostItemCountChanged;
        host.FolderViewStateChanged -= OnHostFolderViewStateChanged;
        host.NavigationChanged -= OnHostNavigationChanged;
        host.NetworkPathStatusChanged -= OnHostNetworkPathStatusChanged;
        host.NavigationPaneVisibilityChanged -= OnHostNavigationPaneVisibilityChanged;
        host.SelectionChanged -= OnHostSelectionChanged;
        _itemCounts.Remove(host);
        host.InitialNavigationCompleted -= OnHostInitialNavigationCompleted;
    }

    private void OnHostFilterEscapePressed(object? sender, EventArgs e) => ClearFilter();

    private void OnHostApplicationShortcutRequested(object? sender, CommandBar.Cmd command) =>
        ExecuteCommand(command);

    private void OnHostShellMouseDown(object? sender, EventArgs e)
    {
        _pathBar.DismissHistoryPopup();
        FocusReceived?.Invoke(this, EventArgs.Empty);
        RefreshActivityObserved?.Invoke(this, EventArgs.Empty);
    }

    private void OnHostShellFocused(object? sender, EventArgs e) =>
        FocusReceived?.Invoke(this, EventArgs.Empty);

    private void OnHostNavigationChanged(object? sender, EventArgs e)
    {
        if (sender is not ExplorerHost host || !ReferenceEquals(host, ActiveHost))
            return;

        RefreshActivityObserved?.Invoke(this, EventArgs.Empty);
        PollPath();
    }

    private void OnHostNetworkPathStatusChanged(object? sender, EventArgs e)
    {
        if (sender is not ExplorerHost host) return;
        if (ReferenceEquals(host, _networkRetryHost)
            && host.NetworkStatus != NetworkPathStatus.Checking)
            _networkRetryHost = null;
        if (ReferenceEquals(host, ActiveHost))
        {
            UpdateNetworkUnavailableOverlay(host);
            if (host.NetworkStatus == NetworkPathStatus.Available)
                RefreshManagedDetailsView(force: true);
            else
            {
                _managedDetailsPath = string.Empty;
                _managedDetailsListView.CancelDirectoryLoad();
            }
            PositionManagedDetailsView();
        }
    }

    private void OnHostNavigationPaneVisibilityChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActiveHost))
            UpdateCommandBarToggles();
    }

    private void OnHostSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not ExplorerHost host || !ReferenceEquals(host, ActiveHost))
            return;

        RefreshActivityObserved?.Invoke(this, EventArgs.Empty);
        RefreshSelectionDependentUi(host);
    }

    private void OnHostNavigationContextMenuRequested(
        object? sender, NavigationContextMenuEventArgs e)
    {
        if (sender is not ExplorerHost host
            || !ReferenceEquals(host, ActiveHost)
            || !Directory.Exists(e.Path))
            return;

        ShowShellContextMenu([e.Path], host,
            host.PointToClient(e.ScreenLocation), allowInlineRename: false,
            renameAction: host.BeginNavigationRename);
    }

    private void OnHostFileContextMenuRequested(
        object? sender, FileContextMenuEventArgs e)
    {
        if (sender is not ExplorerHost host
            || !ReferenceEquals(host, ActiveHost))
            return;

        ShowShellContextMenu(e.Paths, host,
            host.PointToClient(e.ScreenLocation), allowInlineRename: false,
            renameAction: host.Rename);
    }

    private void OnHostQuickLookUnavailable(object? sender, QuickLookFailure failure) =>
        ShowQuickLookUnavailable(failure);

    private void OnHostItemCountChanged(object? sender, DirectoryItemCount count)
    {
        if (sender is not ExplorerHost host) return;

        _itemCounts[host] = count;
        if (ReferenceEquals(host, ActiveHost))
            UpdateItemCountStatus(host, count);
    }

    private void OnHostFolderViewStateChanged(
        object? sender, ExplorerFolderViewState state)
    {
        if (sender is not ExplorerHost host
            || !ReferenceEquals(host, ActiveHost))
            return;

        _managedDetailsOverlaySuppressed = state.ViewMode != 4;
        PositionManagedDetailsView();
        if (!_managedDetailsOverlaySuppressed)
            RefreshManagedDetailsView(force: true);
    }

    private async void OnHostInitialNavigationCompleted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ActiveHost)
            || sender is not ExplorerHost host)
            return;

        // The path poller can observe the saved path before ExplorerBrowser has
        // created its namespace tree. Restore again at the browser-ready boundary
        // so the saved folder's complete ancestor chain is expanded and selected.
        string path = host.GetCurrentPathForPolling();
        if (!string.IsNullOrWhiteSpace(path)
            && host.NetworkStatus is not (NetworkPathStatus.Checking
                or NetworkPathStatus.Unavailable))
        {
            // Mark the browser-ready path as observed before queuing retries so
            // the normal poller cannot cancel the startup restoration.
            _lastActivePath = path;
            host.EnsureColumnHeaders();
            await host.RestoreNavigationPaneForStartupAsync(path);
        }

        if (IsDisposed || Disposing || !ReferenceEquals(host, ActiveHost))
            return;

        _initialBrowserNavigationReady = true;
        InitialNavigationReady?.Invoke(this, EventArgs.Empty);
        TryRaiseInitialBrowserReady(host);
    }

    private void TryRaiseInitialBrowserReady(ExplorerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_initialBrowserReadyRaised || !_initialBrowserNavigationReady)
            return;

        string path = host.GetCurrentPathForPolling();
        bool waitsForManagedDetails = !_managedDetailsOverlaySuppressed
            && !ExplorerHost.IsNetworkPath(path)
            && Directory.Exists(path);
        if (waitsForManagedDetails
            && !_managedDetailsListView.HasCompletedLoad(path))
            return;

        _initialBrowserReadyRaised = true;
        InitialBrowserReady?.Invoke(this, EventArgs.Empty);
    }

    private void CloseTab(int index)
    {
        if (_hosts.Count <= 1) return;

        var host = _hosts[index];
        _hosts.RemoveAt(index);
        _hostContainer.Controls.Remove(host);
        DetachHostEvents(host);
        host.Dispose();

        ActivateAfterTabRemoved(index);
    }

    private void ActivateAfterTabRemoved(int index)
    {
        int newActive = _active;
        if (_active == index)
            newActive = Math.Min(index, _hosts.Count - 1);
        else if (_active > index)
            newActive = _active - 1;

        _active = -1;
        ActivateTab(newActive);
    }

    internal void CloseTabAfterExplorerOpened(ExplorerHost sourceTab)
    {
        ArgumentNullException.ThrowIfNull(sourceTab);
        if (IsDisposed || Disposing || _hosts.Count <= 1) return;

        // The user may select or duplicate tabs while File Explorer starts.
        int index = _hosts.IndexOf(sourceTab);
        if (index >= 0)
            CloseTab(index);
    }

    private void OnTabDropRequested(object? sender, TabDropRequestedEventArgs e)
    {
        if (e.Source.Parent is not PanelView sourcePanel) return;
        sourcePanel.HandleTabDrop(this, e.SourceIndex, e.TargetIndex, e.Copy);
    }

    internal void HandleTabDrop(PanelView target, int sourceIndex,
        int targetInsertionIndex, bool copy = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (sourceIndex < 0 || sourceIndex >= _hosts.Count
            || target.IsDisposed || target.Disposing)
            return;

        bool samePane = ReferenceEquals(this, target);
        int insertionIndex = TabInsertionIndex(
            samePane, target._hosts.Count, targetInsertionIndex);
        if (samePane || copy)
        {
            target.AddTabAt(_hosts[sourceIndex].GetCurrentPath(), insertionIndex);
            return;
        }

        // A pane always keeps one tab. Moving its last tab leaves it at the
        // normal starting location.
        if (_hosts.Count == 1)
            AddHostInternal(@"C:\");

        ExplorerHost host = _hosts[sourceIndex];
        _hosts.RemoveAt(sourceIndex);
        _hostContainer.Controls.Remove(host);
        DetachHostEvents(host);
        ActivateAfterTabRemoved(sourceIndex);

        target.ClearFilter();
        target.AttachHostEvents(host);
        target._hostContainer.Controls.Add(host);
        target._hosts.Insert(insertionIndex, host);
        if (target._active >= insertionIndex) target._active++;
        target.ActivateTab(insertionIndex);
        target.FocusActiveFileView();
    }

    internal static int TabInsertionIndex(
        bool samePane, int targetCount, int insertionIndex) =>
        samePane ? targetCount : Math.Clamp(insertionIndex, 0, targetCount);

    private void AddTabAt(string path, int insertionIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ClearFilter();
        AddHostInternal(path);
        ExplorerHost addedHost = _hosts[^1];
        _hosts.RemoveAt(_hosts.Count - 1);
        int targetIndex = Math.Clamp(insertionIndex, 0, _hosts.Count);
        _hosts.Insert(targetIndex, addedHost);
        if (_active >= targetIndex) _active++;
        ActivateTab(targetIndex);
    }

    private void ActivateTab(int index)
    {
        if (index < 0 || index >= _hosts.Count) return;

        ClearFilter(); // clear outgoing tab's filter before switching

        if (_active >= 0 && _active < _hosts.Count && _active != index)
        {
            _hosts[_active].SetItemCountMonitoringActive(false);
            _hosts[_active].Visible = false;
        }

        _active = index;
        var host = _hosts[index];
        _managedDetailsOverlaySuppressed = host.CurrentViewMode != 4;
        _lastActivePath = host.GetCurrentPath();
        ResetSelectedSizeStatus(host);
        UpdateItemCountStatus(host,
            _itemCounts.TryGetValue(host, out DirectoryItemCount count) ? count : null);

        host.SetBounds(0, 0,
            Math.Max(1, _hostContainer.ClientSize.Width),
            Math.Max(1, _hostContainer.ClientSize.Height));
        host.Visible = true;
        host.CreateControl();
        host.LaunchExplorer();
        host.SetItemCountMonitoringActive(true);

        UpdateNetworkUnavailableOverlay(host);

        _pathBar.SetPath(host.GetCurrentPath());
        RefreshTabBar();
        ApplyManagedDetailsDisplaySettings();
        PositionExplorerCommandBand();
        PositionManagedDetailsView();
        RefreshManagedDetailsView(force: true);
        UpdateCommandBarToggles();
    }

    private void UpdateNetworkUnavailableOverlay(ExplorerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ReferenceEquals(host, ActiveHost)) return;

        NetworkPathStatus status = host.NetworkStatus;
        bool show = status is NetworkPathStatus.Checking
            or NetworkPathStatus.Unavailable;
        _networkUnavailableOverlay.Visible = show;
        if (!show) return;

        if (_filterBar.Visible)
            ClearFilter();

        _networkUnavailableMessage.Text = status switch
        {
            NetworkPathStatus.Checking when ReferenceEquals(host, _networkRetryHost)
                => $"Retrying network location…\n{host.GetCurrentPath()}",
            NetworkPathStatus.Checking => "Checking network location…",
            _ => $"Network location unavailable.\n{host.GetCurrentPath()}\n\nPress F5 to try again.",
        };
        _managedDetailsListView.Visible = false;
        _filterListView.Visible = false;
        _explorerCommandBand.Visible = false;
        _networkUnavailableOverlay.BringToFront();
    }

    private void RefreshTabBar()
    {
        var paths  = _hosts.Select(h => h.GetCurrentPath()).ToList();
        var labels = paths.Select(FolderLabel).ToList();
        _tabBar.SetTabs(labels, paths, _active);
    }

    private void ResizeAllHosts()
    {
        int w = Math.Max(1, _hostContainer.ClientSize.Width);
        int h = Math.Max(1, _hostContainer.ClientSize.Height);
        foreach (var host in _hosts)
            host.SetBounds(0, 0, w, h);
    }

    private bool IsManagedDetailsViewActive => _managedDetailsListView.Visible;

    private void UpdateItemCountStatus(ExplorerHost host, DirectoryItemCount? count)
    {
        ArgumentNullException.ThrowIfNull(host);
        _itemCountLabel.Text = ExplorerHost.IsNetworkPath(host.GetCurrentPath())
            ? "Network location — item count disabled"
            : count is { } value
            ? ExplorerHost.FormatItemCountStatus(value.FileCount, value.FolderCount)
            : "Counting…";
    }

    private void UpdateSelectedSizeStatus(ExplorerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ReferenceEquals(host, ActiveHost)) return;

        if (ExplorerHost.IsNetworkPath(host.GetCurrentPath()))
        {
            if (!_selectedSizeNetworkDisabled || !ReferenceEquals(host, _selectedSizeHost))
            {
                CancelSelectedSizeCalculation();
                _selectedSizeHost = host;
                _selectedSizePaths = [];
                _selectedSizeNetworkDisabled = true;
                _selectedSizeLabel.Text = "Selected: —";
                _selectedSizeLabel.ToolTipText =
                    "Selected-size calculation is disabled on network locations";
            }
            return;
        }

        _selectedSizeNetworkDisabled = false;
        _selectedSizeLabel.ToolTipText =
            "Total size of the selected files and folder contents";
        string[] selectedPaths = GetSelectedPaths(host)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ReferenceEquals(host, _selectedSizeHost)
            && _selectedSizePaths.SequenceEqual(
                selectedPaths, StringComparer.OrdinalIgnoreCase))
            return;

        CancelSelectedSizeCalculation();
        _selectedSizeHost = host;
        _selectedSizePaths = selectedPaths;
        int generation = ++_selectedSizeGeneration;

        if (selectedPaths.Length == 0)
        {
            _selectedSizeLabel.Text = "Selected: 0 files (0 B)";
            return;
        }

        _selectedSizeLabel.Text = $"Selected: {selectedPaths.Length} " +
            $"{(selectedPaths.Length == 1 ? "item" : "items")} (Calculating…)";
        var cancellation = new CancellationTokenSource();
        _selectedSizeCancellation = cancellation;
        _ = CalculateSelectedSizeAsync(host, selectedPaths, generation, cancellation);
    }

    private IReadOnlyList<string> GetSelectedPaths(ExplorerHost host)
    {
        if (_filterListView.Visible)
        {
            return _filterListView.SelectedIndices
                .Cast<int>()
                .Select(GetFilterPathAt)
                .OfType<string>()
                .ToArray();
        }

        return IsManagedDetailsViewActive
            ? _managedDetailsListView.SelectedPaths
            : host.GetSelectedFileSystemPathsSnapshot();
    }

    private async Task CalculateSelectedSizeAsync(
        ExplorerHost host,
        string[] paths,
        int generation,
        CancellationTokenSource cancellation)
    {
        bool gateEntered = false;
        try
        {
            await _selectedSizeGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            SelectedSizeResult result = await Task.Run(
                () => SelectionSizeCalculator.Calculate(paths, cancellation.Token),
                cancellation.Token);

            if (cancellation.IsCancellationRequested
                || IsDisposed
                || Disposing
                || generation != _selectedSizeGeneration
                || !ReferenceEquals(host, ActiveHost))
                return;

            _selectedSizeLabel.Text = SelectionSizeCalculator.FormatSelection(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer selection owns the status label now.
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(CalculateSelectedSizeAsync),
                "Could not calculate the selected-item size.");
            if (!IsDisposed
                && !Disposing
                && generation == _selectedSizeGeneration
                && ReferenceEquals(host, ActiveHost))
            {
                _selectedSizeLabel.Text = "Selected size: unavailable";
            }
        }
        finally
        {
            if (gateEntered) _selectedSizeGate.Release();
            if (ReferenceEquals(_selectedSizeCancellation, cancellation))
                _selectedSizeCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ResetSelectedSizeStatus(ExplorerHost host)
    {
        CancelSelectedSizeCalculation();
        _selectedSizeHost = null;
        _selectedSizePaths = [];
        bool isNetworkPath = ExplorerHost.IsNetworkPath(host.GetCurrentPath());
        _selectedSizeNetworkDisabled = isNetworkPath;
        _selectedSizeLabel.Text = isNetworkPath
            ? "Selected: —"
            : "Selected: 0 files (0 B)";
        _selectedSizeLabel.ToolTipText = isNetworkPath
            ? "Selected-size calculation is disabled on network locations"
            : "Total size of the selected files and folder contents";
    }

    private void CancelSelectedSizeCalculation()
    {
        _selectedSizeCancellation?.Cancel();
        _selectedSizeCancellation = null;
        _selectedSizeGeneration++;
    }

    private void PositionExplorerCommandBand()
    {
        ExplorerHost? host = ActiveHost;
        if (_filterListView.Visible
            || host is null
            || !host.Visible
            || host.NetworkStatus is NetworkPathStatus.Checking
                or NetworkPathStatus.Unavailable
            || !host.TryGetFileViewBounds(out Rectangle fileViewBounds)
            || fileViewBounds.Top <= 0)
        {
            _explorerCommandBand.Visible = false;
            return;
        }

        Point hostLocation = _hostContainer.PointToClient(host.PointToScreen(Point.Empty));
        int bandHeight = Math.Min(fileViewBounds.Top, host.ClientSize.Height);
        _explorerCommandBand.ApplyDpi(host.DeviceDpi);
        _explorerCommandBand.Bounds = new Rectangle(
            hostLocation.X,
            hostLocation.Y,
            Math.Max(1, host.ClientSize.Width),
            Math.Max(1, bandHeight));
        _explorerCommandBand.Visible = true;
        _explorerCommandBand.BringToFront();
    }

    private void PositionManagedDetailsView()
    {
        ExplorerHost? host = ActiveHost;
        string path = host?.GetCurrentPath() ?? string.Empty;
        bool isNetworkPath = ExplorerHost.IsNetworkPath(path);
        if (_managedDetailsOverlaySuppressed
            || _filterListView.Visible
            || host is null
            || (isNetworkPath
                ? !_sortFoldersWithFilesByName
                    || host.NetworkStatus != NetworkPathStatus.Available
                    || !_managedDetailsListView.HasCompletedLoad(path)
                : !Directory.Exists(path)
                    || !_managedDetailsListView.HasCompletedLoad(path))
            || !host.TryGetFileViewBounds(out Rectangle nativeBounds))
        {
            _managedDetailsListView.Visible = false;
            return;
        }

        Point screenLocation = host.PointToScreen(nativeBounds.Location);
        Point overlayLocation = _hostContainer.PointToClient(screenLocation);
        _managedDetailsListView.Bounds = new Rectangle(overlayLocation, nativeBounds.Size);
        _managedDetailsListView.Visible = true;
        _managedDetailsListView.BringToFront();
    }

    private void RefreshManagedDetailsView(bool force = false)
    {
        if (ActiveHost is not { } host) return;

        string path = host.GetCurrentPath();
        bool isNetworkPath = ExplorerHost.IsNetworkPath(path);
        if (isNetworkPath && (!_sortFoldersWithFilesByName
            || host.NetworkStatus != NetworkPathStatus.Available
            || _managedDetailsOverlaySuppressed))
        {
            _managedDetailsPath = string.Empty;
            _managedDetailsListView.Visible = false;
            _managedDetailsListView.CancelDirectoryLoad();
            return;
        }
        if (!isNetworkPath && !Directory.Exists(path))
        {
            _managedDetailsPath = string.Empty;
            return;
        }

        if (!force && string.Equals(path, _managedDetailsPath, StringComparison.OrdinalIgnoreCase))
            return;

        _managedDetailsPath = path;
        if (isNetworkPath)
        {
            _ = ShowNetworkManagedDetailsAsync(host, path);
            return;
        }

        ++_networkManagedColumnsGeneration;
        if (_usingNetworkManagedColumns)
        {
            _usingNetworkManagedColumns = false;
            _managedDetailsListView.SetVisibleColumns(_localManagedColumns);
        }
        _managedDetailsListView.ShowDirectory(path, host.HiddenItemsVisible);
    }

    private async Task ShowNetworkManagedDetailsAsync(
        ExplorerHost host, string path)
    {
        int generation = ++_networkManagedColumnsGeneration;
        IReadOnlyList<ExplorerShellColumn> nativeColumns;
        try
        {
            nativeColumns = await host.GetVisibleShellColumnsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ShowNetworkManagedDetailsAsync));
            nativeColumns = [];
        }

        NativeMethods.SORTCOLUMN? nativeSort = null;
        if (nativeColumns.Count > 0)
        {
            try { nativeSort = await host.GetCurrentShellSortColumnAsync(); }
            catch (Exception ex)
            {
                AppLog.Debug(ex, nameof(ShowNetworkManagedDetailsAsync));
            }
        }

        if (generation != _networkManagedColumnsGeneration
            || IsDisposed
            || !ReferenceEquals(host, ActiveHost)
            || !_sortFoldersWithFilesByName
            || host.NetworkStatus != NetworkPathStatus.Available
            || !string.Equals(path, host.GetCurrentPath(),
                StringComparison.OrdinalIgnoreCase))
            return;

        if (nativeColumns.Count > 0)
        {
            IReadOnlyList<ManagedDetailsListView.ColumnDefinition> columns =
                MapNativeFilterColumns(nativeColumns, host.DeviceDpi);
            _usingNetworkManagedColumns = true;
            _managedDetailsListView.SetVisibleColumns(columns);
            if (nativeSort is { } sort)
            {
                int sortIndex = Enumerable.Range(0, nativeColumns.Count)
                    .FirstOrDefault(index =>
                        nativeColumns[index].Key.Equals(sort.propkey), -1);
                if (sortIndex >= 0)
                    _managedDetailsListView.SetSortState(
                        columns[sortIndex].Id, sort.direction >= 0);
            }
        }
        _managedDetailsListView.ShowDirectory(path, host.HiddenItemsVisible);
    }

    private void ScheduleManagedDetailsRefresh()
    {
        _managedDetailsRefreshTimer.Stop();
        _managedDetailsRefreshTimer.Start();
    }

    private void FocusActiveFileView()
    {
        if (ActiveHost?.NetworkStatus is NetworkPathStatus.Checking
            or NetworkPathStatus.Unavailable)
        {
            _networkUnavailableOverlay.Focus();
            return;
        }
        if (IsManagedDetailsViewActive)
            _managedDetailsListView.Focus();
        else
            ActiveHost?.FocusShellView();
    }

    private void OpenActiveSelection()
    {
        if (IsManagedDetailsViewActive)
        {
            string[] selectedPaths = _managedDetailsListView.SelectedPaths.ToArray();
            if (selectedPaths.Length > 0)
                OpenFileContextPaths(selectedPaths);
        }
        else
        {
            ActiveHost?.OpenSelection();
        }

        FocusActiveFileView();
    }

    // Recalculates every hardcoded pixel in the filter bar and list from logical
    // units to device units for the current DPI.  Called once after handle creation
    // and again whenever the parent window moves to a different-DPI monitor.
    private void ScaleFilterControls()
    {
        // Bar height: textbox natural height + equal padding above and below.
        int textH = _filterTextBox.PreferredHeight;
        int barH  = textH + LogicalToDeviceUnits(10);
        int vpad  = (barH - textH) / 2;
        _filterBar.Height   = barH;
        _filterBar.Padding  = new Padding(0, vpad, 0, vpad);

        _filterPrefix.Width   = LogicalToDeviceUnits(82);
        _filterPrefix.Padding = new Padding(LogicalToDeviceUnits(6), 0, 0, 0);
        _clearBtn.Width       = LogicalToDeviceUnits(28);

        SyncFilterColumnWidths();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ScaleFilterControls();
        UpdateFilterIconSize();
        _explorerCommandBand.ApplyDpi(DeviceDpi);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ScaleFilterControls();
        UpdateFilterIconSize();
        _explorerCommandBand.ApplyDpi(DeviceDpi);
        PositionExplorerCommandBand();
    }

    private void UpdateFilterIconSize()
    {
        int size = LogicalToDeviceUnits(20);
        if (_filterIcons.ImageSize == new Size(size, size)) return;

        _filterIcons.Images.Clear();
        _filterIconIndexes.Clear();
        _filterVirtualItemCache.Clear();
        _filterIcons.ImageSize = new Size(size, size);
        _filterListView.Invalidate();
    }

    // ── Filter bar ────────────────────────────────────────────────────────────

    private void OnFilterCharInput(object? sender, char c)
    {
        // First printable char from ExplorerHost's key interceptor while the file list
        // has focus. Pre-populate the TextBox, then hand focus to it so the user can
        // keep typing naturally without further interception.
        _filterTextBox.TextChanged -= OnFilterTextBoxChanged;
        _filterTextBox.Text = _filterText + c;
        _filterTextBox.TextChanged += OnFilterTextBoxChanged;

        _filterText = _filterTextBox.Text;
        _filterTextBox.SelectionStart = _filterText.Length;
        _filterTextBox.Focus();

        if (ActiveHost != null) ActiveHost.IsFiltering = true;
        ShowFilterOverlay();
        RestartFilterDebounce();
    }

    private void OnFilterBackspaceTyped(object? sender, EventArgs e)
    {
        if (_filterText.Length == 0) return;
        _filterTextBox.TextChanged -= OnFilterTextBoxChanged;
        _filterTextBox.Text = _filterText[..^1];
        _filterTextBox.TextChanged += OnFilterTextBoxChanged;
        _filterText = _filterTextBox.Text;
        if (_filterText.Length == 0) { ClearFilter(); return; }
        ShowFilterOverlay();
        RestartFilterDebounce();
    }

    private void OnFilterTextBoxChanged(object? sender, EventArgs e)
    {
        _filterText = _filterTextBox.Text;
        if (string.IsNullOrEmpty(_filterText)) { ClearFilter(); return; }
        if (ActiveHost != null) ActiveHost.IsFiltering = true;
        ShowFilterOverlay();
        RestartFilterDebounce();
    }

    private void OnFilterTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2 && e.Modifiers == Keys.None)
        {
            BeginFilterRename();
            e.SuppressKeyPress = true;
        }
        else if (IsFilterQuickLookShortcut(e.KeyCode, e.Modifiers))
        {
            ExitFilterAndOpenQuickLook();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            ClearFilter();
            e.Handled = true;
        }
    }

    private void OnFilterListKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (e.KeyChar >= 0x20 && e.KeyChar != 0x7F)
        {
            OnFilterCharInput(sender, e.KeyChar);
            e.Handled = true;
        }
    }

    private void OnFilterListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2 && e.Modifiers == Keys.None)
        {
            BeginFilterRename();
            e.SuppressKeyPress = true;
        }
        else if (IsFilterQuickLookShortcut(e.KeyCode, e.Modifiers))
        {
            ExitFilterAndOpenQuickLook();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            ClearFilter();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Back && !e.Control && !e.Alt)
        {
            OnFilterBackspaceTyped(sender, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
        {
            OnFilterListDoubleClick(sender, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnFilterListDoubleClick(object? sender, EventArgs e)
    {
        if (_filterListView.SelectedIndices.Count == 0) return;
        string? path = GetFilterPathAt(_filterListView.SelectedIndices[0]);
        if (path == null) return;

        ExplorerHost? host = ActiveHost;
        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && File.Exists(path))
            host?.SelectItemPath(path);
        ClearFilter();

        if (isDirectory)
            host?.NavigateTo(path);
        else if (File.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Open file",
                    $"Could not open \"{Path.GetFileName(path)}\".");
            }
        }
    }

    private void OnFilterListMouseDown(object? sender, MouseEventArgs e)
    {
        FocusReceived?.Invoke(this, EventArgs.Empty);
        if (e.Button != MouseButtons.Right) return;
        var hit = _filterListView.HitTest(e.Location);
        if (hit.Item == null) return;
        hit.Item.Selected = true;
        hit.Item.Focused  = true;
        string? path = GetFilterPathAt(hit.Item.Index);
        if (path == null) return;
        ShowShellContextMenu([path], _filterListView, e.Location,
            allowInlineRename: false, renameAction: BeginFilterRename);
    }

    private void OnFilterListMouseMove(object? sender, MouseEventArgs e) =>
        SetHoveredFilterItem(_filterListView.HitTest(e.Location).Item?.Index ?? -1);

    private void OnFilterListMouseLeave(object? sender, EventArgs e) =>
        SetHoveredFilterItem(-1);

    private void SetHoveredFilterItem(int itemIndex)
    {
        if (_hoveredFilterItemIndex == itemIndex) return;

        int previousIndex = _hoveredFilterItemIndex;
        _hoveredFilterItemIndex = itemIndex;
        RedrawFilterItem(previousIndex);
        RedrawFilterItem(itemIndex);
    }

    private void OnFilterListDrawColumnHeader(object? sender,
        DrawListViewColumnHeaderEventArgs e)
    {
        using var brush = new SolidBrush(ThemeManager.Surface);
        using var pen = new Pen(ThemeManager.Border);
        e.Graphics.FillRectangle(brush, e.Bounds);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1,
            e.Bounds.Right, e.Bounds.Bottom - 1);
        var textBounds = Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(6), 0);
        if (e.ColumnIndex == _filterSortColumn)
        {
            int glyphWidth = LogicalToDeviceUnits(8);
            int glyphHeight = LogicalToDeviceUnits(5);
            int centerX = e.Bounds.Right - LogicalToDeviceUnits(8) - glyphWidth / 2;
            int centerY = e.Bounds.Top + e.Bounds.Height / 2;
            Point[] points = _filterSortAscending
                ?
                [
                    new(centerX, centerY - glyphHeight / 2),
                    new(centerX - glyphWidth / 2, centerY + glyphHeight / 2),
                    new(centerX + glyphWidth / 2, centerY + glyphHeight / 2),
                ]
                :
                [
                    new(centerX - glyphWidth / 2, centerY - glyphHeight / 2),
                    new(centerX + glyphWidth / 2, centerY - glyphHeight / 2),
                    new(centerX, centerY + glyphHeight / 2),
                ];
            using var glyphBrush = new SolidBrush(ThemeManager.MutedText);
            e.Graphics.FillPolygon(glyphBrush, points);
            textBounds.Width = Math.Max(1,
                textBounds.Width - glyphWidth - LogicalToDeviceUnits(6));
        }

        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, _filterListView.Font,
            textBounds, ThemeManager.Text,
            GetFilterListTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left));
    }

    private void OnFilterListColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column < 0 || e.Column >= _filterListView.Columns.Count)
            return;

        _filterSortAscending = e.Column == _filterSortColumn
            ? !_filterSortAscending
            : true;
        _filterSortColumn = e.Column;
        ResortFilterItemsPreservingSelection();
    }

    private void ResortFilterItemsPreservingSelection()
    {

        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int index in _filterListView.SelectedIndices)
        {
            if (GetFilterPathAt(index) is { } path)
                selectedPaths.Add(path);
        }
        string? focusedPath = GetFilterPathAt(_filterListView.FocusedItem?.Index ?? -1);

        SetFilterItems(_filterItems);

        for (int index = 0; index < _filterItems.Count; index++)
        {
            string path = _filterItems[index].FullPath;
            if (selectedPaths.Contains(path))
                _filterListView.Items[index].Selected = true;
            if (string.Equals(path, focusedPath, StringComparison.OrdinalIgnoreCase))
                _filterListView.Items[index].Focused = true;
        }
        _filterListView.Invalidate(true);
    }

    private void OnFilterListDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        ListViewItem? item = e.Item;
        if (item is null) return;

        using var brush = new SolidBrush(GetFilterItemBackColor(item));
        e.Graphics.FillRectangle(brush, new Rectangle(0, e.Bounds.Top,
            _filterListView.ClientSize.Width, e.Bounds.Height));
    }

    private void OnFilterListDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        ListViewItem? item = e.Item;
        if (item is null) return;

        Color background = GetFilterItemBackColor(item);
        Color text = item.Selected && _hasFocusIndicator
            ? Color.White
            : ThemeManager.Text;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        Rectangle textBounds = Rectangle.Inflate(e.Bounds,
            -LogicalToDeviceUnits(6), 0);
        if (e.ColumnIndex == 0 && item.ImageIndex >= 0
            && item.ImageIndex < _filterIcons.Images.Count)
        {
            int iconX = textBounds.Left;
            int iconY = e.Bounds.Top
                + Math.Max(0, (e.Bounds.Height - _filterIcons.ImageSize.Height) / 2);
            _filterIcons.Draw(e.Graphics, iconX, iconY, item.ImageIndex);
            int textX = iconX + _filterIcons.ImageSize.Width
                + LogicalToDeviceUnits(4);
            textBounds = new Rectangle(textX, e.Bounds.Top,
                Math.Max(1, e.Bounds.Right - LogicalToDeviceUnits(6) - textX),
                e.Bounds.Height);
        }
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, _filterListView.Font,
            textBounds, text,
            GetFilterListTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left));

        if (e.ColumnIndex == _filterListView.Columns.Count - 1
            && item.Selected
            && item.Focused)
        {
            var focusBounds = new Rectangle(1, e.Bounds.Top,
                Math.Max(1, _filterListView.ClientSize.Width - 2), e.Bounds.Height);
            ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, text, background);
        }
    }

    internal static bool IsFilterQuickLookShortcut(Keys keyCode, Keys modifiers) =>
        keyCode == Keys.Space && modifiers == Keys.Control;

    private void ExitFilterAndOpenQuickLook()
    {
        string? selectedPath = GetSelectedFilterPath();
        ClearFilter();

        if (selectedPath is null) return;
        ActiveHost?.SelectItemPath(selectedPath);
        OpenWithQuickLook(selectedPath);
    }

    private string? GetSelectedFilterPath()
    {
        if (_filterListView.SelectedIndices.Count > 0)
            return GetFilterPathAt(_filterListView.SelectedIndices[0]);

        return GetFilterPathAt(_filterListView.FocusedItem?.Index ?? -1);
    }

    private void BeginFilterRename()
    {
        if (!_filterListView.Visible || _filterRenameEditor is not null) return;

        int focusedIndex = _filterListView.FocusedItem?.Index ?? -1;
        int index = focusedIndex >= 0
            && _filterListView.Items[focusedIndex].Selected
                ? focusedIndex
                : _filterListView.SelectedIndices.Count > 0
                    ? _filterListView.SelectedIndices[0]
                    : -1;
        if (index < 0 || index >= _filterItems.Count) return;

        FilterItemData item = _filterItems[index];
        _filterListView.Items[index].EnsureVisible();
        Rectangle row = _filterListView.GetItemRect(index,
            ItemBoundsPortion.Entire);
        int inset = LogicalToDeviceUnits(6);
        int left = row.Left + inset + _filterIcons.ImageSize.Width
            + LogicalToDeviceUnits(4);
        int width = Math.Max(LogicalToDeviceUnits(48),
            _filterListView.Columns[0].Width - (left - row.Left) - inset);
        var editor = new TextBox
        {
            Text = item.Name,
            Font = _filterListView.Font,
            BackColor = ThemeManager.Window,
            ForeColor = ThemeManager.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(left,
                row.Top + Math.Max(0,
                    (row.Height - _filterListView.Font.Height - inset) / 2),
                width, Math.Max(_filterListView.Font.Height + inset, row.Height)),
        };
        _filterRenameEditor = editor;
        _filterRenamePath = item.FullPath;
        _filterRenameIsDirectory = item.IsDirectory;
        editor.KeyDown += OnFilterRenameEditorKeyDown;
        editor.LostFocus += OnFilterRenameEditorLostFocus;
        _filterListView.Controls.Add(editor);
        editor.BringToFront();
        editor.Focus();
        editor.Select(0, ManagedDetailsListView.GetInitialRenameSelectionLength(
            item.Name, item.IsDirectory, fileExtensionsVisible: true));
    }

    private async void OnFilterRenameEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
        e.SuppressKeyPress = true;
        await FinishFilterRenameAsync(commit: e.KeyCode == Keys.Enter,
            restoreFocus: true);
    }

    private async void OnFilterRenameEditorLostFocus(object? sender, EventArgs e) =>
        await FinishFilterRenameAsync(commit: true, restoreFocus: false);

    private void CancelFilterRename()
    {
        if (_filterRenameEditor is not { } editor) return;
        editor.KeyDown -= OnFilterRenameEditorKeyDown;
        editor.LostFocus -= OnFilterRenameEditorLostFocus;
        _filterRenameEditor = null;
        _filterRenamePath = null;
        _filterRenameIsDirectory = false;
        editor.Dispose();
    }

    private async Task FinishFilterRenameAsync(bool commit, bool restoreFocus)
    {
        if (_filterRenameEditor is not { } editor
            || _filterRenamePath is not { } sourcePath) return;

        string editedName = editor.Text;
        bool isDirectory = _filterRenameIsDirectory;
        CancelFilterRename();
        if (restoreFocus && _filterListView.Visible)
            _filterListView.Focus();
        if (!commit) return;

        await RenameFilteredItemAsync(sourcePath, editedName, isDirectory);
    }

    private async Task RenameFilteredItemAsync(
        string sourcePath, string editedName, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(editedName);
        try
        {
            if (string.IsNullOrWhiteSpace(editedName)
                || editedName is "." or ".."
                || editedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Enter a valid file or folder name.",
                    nameof(editedName));

            string? folder = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(folder)) return;
            string destination = Path.Combine(folder, editedName);
            if (string.Equals(sourcePath, destination, StringComparison.Ordinal))
                return;

            await Task.Run(() =>
            {
                if (isDirectory)
                    Directory.Move(sourcePath, destination);
                else
                    File.Move(sourcePath, destination);
            });

            DirectorySnapshotCache.Invalidate(folder);
            if (IsDisposed || Disposing || !_filterListView.Visible
                || !string.Equals(ActiveHost?.GetCurrentPath(), folder,
                    StringComparison.OrdinalIgnoreCase))
                return;

            string currentFilter = _filterText;
            await PopulateFilterListAsync(currentFilter);
            if (IsDisposed || Disposing || !_filterListView.Visible
                || !string.Equals(ActiveHost?.GetCurrentPath(), folder,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_filterText, currentFilter,
                    StringComparison.Ordinal))
                return;

            int selectedIndex = -1;
            for (int index = 0; index < _filterItems.Count; index++)
            {
                if (!string.Equals(_filterItems[index].FullPath,
                        destination, StringComparison.OrdinalIgnoreCase))
                    continue;
                selectedIndex = index;
                break;
            }
            if (selectedIndex >= 0)
            {
                ListViewItem selected = _filterListView.Items[selectedIndex];
                selected.Selected = true;
                selected.Focused = true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException)
        {
            AppLog.Warn(ex, nameof(RenameFilteredItemAsync),
                $"Could not rename \"{Path.GetFileName(sourcePath)}\".");
        }
    }

    private string? GetSelectedPathForPreview()
    {
        if (_filterListView.Visible)
            return GetSelectedFilterPath();

        if (IsManagedDetailsViewActive)
            return _managedDetailsListView.SelectedPath;

        return ActiveHost?.GetSelectedItemPathSnapshot();
    }

    private Color GetFilterItemBackColor(ListViewItem item) => item.Selected
        ? _hasFocusIndicator ? ThemeManager.ActiveSelection : ThemeManager.InactiveSelection
        : item.Index == _hoveredFilterItemIndex
            ? ThemeManager.AccentHover
            : ThemeManager.Window;

    private static TextFormatFlags GetFilterListTextFlags(HorizontalAlignment alignment)
    {
        TextFormatFlags flags = TextFormatFlags.VerticalCenter
                                | TextFormatFlags.SingleLine
                                | TextFormatFlags.EndEllipsis;
        return alignment switch
        {
            HorizontalAlignment.Center => flags | TextFormatFlags.HorizontalCenter,
            HorizontalAlignment.Right  => flags | TextFormatFlags.Right,
            _                          => flags | TextFormatFlags.Left,
        };
    }

    private void RedrawFilterItem(int itemIndex)
    {
        if (!_filterListView.IsHandleCreated
            || itemIndex < 0
            || itemIndex >= _filterListView.VirtualListSize)
            return;

        _filterListView.RedrawItems(itemIndex, itemIndex, invalidateOnly: true);
    }

    private void ShowShellContextMenu(IReadOnlyList<string> paths, Control control,
        Point clientPt, bool allowInlineRename, Action? renameAction = null)
    {
        UseShellItemContextMenu(paths, control, (contextMenu, selectedPaths) =>
        {
            Point screen = control.PointToScreen(clientPt);
            IntPtr ownerWindow = control.FindForm()?.Handle ?? control.Handle;
            ExplorerHost? activeHost = ActiveHost;
            string? quickAccessFolder = selectedPaths.Length == 1
                && Directory.Exists(selectedPaths[0])
                    ? selectedPaths[0]
                    : null;
            string? favoriteFile = selectedPaths.Length == 1
                && File.Exists(selectedPaths[0])
                    ? selectedPaths[0]
                    : null;
            string? pinnablePath = quickAccessFolder ?? favoriteFile;
            bool isPinned = pinnablePath is not null
                && QuickAccessService.IsPinned(pinnablePath);
            // Use the complete Windows Shell menu for both files and folders.
            // Keep the application-owned action strip, but leave every command
            // supplied by Windows and installed applications available below it.
            using var shellMenuIcons = new NativeMenuIconSet(control.DeviceDpi);
            ShellContextMenu.Show(contextMenu, ownerWindow, screen,
                customizeMenu: menu =>
                {
                    if (quickAccessFolder is not null)
                        AddQuickAccessMenuCommand(menu, isPinned);
                    else if (favoriteFile is not null)
                        AddFavoriteMenuCommand(menu, isPinned);
                    if (selectedPaths.Length == 1)
                        AddCreateShortcutMenuCommand(menu);
                    AddOppositePaneMenuCommands(menu);
                    EnsureCopyAsPathShortcutLabels(menu);
                    ShellContextMenu.OrderCommonFileCommands(menu);
                    PopulateFolderMenuIcons(menu, shellMenuIcons);
                },
                executeCustomCommand: commandId =>
                {
                    return (uint)commandId switch
                    {
                        QuickAccessMenuCommandId when pinnablePath is not null =>
                            SetQuickAccessPinned(pinnablePath, !isPinned),
                        CreateShortcutMenuCommandId when selectedPaths.Length == 1 =>
                            RequestCreateShortcut(selectedPaths[0]),
                        CopyToOtherPaneMenuCommandId =>
                            RequestTransferToOtherPane(
                                FileOperationKind.Copy, selectedPaths),
                        MoveToOtherPaneMenuCommandId =>
                            RequestTransferToOtherPane(
                                FileOperationKind.Move, selectedPaths),
                        _ => false,
                    };
                },
                executeCanonicalCommand: canonicalVerb =>
                    TryOpenShellItemInCurrentPane(canonicalVerb, selectedPaths)
                    || (ShellContextMenu.CanUseLiveSelectionContext(
                            selectedPaths.Length, canonicalVerb)
                        && activeHost is not null
                        && activeHost.TryInvokeSelectedShellCommand(
                            selectedPaths[0], canonicalVerb, screen)),
                executeCommandBarCommand: command => ExecuteFileContextCommand(
                    command, selectedPaths, allowInlineRename, renameAction),
                isCommandBarCommandEnabled: command =>
                    IsFileContextCommandEnabled(command, selectedPaths),
                isCommandBarCommandVisible: command =>
                    IsFileContextCommandVisible(command, selectedPaths),
                dpi: control.DeviceDpi,
                queryFlags: NativeMethods.CMF_EXPLORE);
        });
    }

    internal static bool AddOppositePaneMenuCommands(IntPtr menu)
    {
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        // Keep these actions next to Open, where they remain visible even when
        // Windows and shell extensions supply a long context menu.
        uint insertAt = NativeMethods.GetMenuItemCount(menu) > 0 ? 1u : 0u;
        return NativeMethods.InsertMenu(menu, insertAt,
                   NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING,
                   CopyToOtherPaneMenuCommandId, "Copy to opposite pane\tAlt+C")
            && NativeMethods.InsertMenu(menu, insertAt + 1,
                   NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING,
                   MoveToOtherPaneMenuCommandId, "Move to opposite pane\tAlt+M")
            && NativeMethods.InsertMenu(menu, insertAt + 2,
                   NativeMethods.MF_BYPOSITION | NativeMethods.MF_SEPARATOR,
                   0, string.Empty);
    }

    private bool TryOpenShellItemInCurrentPane(string canonicalVerb,
        IReadOnlyList<string> paths)
    {
        if (paths.Count != 1
            || !canonicalVerb.Equals("open", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(paths[0]))
            return false;

        ActiveHost?.NavigateTo(paths[0]);
        return true;
    }

    private bool RequestTransferToOtherPane(FileOperationKind kind,
        IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) return false;
        TransferToOtherPaneRequested?.Invoke(this,
            new OppositePaneTransferRequest(kind, paths.ToArray()));
        return true;
    }

    internal static bool AddQuickAccessMenuCommand(IntPtr menu, bool isPinned)
        => AddPinnedItemMenuCommand(menu,
            QuickAccessMenuCommandId,
            QuickAccessService.GetCommandLabel(isPinned),
            ["Pin to Quick access", "Unpin from Quick access"]);

    internal static bool AddFavoriteMenuCommand(IntPtr menu, bool isPinned)
        => AddPinnedItemMenuCommand(menu,
            QuickAccessMenuCommandId,
            QuickAccessService.GetFavoriteCommandLabel(isPinned),
            ["Add to Favorites", "Remove from Favorites"]);

    internal static bool AddCreateShortcutMenuCommand(IntPtr menu)
    {
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        int count = NativeMethods.GetMenuItemCount(menu);
        int insertPosition = count;
        var label = new StringBuilder(128);
        for (int position = count - 1; position >= 0; position--)
        {
            label.Clear();
            if (NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION) <= 0)
                continue;
            if (ShellContextMenu.NormalizeMenuLabel(label.ToString()).Equals(
                    "Create shortcut", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.DeleteMenu(menu, (uint)position,
                    NativeMethods.MF_BYPOSITION);
                insertPosition = position;
            }
        }

        if (insertPosition == count)
        {
            for (int position = 0;
                 position < NativeMethods.GetMenuItemCount(menu);
                 position++)
            {
                label.Clear();
                if (NativeMethods.GetMenuString(menu, (uint)position, label,
                        label.Capacity, NativeMethods.MF_BYPOSITION) > 0
                    && ShellContextMenu.NormalizeMenuLabel(label.ToString()).Equals(
                        "Open", StringComparison.OrdinalIgnoreCase))
                {
                    insertPosition = position + 1;
                    break;
                }
            }
        }

        return NativeMethods.InsertMenu(menu, (uint)insertPosition,
                   NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING,
                   CreateShortcutMenuCommandId, "Create shortcut...")
               || NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                   CreateShortcutMenuCommandId, "Create shortcut...");
    }

    private bool RequestCreateShortcut(string targetPath)
    {
        BeginInvoke((MethodInvoker)(() =>
            CreateShortcutRequested?.Invoke(this, targetPath)));
        return true;
    }

    private static bool AddPinnedItemMenuCommand(IntPtr menu,
        uint commandId, string commandLabel,
        IReadOnlyCollection<string> labelsToReplace)
    {
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLabel);

        int itemCount = NativeMethods.GetMenuItemCount(menu);
        int insertPosition = itemCount;
        var label = new StringBuilder(128);
        for (int position = itemCount - 1; position >= 0; position--)
        {
            label.Clear();
            if (NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION) <= 0)
                continue;

            string normalized = ShellContextMenu.NormalizeMenuLabel(
                label.ToString());
            if (labelsToReplace.Contains(normalized,
                    StringComparer.OrdinalIgnoreCase))
            {
                NativeMethods.DeleteMenu(menu, (uint)position,
                    NativeMethods.MF_BYPOSITION);
                if (position < insertPosition) insertPosition--;
                continue;
            }

            if (normalized.Equals("Open", StringComparison.OrdinalIgnoreCase))
                insertPosition = Math.Min(insertPosition, position + 1);
        }

        return NativeMethods.InsertMenu(menu, (uint)insertPosition,
                   NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING,
                   commandId, commandLabel)
               || NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                   commandId, commandLabel);
    }

    private static bool SetQuickAccessPinned(string path, bool pin)
    {
        QuickAccessService.TrySetPinned(path, pin);
        return true;
    }

    private void ShowShellSendToMenu(Control anchor,
        IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        UseShellItemContextMenu(paths, anchor, (contextMenu, _) =>
        {
            IntPtr ownerWindow = anchor.FindForm()?.Handle ?? anchor.Handle;
            Point location = anchor.PointToScreen(new Point(0, anchor.Height));
            if (ShellContextMenu.ShowSubmenu(contextMenu, ownerWindow,
                    location, "Send to"))
                return;

            MessageBox.Show(anchor.FindForm(),
                "Windows did not provide any Send to destinations for the selected files.",
                "Send to is not available", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
    }

    private static void UseShellItemContextMenu(IReadOnlyList<string> paths,
        Control owner, Action<NativeMethods.IContextMenu, string[]> action)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(action);

        string[] selectedPaths = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPaths.Length == 0) return;

        string? parentPath = Path.GetDirectoryName(selectedPaths[0]);
        selectedPaths = selectedPaths
            .Where(path => string.Equals(Path.GetDirectoryName(path), parentPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var pidls = new List<IntPtr>(selectedPaths.Length);
        var childPidls = new List<IntPtr>(selectedPaths.Length);
        NativeMethods.IShellFolder? folder = null;
        NativeMethods.IContextMenu? contextMenu = null;
        try
        {
            foreach (string path in selectedPaths)
            {
                int parseResult = NativeMethods.SHParseDisplayName(path, IntPtr.Zero,
                    out IntPtr pidl, 0, out _);
                if (parseResult != 0 || pidl == IntPtr.Zero) continue;
                pidls.Add(pidl);
                childPidls.Add(NativeMethods.ILFindLastID(pidl));
            }
            if (pidls.Count == 0) return;

            var iidFolder = typeof(NativeMethods.IShellFolder).GUID;
            int hr = NativeMethods.SHBindToParent(pidls[0], ref iidFolder,
                out IntPtr folderPointer, out IntPtr firstChildPidl);
            if (hr != 0 || folderPointer == IntPtr.Zero) return;
            folder = (NativeMethods.IShellFolder)
                Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            childPidls[0] = firstChildPidl;

            IntPtr ownerWindow = owner.FindForm()?.Handle ?? owner.Handle;
            contextMenu = CreateItemContextMenu(folder, ownerWindow, childPidls);
            if (contextMenu is not null)
                action(contextMenu, selectedPaths);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(UseShellItemContextMenu),
                $"Could not complete a Shell action for \"{Path.GetFileName(selectedPaths[0])}\".");
        }
        finally
        {
            if (contextMenu != null) Marshal.ReleaseComObject(contextMenu);
            if (folder != null) Marshal.ReleaseComObject(folder);
            foreach (IntPtr pidl in pidls)
                NativeMethods.CoTaskMemFree(pidl);
        }
    }

    private static NativeMethods.IContextMenu? CreateItemContextMenu(
        NativeMethods.IShellFolder folder, IntPtr ownerWindow,
        IReadOnlyList<IntPtr> childPidls)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(childPidls);
        if (childPidls.Count == 0) return null;

        var contextMenuId = typeof(NativeMethods.IContextMenu).GUID;
        int result = folder.GetUIObjectOf(ownerWindow, (uint)childPidls.Count,
            childPidls.ToArray(), ref contextMenuId, IntPtr.Zero,
            out IntPtr contextMenuPointer);
        if (result != 0 || contextMenuPointer == IntPtr.Zero)
        {
            if (contextMenuPointer != IntPtr.Zero)
                Marshal.Release(contextMenuPointer);
            if (childPidls.Count == 1) return null;

            result = folder.GetUIObjectOf(ownerWindow, 1, [childPidls[0]],
                ref contextMenuId, IntPtr.Zero, out contextMenuPointer);
        }

        if (result != 0 || contextMenuPointer == IntPtr.Zero)
        {
            if (contextMenuPointer != IntPtr.Zero)
                Marshal.Release(contextMenuPointer);
            return null;
        }

        try
        {
            return (NativeMethods.IContextMenu)
                Marshal.GetObjectForIUnknown(contextMenuPointer);
        }
        finally
        {
            Marshal.Release(contextMenuPointer);
        }
    }

    internal static void PopulateFolderMenuIcons(IntPtr menu,
        NativeMenuIconSet icons)
    {
        ArgumentNullException.ThrowIfNull(icons);
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        int itemCount = NativeMethods.GetMenuItemCount(menu);
        var label = new StringBuilder(256);
        for (int position = 0; position < itemCount; position++)
        {
            label.Clear();
            if (NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION) > 0
                && TryGetFolderMenuGlyph(label.ToString(), out CompactMenuGlyph glyph))
            {
                icons.ApplyAtPosition(menu, (uint)position, glyph);
            }

            IntPtr subMenu = NativeMethods.GetSubMenu(menu, position);
            if (subMenu != IntPtr.Zero)
                PopulateFolderMenuIcons(subMenu, icons);
        }
    }

    internal static int EnsureCopyAsPathShortcutLabels(IntPtr menu)
    {
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        int updatedCount = 0;
        int itemCount = NativeMethods.GetMenuItemCount(menu);
        var label = new StringBuilder(256);
        for (int position = 0; position < itemCount; position++)
        {
            label.Clear();
            if (NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION) > 0)
            {
                string currentLabel = label.ToString();
                if (ShellContextMenu.NormalizeMenuLabel(currentLabel).Equals(
                        "Copy as path", StringComparison.OrdinalIgnoreCase))
                {
                    string text = currentLabel.Split('\t', 2)[0];
                    string updatedLabel =
                        $"{text}\t{CommandBar.CopyPathsShortcutText}";
                    if (!currentLabel.Equals(updatedLabel,
                            StringComparison.Ordinal)
                        && NativeMethods.SetMenuItemText(
                            menu, (uint)position, updatedLabel))
                    {
                        updatedCount++;
                    }
                }
            }

            IntPtr subMenu = NativeMethods.GetSubMenu(menu, position);
            if (subMenu != IntPtr.Zero)
                updatedCount += EnsureCopyAsPathShortcutLabels(subMenu);
        }

        return updatedCount;
    }

    internal static bool TryGetFolderMenuGlyph(string label,
        out CompactMenuGlyph glyph)
    {
        ArgumentNullException.ThrowIfNull(label);
        string normalized = label
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Split('\t', 2)[0]
            .Trim()
            .TrimEnd('.', '…')
            .ToLowerInvariant();

        glyph = normalized switch
        {
            "give access to" => CompactMenuGlyph.GiveAccess,
            "restore previous versions" => CompactMenuGlyph.RestorePrevious,
            "include in library" => CompactMenuGlyph.IncludeLibrary,
            "pin to start" => CompactMenuGlyph.PinStart,
            "unpin from start" => CompactMenuGlyph.PinStart,
            "pin to quick access" => CompactMenuGlyph.PinStart,
            "unpin from quick access" => CompactMenuGlyph.PinStart,
            "add to favorites" => CompactMenuGlyph.Favorite,
            "remove from favorites" => CompactMenuGlyph.Favorite,
            "copy to opposite pane" => CompactMenuGlyph.CopyToOtherPane,
            "move to opposite pane" => CompactMenuGlyph.MoveToOtherPane,
            "copy as path" => CompactMenuGlyph.CopyPath,
            "create shortcut" => CompactMenuGlyph.CreateShortcut,
            "properties" => CompactMenuGlyph.Properties,
            _ => default,
        };
        return normalized is "give access to"
            or "restore previous versions"
            or "include in library"
            or "pin to start"
            or "unpin from start"
            or "pin to quick access"
            or "unpin from quick access"
            or "add to favorites"
            or "remove from favorites"
            or "copy to opposite pane"
            or "move to opposite pane"
            or "copy as path"
            or "create shortcut"
            or "properties";
    }

    private void OpenFileContextPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            ActiveHost?.NavigateTo(paths[0]);
            return;
        }

        foreach (string path in paths.Where(static path =>
                     File.Exists(path) || Directory.Exists(path)))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, nameof(OpenFileContextPaths),
                    $"Could not open \"{Path.GetFileName(path)}\".");
            }
        }
    }

    private bool ExecuteFileContextCommand(ShellContextCommand command,
        IReadOnlyList<string> paths, bool allowInlineRename,
        Action? renameAction = null)
    {
        switch (command)
        {
            case ShellContextCommand.Cut:
                CopyManagedPathsToClipboard(paths, cut: true);
                return true;
            case ShellContextCommand.Copy:
                CopyManagedPathsToClipboard(paths, cut: false);
                return true;
            case ShellContextCommand.Rename when allowInlineRename && paths.Count == 1:
                BeginInvoke((MethodInvoker)(() => _managedDetailsListView.BeginRename()));
                return true;
            case ShellContextCommand.Rename when renameAction != null && paths.Count == 1:
                BeginInvoke((MethodInvoker)(() => renameAction()));
                return true;
            case ShellContextCommand.Delete when OperationManager.Current is { } manager:
                manager.Start(ModifierKeys.HasFlag(Keys.Shift)
                    ? FileOperationKind.DeletePermanently
                    : FileOperationKind.Delete, paths);
                if (allowInlineRename) ScheduleManagedDetailsRefresh();
                return true;
            case ShellContextCommand.Share when FileSharingService.CanShare(paths):
                ShareFiles(paths);
                return true;
            case ShellContextCommand.Rename:
            case ShellContextCommand.Delete:
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private void ShareFiles(IReadOnlyList<string> paths)
    {
        string[] files = FileSharingService.GetShareableFiles(paths);
        if (files.Length == 0) return;

        using var dialog = new ShareDialog(files, new FileSharingService(),
            (anchor, selectedFiles) =>
                ShowShellSendToMenu(anchor, selectedFiles),
            static (selectedFiles, destination) =>
                OperationManager.Current?.Start(FileOperationKind.Copy,
                    selectedFiles, destination) is not null);
        IWin32Window owner = FindForm() is { } form ? form : this;
        dialog.ShowDialog(owner);
    }

    private static bool IsFileContextCommandEnabled(ShellContextCommand command,
        IReadOnlyList<string> paths) => command switch
    {
        ShellContextCommand.Rename => paths.Count == 1,
        _ => paths.Count > 0,
    };

    private static bool IsFileContextCommandVisible(ShellContextCommand command,
        IReadOnlyList<string> paths) =>
        command != ShellContextCommand.Share || FileSharingService.CanShare(paths);

    /// <summary>
    /// Opens the Shell's folder-background context menu (View, Sort by, Group
    /// by, New, Properties, Terminal, and extension-provided entries) rather
    /// than the context menu for the folder itself.
    /// </summary>
    private void ShowShellBackgroundContextMenu(string folderPath, Control control, Point clientPt)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        int hr = NativeMethods.SHParseDisplayName(folderPath, IntPtr.Zero,
            out IntPtr pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return;

        NativeMethods.IShellFolder? parentFolder = null;
        NativeMethods.IShellFolder? folder = null;
        NativeMethods.IContextMenu? contextMenu = null;
        try
        {
            var folderId = typeof(NativeMethods.IShellFolder).GUID;
            hr = NativeMethods.SHBindToParent(pidl, ref folderId,
                out IntPtr parentPointer, out IntPtr childPidl);
            if (hr != 0 || parentPointer == IntPtr.Zero) return;
            parentFolder = (NativeMethods.IShellFolder)Marshal.GetObjectForIUnknown(parentPointer);
            Marshal.Release(parentPointer);

            hr = parentFolder.BindToObject(childPidl, IntPtr.Zero,
                ref folderId, out IntPtr folderPointer);
            if (hr != 0 || folderPointer == IntPtr.Zero) return;
            folder = (NativeMethods.IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);

            var contextMenuId = typeof(NativeMethods.IContextMenu).GUID;
            hr = folder.CreateViewObject(control.Handle, ref contextMenuId,
                out IntPtr contextMenuPointer);
            if (hr != 0 || contextMenuPointer == IntPtr.Zero) return;
            contextMenu = (NativeMethods.IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPointer);
            Marshal.Release(contextMenuPointer);

            Point screen = control.PointToScreen(clientPt);
            IReadOnlyDictionary<int, ShellNewMenu.ShellNewItem> shellNewCommands =
                new Dictionary<int, ShellNewMenu.ShellNewItem>();
            ShellContextMenu.Show(contextMenu, control.Handle, screen,
                customizeMenu: menu => shellNewCommands = ShellNewMenu.Populate(menu),
                executeCustomCommand: commandId =>
                {
                    bool handled = ShellNewMenu.TryExecute(commandId,
                        shellNewCommands, folderPath, out string? createdPath);
                    if (createdPath is not null)
                        _managedDetailsListView.BeginRenameCreatedItem(createdPath);
                    return handled;
                });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ShowShellBackgroundContextMenu),
                $"Could not open the background context menu for \"{folderPath}\".");
        }
        finally
        {
            if (contextMenu != null) Marshal.ReleaseComObject(contextMenu);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (parentFolder != null) Marshal.ReleaseComObject(parentFolder);
            NativeMethods.CoTaskMemFree(pidl);
        }
    }

    private void OnManagedDetailsItemActivated(object? sender, string path)
    {
        OpenFileContextPaths([path]);
    }

    private void OnManagedDetailsContextMenuRequested(
        object? sender, ManagedContextMenuEventArgs e)
    {
        IReadOnlyList<string> selectedPaths = _managedDetailsListView.SelectedPaths;
        IReadOnlyList<string> paths = selectedPaths.Contains(e.Path,
            StringComparer.OrdinalIgnoreCase)
            ? selectedPaths
            : [e.Path];
        ShowShellContextMenu(paths, _managedDetailsListView, e.Location,
            allowInlineRename: true);
    }

    private void OnManagedDetailsBackgroundContextMenuRequested(
        object? sender, ManagedBackgroundContextMenuEventArgs e)
    {
        ShowManagedBackgroundContextMenu(e.Location);
    }

    private void ShowManagedBackgroundContextMenu(Point clientLocation)
    {
        var images = new List<Image>();
        var menu = new ContextMenuStrip
        {
            Font = Font,
            ShowImageMargin = true,
            ShowCheckMargin = true,
            RenderMode = ToolStripRenderMode.System,
        };
        ThemeManager.ApplyToolStrip(menu);
        menu.Closed += (_, _) => BeginInvoke((MethodInvoker)(() =>
        {
            foreach (Image image in images) image.Dispose();
            menu.Dispose();
        }));

        ToolStripMenuItem AddItem(ToolStripItemCollection items, string text, char glyph,
            Action action, string? shortcut = null, bool enabled = true, bool isChecked = false)
        {
            var item = new ToolStripMenuItem(text)
            {
                Image = CreateBackgroundMenuGlyph(glyph, images,
                    _managedDetailsListView.DeviceDpi),
                ImageScaling = ToolStripItemImageScaling.None,
                ShortcutKeyDisplayString = shortcut,
                Enabled = enabled,
                Checked = isChecked,
            };
            item.Click += (_, _) => BeginInvoke(action);
            items.Add(item);
            return item;
        }

        ToolStripMenuItem AddPlainItem(ToolStripItemCollection items, string text,
            Action action, bool enabled = true, bool isChecked = false)
        {
            var item = new ToolStripMenuItem(text)
            {
                Enabled = enabled,
                Checked = isChecked,
            };
            item.Click += (_, _) => BeginInvoke(action);
            items.Add(item);
            return item;
        }

        ToolStripMenuItem AddViewItem(ToolStripItemCollection items, string text,
            ViewMenuGlyph glyph, Action action, string shortcut, bool isChecked = false)
        {
            var item = new ToolStripMenuItem(text)
            {
                Image = CreateViewMenuGlyph(glyph, images, _managedDetailsListView.DeviceDpi),
                ImageScaling = ToolStripItemImageScaling.None,
                ShortcutKeyDisplayString = shortcut,
                Checked = isChecked,
            };
            item.Click += (_, _) => BeginInvoke(action);
            items.Add(item);
            return item;
        }

        const char glyphView = '\uE8A9';
        const char glyphSort = '\uE8CB';
        const char glyphGroup = '\uE8D2';
        const char glyphUndo = '\uE7A7';
        const char glyphPaste = '\uE77F';
        const char glyphNew = '\uE710';
        const char glyphFolder = '\uE8B7';
        const char glyphProperties = '\uE946';
        const char glyphRename = '\uE8AC';
        const char glyphTerminal = '\uE756';
        const char glyphMore = '\uE712';
        const char glyphDocument = '\uE7C3';

        var view = AddItem(menu.Items, "View", glyphView, static () => { });
        ConfigureBackgroundSubmenu(view);
        AddViewItem(view.DropDownItems, "Extra large icons", ViewMenuGlyph.ExtraLargeIcons,
            () => SetManagedBackgroundView(1, 256), "Ctrl+Shift+1");
        AddViewItem(view.DropDownItems, "Large icons", ViewMenuGlyph.LargeIcons,
            () => SetManagedBackgroundView(1, 96), "Ctrl+Shift+2");
        AddViewItem(view.DropDownItems, "Medium icons", ViewMenuGlyph.MediumIcons,
            () => SetManagedBackgroundView(1, 48), "Ctrl+Shift+3");
        AddViewItem(view.DropDownItems, "Small icons", ViewMenuGlyph.SmallIcons,
            () => SetManagedBackgroundView(1, 16), "Ctrl+Shift+4");
        AddViewItem(view.DropDownItems, "List", ViewMenuGlyph.List,
            () => SetManagedBackgroundView(3, null), "Ctrl+Shift+5");
        AddViewItem(view.DropDownItems, "Details", ViewMenuGlyph.Details,
            () => SetManagedBackgroundView(4, null), "Ctrl+Shift+6",
            isChecked: !_managedDetailsOverlaySuppressed);
        AddViewItem(view.DropDownItems, "Tiles", ViewMenuGlyph.Tiles,
            () => SetManagedBackgroundView(6, null), "Ctrl+Shift+7");
        AddViewItem(view.DropDownItems, "Content", ViewMenuGlyph.Content,
            () => SetManagedBackgroundView(8, null), "Ctrl+Shift+8");

        var sort = AddItem(menu.Items, "Sort by", glyphSort, static () => { });
        ConfigureBackgroundSubmenu(sort);
        SetSubmenuIconsVisible(sort, visible: false);
        AddManagedSortMenuItem(sort.DropDownItems, "Name", ManagedDetailsSortColumn.Name);
        AddManagedSortMenuItem(sort.DropDownItems, "Date modified",
            ManagedDetailsSortColumn.DateModified);
        AddManagedSortMenuItem(sort.DropDownItems, "Type", ManagedDetailsSortColumn.Type);
        AddManagedSortMenuItem(sort.DropDownItems, "Size", ManagedDetailsSortColumn.Size);
        sort.DropDownItems.Add(new ToolStripSeparator());
        AddPlainItem(sort.DropDownItems, "Ascending",
            () => _managedDetailsListView.SetSortDirection(true),
            isChecked: _managedDetailsListView.IsSortAscending);
        AddPlainItem(sort.DropDownItems, "Descending",
            () => _managedDetailsListView.SetSortDirection(false),
            isChecked: !_managedDetailsListView.IsSortAscending);

        var group = AddItem(menu.Items, "Group by", glyphGroup, static () => { });
        ConfigureBackgroundSubmenu(group);
        SetSubmenuIconsVisible(group, visible: false);
        AddManagedGroupMenuItem(group.DropDownItems, "Name", ManagedDetailsGroupColumn.Name);
        AddManagedGroupMenuItem(group.DropDownItems, "Date modified",
            ManagedDetailsGroupColumn.DateModified);
        AddManagedGroupMenuItem(group.DropDownItems, "Type", ManagedDetailsGroupColumn.Type);
        AddManagedGroupMenuItem(group.DropDownItems, "Size", ManagedDetailsGroupColumn.Size);
        group.DropDownItems.Add(new ToolStripSeparator());
        bool grouping = _managedDetailsListView.GroupColumn is not null;
        AddPlainItem(group.DropDownItems, "Ascending",
            () => _managedDetailsListView.SetGroupDirection(true), enabled: grouping,
            isChecked: grouping && _managedDetailsListView.IsGroupAscending);
        AddPlainItem(group.DropDownItems, "Descending",
            () => _managedDetailsListView.SetGroupDirection(false), enabled: grouping,
            isChecked: grouping && !_managedDetailsListView.IsGroupAscending);
        group.DropDownItems.Add(new ToolStripSeparator());
        AddPlainItem(group.DropDownItems, "More...",
            _managedDetailsListView.OpenColumnChooser);

        AddItem(menu.Items, "Undo Copy", glyphUndo, static () => { }, "Ctrl+Z", enabled: false);
        AddItem(menu.Items, "Paste", glyphPaste, PasteIntoCurrentFolder, "Ctrl+V",
            enabled: CanPasteFilesFromClipboard());

        var create = AddItem(menu.Items, "New", glyphNew, static () => { });
        ConfigureBackgroundSubmenu(create);
        foreach (ShellNewMenu.ShellNewItem newItem in ShellNewMenu.GetPreferredItems())
        {
            char glyph = newItem.Kind == ShellNewMenu.ShellNewKind.Folder
                ? glyphFolder
                : glyphDocument;
            var item = AddItem(create.DropDownItems, newItem.DisplayName, glyph, () =>
            {
                if (newItem.Kind == ShellNewMenu.ShellNewKind.Folder)
                    _managedDetailsListView.CreateNewFolderAndBeginRename();
                else if (newItem.Kind == ShellNewMenu.ShellNewKind.Shortcut)
                {
                    IWin32Window owner = FindForm() is { } form ? form : this;
                    if (ShortcutWizard.ShowForFolder(owner, CurrentPath()))
                        ScheduleManagedDetailsRefresh();
                }
                else
                {
                    if (ShellNewMenu.TryExecute(newItem, CurrentPath(),
                            out string? createdPath) && createdPath is not null)
                        _managedDetailsListView.BeginRenameCreatedItem(createdPath);
                }
            });
            item.Image = CreateShellMenuIcon(newItem.Extension,
                newItem.Kind == ShellNewMenu.ShellNewKind.Folder, glyph, images,
                _managedDetailsListView.DeviceDpi);
        }

        menu.Items.Add(new ToolStripSeparator());
        AddItem(menu.Items, "Properties", glyphProperties,
            () => ActiveHost?.ShowProperties(), "Alt+Enter");
        AddItem(menu.Items, "Rename with PowerRename", glyphRename,
            OpenPowerRename);
        AddItem(menu.Items, "Open in Terminal", glyphTerminal,
            OpenTerminalInCurrentFolder);
        menu.Items.Add(new ToolStripSeparator());
        AddItem(menu.Items, "Show more options", glyphMore, () => BeginInvoke(() =>
            ShowShellBackgroundContextMenu(CurrentPath(), _managedDetailsListView, clientLocation)));

        menu.Show(_managedDetailsListView, clientLocation);
    }

    private void AddManagedSortMenuItem(ToolStripItemCollection items, string text,
        ManagedDetailsSortColumn column)
    {
        var item = new ToolStripMenuItem(text)
        {
            Checked = _managedDetailsListView.SortColumn == column,
        };
        item.Click += (_, _) => BeginInvoke((MethodInvoker)(() =>
            _managedDetailsListView.SortBy(column)));
        items.Add(item);
    }

    private void AddManagedGroupMenuItem(ToolStripItemCollection items, string text,
        ManagedDetailsGroupColumn column)
    {
        var item = new ToolStripMenuItem(text)
        {
            Checked = _managedDetailsListView.GroupColumn == column,
        };
        item.Click += (_, _) => BeginInvoke((MethodInvoker)(() =>
            _managedDetailsListView.GroupBy(column)));
        items.Add(item);
    }

    private void SetManagedBackgroundView(uint viewMode, uint? iconSize)
    {
        ExplorerHost? host = ActiveHost;
        if (viewMode == 4)
        {
            _managedDetailsOverlaySuppressed = false;
            host?.SetViewMode(viewMode);
            PositionExplorerCommandBand();
            PositionManagedDetailsView();
            RefreshManagedDetailsView(force: true);
            FocusActiveFileView();
            return;
        }

        _managedDetailsOverlaySuppressed = true;
        _managedDetailsListView.Visible = false;
        if (iconSize is { } size)
            host?.SetViewModeAndIconSize(viewMode, (int)size);
        else
            host?.SetViewMode(viewMode);
        host?.FocusShellView();
    }

    private void OpenTerminalInCurrentFolder()
    {
        string folderPath = CurrentPath();
        if (!Directory.Exists(folderPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                UseShellExecute = true,
                ArgumentList = { "-d", folderPath },
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OpenTerminalInCurrentFolder),
                $"Could not open a terminal in \"{folderPath}\".");
        }
    }

    private static void ConfigureBackgroundSubmenu(ToolStripMenuItem item)
    {
        if (item.DropDown is not ToolStripDropDownMenu submenu) return;

        submenu.ShowImageMargin = true;
        submenu.ShowCheckMargin = true;
    }

    private static void SetSubmenuIconsVisible(ToolStripMenuItem item, bool visible)
    {
        if (item.DropDown is ToolStripDropDownMenu submenu)
            submenu.ShowImageMargin = visible;
    }

    private void OpenPowerRename()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "PowerRename.exe",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OpenPowerRename),
                "Could not start PowerRename. Use Show more options to access the installed Shell extension.");
        }
    }

    private static Image CreateBackgroundMenuGlyph(char glyph, List<Image> images, int dpi)
    {
        float scale = Math.Max(96, dpi) / 96f;
        int iconSize = (int)Math.Round(20 * scale);
        string iconFont = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";
        var image = new Bitmap(iconSize, iconSize);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var font = new Font(iconFont, 15f * scale, FontStyle.Regular,
            GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(ThemeManager.Text))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        })
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(glyph.ToString(), font, brush,
                new RectangleF(0, 0, iconSize, iconSize), format);
        }
        images.Add(image);
        return image;
    }

    private static Image CreateViewMenuGlyph(ViewMenuGlyph glyph, List<Image> images, int dpi)
    {
        float scale = Math.Max(96, dpi) / 96f;
        int Scale(int logicalPixels) => (int)Math.Round(logicalPixels * scale);

        int iconSize = Scale(20);
        var image = new Bitmap(iconSize, iconSize);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var pen = new Pen(ThemeManager.MutedText, 1f))
        {
            // These are deliberately one-pixel, pixel-aligned strokes. The
            // previous anti-aliased rendering blurred the small menu glyphs.
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.PixelOffsetMode = PixelOffsetMode.Default;

            switch (glyph)
            {
                case ViewMenuGlyph.ExtraLargeIcons:
                    graphics.DrawRectangle(pen, Scale(4), Scale(3), Scale(12), Scale(13));
                    break;
                case ViewMenuGlyph.LargeIcons:
                    graphics.DrawRectangle(pen, Scale(4), Scale(4), Scale(12), Scale(11));
                    break;
                case ViewMenuGlyph.MediumIcons:
                    graphics.DrawRectangle(pen, Scale(5), Scale(5), Scale(10), Scale(9));
                    break;
                case ViewMenuGlyph.SmallIcons:
                    DrawGrid(graphics, pen, scale, 4, 4, 5, 2, 2, 2);
                    break;
                case ViewMenuGlyph.List:
                    DrawLines(graphics, pen, scale, 4, 6, 12, 4, 3);
                    break;
                case ViewMenuGlyph.Details:
                    DrawLines(graphics, pen, scale, 3, 6, 14, 4, 3);
                    break;
                case ViewMenuGlyph.Tiles:
                    DrawTileRows(graphics, pen, scale, 2);
                    break;
                case ViewMenuGlyph.Content:
                    DrawTileRows(graphics, pen, scale, 3);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(glyph), glyph, null);
            }
        }

        images.Add(image);
        return image;
    }

    private static void DrawGrid(Graphics graphics, Pen pen, float scale, int x, int y,
        int cellSize, int gap, int columns, int rows)
    {
        int Scale(int logicalPixels) => (int)Math.Round(logicalPixels * scale);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                graphics.DrawRectangle(pen,
                    Scale(x + (column * (cellSize + gap))),
                    Scale(y + (row * (cellSize + gap))),
                    Scale(cellSize),
                    Scale(cellSize));
            }
        }
    }

    private static void DrawLines(Graphics graphics, Pen pen, float scale, int x, int y,
        int width, int spacing, int count)
    {
        int Scale(int logicalPixels) => (int)Math.Round(logicalPixels * scale);
        for (int line = 0; line < count; line++)
            graphics.DrawLine(pen, Scale(x), Scale(y + (line * spacing)),
                Scale(x + width), Scale(y + (line * spacing)));
    }

    private static void DrawTileRows(Graphics graphics, Pen pen, float scale, int count)
    {
        int Scale(int logicalPixels) => (int)Math.Round(logicalPixels * scale);
        int spacing = count == 2 ? 7 : 5;
        int startY = count == 2 ? 6 : 5;
        for (int row = 0; row < count; row++)
        {
            int y = startY + (row * spacing);
            graphics.DrawEllipse(pen, Scale(3), Scale(y - 1), Scale(3), Scale(3));
            graphics.DrawLine(pen, Scale(9), Scale(y), Scale(16), Scale(y));
        }
    }

    private static Image CreateShellMenuIcon(string extension, bool isFolder, char fallbackGlyph,
        List<Image> images, int dpi)
    {
        string iconSource = isFolder
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : extension;
        uint attributes = isFolder
            ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY
            : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        var info = new NativeMethods.SHFILEINFOW();
        if (NativeMethods.SHGetFileInfoW(iconSource, attributes, ref info,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON
                | NativeMethods.SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return CreateBackgroundMenuGlyph(fallbackGlyph, images, dpi);

        try
        {
            using Icon nativeIcon = Icon.FromHandle(info.hIcon);
            int iconSize = (int)Math.Round(20 * Math.Max(96, dpi) / 96f);
            var image = new Bitmap(nativeIcon.ToBitmap(), new Size(iconSize, iconSize));
            images.Add(image);
            return image;
        }
        catch (ArgumentException)
        {
            return CreateBackgroundMenuGlyph(fallbackGlyph, images, dpi);
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    private void OnManagedDetailsRenameRequested(object? sender, ManagedRenameEventArgs e)
    {
        if (string.Equals(Path.GetFileName(e.Path), e.NewName, StringComparison.Ordinal)) return;

        try
        {
            string? folder = Path.GetDirectoryName(e.Path);
            if (string.IsNullOrEmpty(folder)) return;
            string destination = Path.Combine(folder, e.NewName);
            if (Directory.Exists(e.Path))
                Directory.Move(e.Path, destination);
            else if (File.Exists(e.Path))
                File.Move(e.Path, destination);
            else
                return;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OnManagedDetailsRenameRequested),
                $"Could not rename \"{Path.GetFileName(e.Path)}\".");
        }

        ScheduleManagedDetailsRefresh();
    }

    private void OnManagedDetailsQuickLookRequested(object? sender, string path)
    {
        OpenWithQuickLook(path);
    }

    private void OpenWithQuickLook(string path)
    {
        if (!ExplorerHost.QuickLookEnabled || string.IsNullOrWhiteSpace(path)) return;

        QuickLookFailure? failure = ExplorerHost.InvokeQuickLook(path);
        if (failure is { } unavailable)
            ShowQuickLookUnavailable(unavailable);
    }

    private bool TryExecuteManagedDetailsCommand(CommandBar.Cmd cmd, ExplorerHost? host)
    {
        if (!IsManagedDetailsViewActive) return false;

        IReadOnlyList<string> paths = _managedDetailsListView.SelectedPaths;
        switch (cmd)
        {
            case CommandBar.Cmd.NewFolder:
                _managedDetailsListView.CreateNewFolderAndBeginRename();
                return true;
            case CommandBar.Cmd.Cut:
                CopyManagedPathsToClipboard(paths, cut: true);
                return true;
            case CommandBar.Cmd.Copy:
                CopyManagedPathsToClipboard(paths, cut: false);
                return true;
            case CommandBar.Cmd.CopyPaths:
                if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
                return true;
            case CommandBar.Cmd.Paste:
                PasteIntoCurrentFolder();
                return true;
            case CommandBar.Cmd.Rename:
                _managedDetailsListView.BeginRename();
                return true;
            case CommandBar.Cmd.Delete:
                OperationManager.Current?.Start(
                    ModifierKeys.HasFlag(Keys.Shift)
                        ? FileOperationKind.DeletePermanently
                        : FileOperationKind.Delete,
                    paths);
                ScheduleManagedDetailsRefresh();
                return true;
            case CommandBar.Cmd.SelectAll:
                _managedDetailsListView.SelectAllItems();
                return true;
            case CommandBar.Cmd.Properties:
                ShowSelectedProperties(host);
                return true;
            default:
                return false;
        }
    }

    private void CopyManagedPathsToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;

        try
        {
            var files = new StringCollection();
            files.AddRange(paths.ToArray());
            var data = new DataObject();
            data.SetFileDropList(files);
            if (cut)
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(2u)));
            Clipboard.SetDataObject(data, copy: true);
            if (cut)
                ClipboardCutState.SetPaths(paths);
            else
                ClipboardCutState.Clear();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(CopyManagedPathsToClipboard),
                "Could not copy the selected items to the clipboard.");
        }
    }

    private void PasteIntoCurrentFolder()
    {
        ActiveHost?.Paste();
        ClipboardCutState.Clear();
        ScheduleManagedDetailsRefresh();
    }

    private static bool CanPasteFilesFromClipboard()
    {
        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch (ExternalException ex)
        {
            AppLog.Debug(ex, nameof(CanPasteFilesFromClipboard));
            return false;
        }
    }

    private async void OnFilterDebounce(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        await PopulateFilterListAsync(_filterText);
    }

    private void ClearFilter()
    {
        CancelFilterRename();
        _filterDebounce.Stop();
        _filterCancellation?.Cancel();
        _filterText = "";

        _filterTextBox.TextChanged -= OnFilterTextBoxChanged;
        _filterTextBox.Text = "";
        _filterTextBox.TextChanged += OnFilterTextBoxChanged;

        _filterBar.Visible      = false;
        _filterListView.Visible = false;
        SetFilterItems([]);
        _filterUsesNativeColumns = false;
        _nativeFilterColumnsLoaded = false;
        if (ActiveHost != null) ActiveHost.IsFiltering = false;
        PositionExplorerCommandBand();
        PositionManagedDetailsView();
        FocusActiveFileView();
    }

    private void ShowFilterOverlay()
    {
        if (!_filterListView.Visible)
        {
            _filterUsesNativeColumns = !_managedDetailsListView.Visible;
            _nativeFilterColumnsLoaded = false;
        }
        int horizontalOrigin = !_filterUsesNativeColumns
            && _managedDetailsListView.Visible
            ? GetListHorizontalOrigin(_managedDetailsListView)
            : 0;
        if (!_filterUsesNativeColumns)
            SyncFilterColumns();
        if (!_filterBar.Visible)
            _filterBar.Visible = true;  // WinForms resizes _hostContainer; Resize fires ResizeAllHosts.
        _filterListView.BringToFront(); // above ExplorerHosts inside _hostContainer
        _filterListView.Visible = true;
        _managedDetailsListView.Visible = false;
        if (horizontalOrigin > 0 && _filterListView.IsHandleCreated)
            NativeMethods.SendMessageI(_filterListView.Handle,
                NativeMethods.LVM_SCROLL, new IntPtr(horizontalOrigin), IntPtr.Zero);
    }

    private static int GetListHorizontalOrigin(ListView list)
    {
        if (!list.IsHandleCreated) return 0;
        return Math.Max(0, NativeMethods.GetScrollPos(list.Handle,
            NativeMethods.SB_HORZ));
    }

    private async void OnManagedDetailsVisibleColumnsChanged(object? sender,
        EventArgs e)
    {
        if (ActiveHost is { } host
            && !ExplorerHost.IsNetworkPath(host.GetCurrentPath()))
            _localManagedColumns = _managedDetailsListView.VisibleColumnDefinitions;
        if (_filterUsesNativeColumns && _filterListView.Visible) return;
        SyncFilterColumns();
        if (_filterListView.Visible && !string.IsNullOrEmpty(_filterText))
            await PopulateFilterListAsync(_filterText);
    }

    private void SyncFilterColumns()
    {
        ApplyFilterColumns(_managedDetailsListView.VisibleColumnDefinitions);
    }

    private void ApplyFilterColumns(
        IReadOnlyList<ManagedDetailsListView.ColumnDefinition> columns,
        bool cancelPendingFilter = true)
    {
        ArgumentNullException.ThrowIfNull(columns);
        bool sameColumns = _filterColumns.Count == columns.Count
            && !_filterColumns.Where((column, index) =>
                column.Id != columns[index].Id).Any();
        if (sameColumns)
        {
            _filterColumns = columns;
            SyncFilterColumnWidths();
            return;
        }

        string sortColumnId = _filterSortColumn < _filterColumns.Count
            ? _filterColumns[_filterSortColumn].Id
            : "Name";
        if (cancelPendingFilter)
            _filterCancellation?.Cancel();
        SetFilterItems([]);
        _filterColumns = columns;
        _filterListView.Columns.Clear();
        foreach (ManagedDetailsListView.ColumnDefinition column in columns)
            _filterListView.Columns.Add(column.Text,
                LogicalToDeviceUnits(column.PreferredWidth), column.Alignment);
        SyncFilterColumnWidths();
        _filterSortColumn = Enumerable.Range(0, columns.Count)
            .FirstOrDefault(index => columns[index].Id == sortColumnId, -1);
        if (_filterSortColumn >= 0) return;
        _filterSortColumn = 0;
        _filterSortAscending = true;
    }

    private void SyncFilterColumnWidths()
    {
        for (int index = 0; index < _filterColumns.Count; index++)
        {
            int width = !_filterUsesNativeColumns
                && _managedDetailsListView.Columns.Count == _filterColumns.Count
                    ? _managedDetailsListView.Columns[index].Width
                    : LogicalToDeviceUnits(_filterColumns[index].PreferredWidth);
            if (width > 0 && _filterListView.Columns[index].Width != width)
                _filterListView.Columns[index].Width = width;
        }
    }

    private void RestartFilterDebounce()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private async Task PopulateFilterListAsync(string filterText)
    {
        var host = ActiveHost;
        if (host == null) return;
        string folder = host.GetCurrentPath();

        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;

        List<FilterItemData> items;
        try
        {
            if (_filterUsesNativeColumns && !_nativeFilterColumnsLoaded)
            {
                IReadOnlyList<ExplorerShellColumn> nativeColumns =
                    await host.GetVisibleShellColumnsAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested || IsDisposed
                    || host != ActiveHost
                    || !string.Equals(filterText, _filterText,
                        StringComparison.Ordinal))
                    return;

                if (nativeColumns.Count > 0)
                    ApplyFilterColumns(MapNativeFilterColumns(
                        nativeColumns, host.DeviceDpi), cancelPendingFilter: false);
                else
                    AppLog.Warn(null, nameof(PopulateFilterListAsync),
                        "The native file view did not provide its visible columns.");
                _nativeFilterColumnsLoaded = true;
            }

            IReadOnlyList<ManagedDetailsListView.ColumnDefinition> columns =
                _filterColumns;
            DirectorySnapshot snapshot = await DirectorySnapshotCache.GetAsync(
                folder, cancellation.Token);
            items = await Task.Run(
                () => BuildFilterItems(snapshot, filterText, columns,
                    cancellation.Token),
                cancellation.Token);

            if (!ReferenceEquals(columns, _filterColumns))
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppLog.Debug(ex, nameof(PopulateFilterListAsync),
                $"Could not enumerate \"{folder}\" while filtering.");
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(PopulateFilterListAsync),
                $"Filtering failed for \"{folder}\".");
            return;
        }

        if (cancellation.IsCancellationRequested
            || IsDisposed
            || host != ActiveHost
            || !string.Equals(filterText, _filterText, StringComparison.Ordinal))
            return;

        SetFilterItems(items);
    }

    private static IReadOnlyList<ManagedDetailsListView.ColumnDefinition>
        MapNativeFilterColumns(IReadOnlyList<ExplorerShellColumn> nativeColumns,
            int deviceDpi)
    {
        ArgumentNullException.ThrowIfNull(nativeColumns);
        int dpi = Math.Max(96, deviceDpi);
        return nativeColumns.Select(column =>
        {
            string id = column.CanonicalName switch
            {
                "System.ItemNameDisplay" or "System.FileName" => "Name",
                "System.DateModified" => "DateModified",
                "System.ItemTypeText" or "System.ItemType" => "Type",
                "System.Size" => "Size",
                "System.DateCreated" => "DateCreated",
                "System.FileAttributes" => "Attributes",
                "System.FileExtension" => "Extension",
                "System.ItemPathDisplay" => "FullPath",
                _ => string.IsNullOrWhiteSpace(column.CanonicalName)
                    ? $"{column.Key.fmtid:N}:{column.Key.pid}"
                    : column.CanonicalName,
            };
            int logicalWidth = Math.Max(48,
                (int)Math.Round(Math.Max(1, column.Width) * 96.0 / dpi));
            return new ManagedDetailsListView.ColumnDefinition(
                id, column.DisplayName, logicalWidth, logicalWidth,
                id == "Size" ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                id is "Name" or "DateModified" or "Type" or "Size"
                    or "DateCreated" or "Attributes" or "Extension"
                    or "FullPath" ? null : column.Key);
        }).ToArray();
    }

    private void SetFilterItems(IReadOnlyList<FilterItemData> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CancelFilterRename();
        IReadOnlyList<FilterItemData> sortedItems = SortFilterItems(items);

        _filterListView.BeginUpdate();
        try
        {
            _hoveredFilterItemIndex = -1;
            // Clear native selection and focus while their old virtual indexes
            // still exist. Shrinking the list first can make a queued WinForms
            // notification request an item beyond the new VirtualListSize.
            _filterListView.SelectedIndices.Clear();
            if (_filterListView.FocusedItem is { } focusedItem)
                focusedItem.Focused = false;
            _filterItems = sortedItems;
            _filterVirtualItemCache.Clear();
            _filterListView.VirtualListSize = sortedItems.Count;
        }
        finally
        {
            _filterListView.EndUpdate();
        }
        _filterListView.Invalidate();
    }

    private IReadOnlyList<FilterItemData> SortFilterItems(
        IReadOnlyList<FilterItemData> items)
    {
        if (items.Count <= 1 || _filterColumns.Count == 0)
            return items;

        string columnId = _filterColumns[_filterSortColumn].Id;
        int columnIndex = _filterSortColumn;
        var previousOrder = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        if (columnId != "Name")
        {
            for (int index = 0; index < _filterItems.Count; index++)
                previousOrder.TryAdd(_filterItems[index].FullPath, index);
        }
        var comparer = Comparer<FilterItemData>.Create((left, right) =>
        {
            int comparison = columnId switch
            {
                "Name" => CompareText(left.Name, right.Name),
                "Type" => CompareText(left.Type, right.Type),
                "Size" => left.Length.CompareTo(right.Length),
                "DateModified" => left.LastWriteTime.CompareTo(right.LastWriteTime),
                "DateCreated" => left.CreationTime.CompareTo(right.CreationTime),
                "Attributes" => left.Attributes.CompareTo(right.Attributes),
                "Extension" => CompareText(left.Extension, right.Extension),
                "FullPath" => CompareText(left.FullPath, right.FullPath),
                _ => CompareText(left.DisplayValues[columnIndex],
                    right.DisplayValues[columnIndex]),
            };
            return _filterSortAscending ? comparison : -Math.Sign(comparison);
        });
        bool groupFoldersFirst = columnId != "Name"
            || !_sortFoldersWithFilesByName;
        return items.OrderBy(item => groupFoldersFirst && !item.IsDirectory ? 1 : 0)
            .ThenBy(static item => item, comparer)
            .ThenBy(item => previousOrder.GetValueOrDefault(
                item.FullPath, int.MaxValue)).ToArray();
    }

    private static int CompareText(string left, string right) =>
        string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

    private void OnRetrieveFilterItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _filterItems.Count)
        {
            e.Item = new ListViewItem();
            return;
        }

        e.Item = _filterVirtualItemCache.GetOrCreate(e.ItemIndex, () =>
        {
            FilterItemData item = _filterItems[e.ItemIndex];
            return new ListViewItem(item.DisplayValues)
            {
                Tag = item.FullPath,
                ImageIndex = GetFilterIconIndex(item),
            };
        });
    }

    private int GetFilterIconIndex(FilterItemData item)
    {
        string extension = Path.GetExtension(item.Name);
        string key = item.IsDirectory ? "<folder>"
            : string.IsNullOrEmpty(extension) ? "<file>" : extension;
        if (_filterIconIndexes.TryGetValue(key, out int existing))
            return existing;

        var info = new NativeMethods.SHFILEINFOW();
        uint attributes = item.IsDirectory
            ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY
            : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        int iconIndex;
        if (NativeMethods.SHGetFileInfoW(item.Name, attributes, ref info,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON
                | NativeMethods.SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
        {
            using Bitmap fallback = SystemIcons.Application.ToBitmap();
            _filterIcons.Images.Add(fallback);
            iconIndex = _filterIcons.Images.Count - 1;
        }
        else
        {
            try
            {
                using Icon icon = Icon.FromHandle(info.hIcon);
                using Bitmap bitmap = icon.ToBitmap();
                _filterIcons.Images.Add(bitmap);
                iconIndex = _filterIcons.Images.Count - 1;
            }
            catch (Exception ex) when (ex is ArgumentException
                or ExternalException)
            {
                AppLog.Debug(ex, nameof(GetFilterIconIndex),
                    $"Could not load the icon for \"{item.Name}\".");
                using Bitmap fallback = SystemIcons.Application.ToBitmap();
                _filterIcons.Images.Add(fallback);
                iconIndex = _filterIcons.Images.Count - 1;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }

        _filterIconIndexes[key] = iconIndex;
        return iconIndex;
    }

    private string? GetFilterPathAt(int index) => index >= 0 && index < _filterItems.Count
        ? _filterItems[index].FullPath
        : null;

    private static List<FilterItemData> BuildFilterItems(
        DirectorySnapshot snapshot,
        string filterText,
        IReadOnlyList<ManagedDetailsListView.ColumnDefinition> columns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(columns);
        var items = new List<FilterItemData>();
        var comparison = StringComparison.OrdinalIgnoreCase;

        foreach (DirectorySnapshotItem child in snapshot.Items
                     .Where(item => item.Name.Contains(filterText, comparison))
                     .OrderBy(static item => item.IsDirectory ? 0 : 1)
                     .ThenBy(static item => item.Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = child.Extension.TrimStart('.').ToUpperInvariant();
            items.Add(new FilterItemData(
                child.Name, child.IsDirectory ? "Folder"
                    : extension.Length > 0 ? $"{extension} file" : "File",
                child.FullPath,
                child.IsDirectory,
                child.Length,
                child.LastWriteTime,
                child.CreationTime,
                child.Attributes,
                child.Extension,
                ManagedDetailsListView.GetFilterDisplayValues(child, columns)));
        }

        return items;
    }

    private readonly record struct FilterItemData(
        string Name, string Type, string FullPath, bool IsDirectory,
        long Length, DateTime LastWriteTime, DateTime CreationTime,
        FileAttributes Attributes, string Extension, string[] DisplayValues);

    private sealed class FilterListView : ListView
    {
        internal FilterListView()
        {
            DoubleBuffered = true;
            OwnerDraw = true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ExplorerHost? ActiveHost
        => _active >= 0 && _active < _hosts.Count ? _hosts[_active] : null;

    private static string FolderLabel(string path)
    {
        if (string.IsNullOrEmpty(path)) return "New tab";
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path.TrimEnd(Path.DirectorySeparatorChar) : name;
    }

    private void UpdateCommandBarToggles()
    {
        var host = ActiveHost;
        _explorerCommandBand.SetPreviewPaneVisible(_previewRegion.Visible);
        _commandBar.UpdateViewToggles(
            detailsPane:    _detailsPanel.Visible,
            previewPane:    _previewRegion.Visible,
            navPane:        host?.IsNavPaneVisible ?? true,
            compactView:    host?.CompactViewEnabled ?? false,
            checkboxes:     host?.ItemCheckBoxesEnabled ?? false,
            fileExtensions: host?.FileExtensionsVisible ?? true,
            hiddenItems:    host?.HiddenItemsVisible ?? false,
            quickLook:      ExplorerHost.QuickLookEnabled);
    }

    private void ApplyActiveTabViewSettings()
    {
        ApplyManagedDetailsDisplaySettings();
        ActiveHost?.RefreshItemCountStatus(force: true);
        UpdateCommandBarToggles();
    }

    private void ApplyManagedDetailsDisplaySettings()
    {
        if (ActiveHost is not { } host) return;

        _managedDetailsListView.ApplyShellDisplaySettings(
            compactMode: host.CompactViewEnabled,
            showItemCheckBoxes: host.ItemCheckBoxesEnabled,
            showFileExtensions: host.FileExtensionsVisible,
            showHiddenItems: host.HiddenItemsVisible);
    }

    // ── Command bar dispatch ─────────────────────────────────────────────────

    internal void ExecuteCommand(CommandBar.Cmd cmd) => OnCommandIssued(this, cmd);

    private void ShowSelectedProperties(ExplorerHost? host)
    {
        if (_filterListView.Visible)
        {
            if (GetSelectedFilterPath() is { } path)
                ShowPathProperties([path]);
            return;
        }

        if (IsManagedDetailsViewActive)
        {
            ShowPathProperties(_managedDetailsListView.SelectedPaths);
            return;
        }

        host?.ShowProperties();
    }

    private void ShowPathProperties(IReadOnlyList<string> paths)
    {
        UseShellItemContextMenu(paths, this, (contextMenu, _) =>
        {
            IntPtr ownerWindow = FindForm()?.Handle ?? Handle;
            if (!ShellContextMenu.TryInvokeNamedShellCommand(
                    contextMenu, ownerWindow, prepareExtensionVerbs: true,
                    verbs: ["properties"]))
                AppLog.Warn(null, nameof(ShowPathProperties),
                    "Windows did not open Properties for the selected item(s).");
        });
    }

    private void OnCommandIssued(object? sender, CommandBar.Cmd cmd)
    {
        var host = ActiveHost;
        if (cmd is CommandBar.Cmd.CopyToOtherPane
            or CommandBar.Cmd.MoveToOtherPane)
        {
            if (host is not null)
                RequestTransferToOtherPane(
                    cmd == CommandBar.Cmd.CopyToOtherPane
                        ? FileOperationKind.Copy : FileOperationKind.Move,
                    GetSelectedPaths(host));
            return;
        }
        if (cmd == CommandBar.Cmd.Rename && _filterListView.Visible)
        {
            BeginFilterRename();
            return;
        }
        if (TryExecuteManagedDetailsCommand(cmd, host)) return;

        switch (cmd)
        {
            // File operations
            case CommandBar.Cmd.NewFolder:      host?.CreateNewFolder();  break;
            case CommandBar.Cmd.NewTab:         AddTab(CurrentPath());    break;
            case CommandBar.Cmd.Cut:            host?.Cut();              break;
            case CommandBar.Cmd.Copy:           host?.Copy();             break;
            case CommandBar.Cmd.CopyPaths:      host?.CopySelectedPaths(); break;
            case CommandBar.Cmd.Paste:          host?.Paste();            break;
            case CommandBar.Cmd.Rename:         host?.Rename();           break;
            case CommandBar.Cmd.Delete:         host?.Delete();           break;
            case CommandBar.Cmd.SelectAll:      host?.SelectAll();        break;
            case CommandBar.Cmd.Properties:     ShowSelectedProperties(host); break;
            case CommandBar.Cmd.EditAddressBar: _pathBar.EditAddress();   break;
            case CommandBar.Cmd.FolderOptions:  OpenFolderOptions();      break;
            case CommandBar.Cmd.Settings:       SettingsRequested?.Invoke(this, EventArgs.Empty); break;
            case CommandBar.Cmd.Help:           _ = OpenHelpPageAsync();  break;
            case CommandBar.Cmd.About:          ShowAbout();              break;
            case CommandBar.Cmd.ViewLog:        AppLog.OpenLogFile();                        break;
            case CommandBar.Cmd.SetHotkey:      SetHotkeyRequested?.Invoke(this, EventArgs.Empty); break;
            case CommandBar.Cmd.ToggleStartWithWindows:
                StartWithWindowsToggled?.Invoke(this, EventArgs.Empty);
                break;
            case CommandBar.Cmd.ToggleMinimizeToTray:
                MinimizeToTrayToggled?.Invoke(this, EventArgs.Empty);
                break;
            case CommandBar.Cmd.GoToParent:     host?.NavigateUp();                               break;
            case CommandBar.Cmd.Refresh:        RefreshCurrentFolder();                           break;
            case CommandBar.Cmd.MirrorToOther:
                MirrorToOtherRequested?.Invoke(this, CurrentPath());
                break;
            case CommandBar.Cmd.ComparePanes:
                ComparePanesRequested?.Invoke(this, EventArgs.Empty);
                break;

            // View modes
            case CommandBar.Cmd.ViewDetails:
                SetManagedBackgroundView(4, null); // FVM_DETAILS
                break;
            case CommandBar.Cmd.ViewList:
                SetManagedBackgroundView(3, null); // FVM_LIST
                break;
            case CommandBar.Cmd.ViewTiles:
                SetManagedBackgroundView(6, null); // FVM_TILE
                break;
            case CommandBar.Cmd.ViewIcons:
                SetManagedBackgroundView(1, 96); // FVM_ICON
                break;
            case CommandBar.Cmd.ViewMediumIcons:
                SetManagedBackgroundView(1, 48); // FVM_ICON
                break;
            case CommandBar.Cmd.ViewSmallIcons:
                // Modern Explorer implements Small icons as FVM_ICON with a
                // 16-pixel image size; legacy FVM_SMALLICON is ignored.
                SetManagedBackgroundView(1, 16);
                break;
            case CommandBar.Cmd.ViewContent:
                SetManagedBackgroundView(8, null); // FVM_CONTENT
                break;

            // Pane toggles
            case CommandBar.Cmd.ToggleDetailsPane:
                _detailsPanel.Visible = !_detailsPanel.Visible;
                if (!_detailsPanel.Visible) _detailsPanel.ShowItem(null);
                UpdateCommandBarToggles();
                break;
            case CommandBar.Cmd.TogglePreviewPane:
                bool showPreviewPane = !_previewRegion.Visible;
                _content.SuspendLayout();
                _previewRegion.Visible = showPreviewPane;
                _content.ResumeLayout(performLayout: true);
                if (_previewRegion.Visible)
                {
                    ConstrainPreviewPaneWidth();
                    _previewPanel.Preview(GetSelectedPathForPreview());
                }
                else
                    _previewPanel.Preview(null);
                UpdateCommandBarToggles();
                break;

            // Show submenu
            case CommandBar.Cmd.ShowNavPane:
                host?.ToggleNavPane();
                UpdateCommandBarToggles();
                break;
            case CommandBar.Cmd.ShowCompactView:
                host?.ToggleCompactView();
                ApplyActiveTabViewSettings();
                break;
            case CommandBar.Cmd.ShowCheckboxes:
                host?.ToggleItemCheckBoxes();
                ApplyActiveTabViewSettings();
                break;
            case CommandBar.Cmd.ShowFileExtensions:
                host?.ToggleFileExtensions();
                ApplyActiveTabViewSettings();
                break;
            case CommandBar.Cmd.ShowHiddenItems:
                host?.ToggleHiddenItems();
                ApplyActiveTabViewSettings();
                break;

            case CommandBar.Cmd.ToggleQuickLook:
                ExplorerHost.QuickLookEnabled = !ExplorerHost.QuickLookEnabled;
                QuickLookToggled?.Invoke(this, EventArgs.Empty);
                UpdateCommandBarToggles();
                break;

            case CommandBar.Cmd.Exit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static void OpenFolderOptions()
        => ExplorerOptions.Open();

    private async Task OpenHelpPageAsync(CancellationToken cancellationToken = default)
    {
        string? helpPath;

        try
        {
            helpPath = await ResolveHelpPagePathAsync(
                AppContext.BaseDirectory, GetHelpRecoveryDirectory(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OpenHelpPageAsync),
                "Could not recover the MultiExplorer help page.");
            ShowHelpUnavailable();
            return;
        }

        if (helpPath is null)
        {
            AppLog.Warn(null, nameof(OpenHelpPageAsync),
                "The installed and embedded MultiExplorer help files are unavailable.");
            ShowHelpUnavailable();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = helpPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OpenHelpPageAsync),
                "Could not open the MultiExplorer help page.");
            ShowHelpUnavailable();
        }
    }

    internal static async Task<string?> ResolveHelpPagePathAsync(
        string applicationDirectory,
        string recoveryDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryDirectory);

        string installedPath = Path.Combine(applicationDirectory, HelpFileName);
        if (File.Exists(installedPath)) return installedPath;

        await HelpFileGate.WaitAsync(cancellationToken);
        try
        {
            string recoveredPath = Path.Combine(recoveryDirectory, HelpFileName);
            if (File.Exists(recoveredPath)) return recoveredPath;

            await using Stream? embeddedHelp = typeof(PanelView).Assembly
                .GetManifestResourceStream(HelpResourceName);
            if (embeddedHelp is null) return null;

            Directory.CreateDirectory(recoveryDirectory);
            string stagingPath = Path.Combine(
                recoveryDirectory, $".{HelpFileName}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var destination = new FileStream(
                    stagingPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await embeddedHelp.CopyToAsync(destination, cancellationToken);
                }

                File.Move(stagingPath, recoveredPath, overwrite: true);
                AppLog.Debug(nameof(ResolveHelpPagePathAsync),
                    $"Recovered the missing help page to \"{recoveredPath}\".");
                return recoveredPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(stagingPath)) File.Delete(stagingPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Debug(ex, nameof(ResolveHelpPagePathAsync),
                        "Could not remove a temporary help recovery file.");
                }
            }
        }
        finally
        {
            HelpFileGate.Release();
        }
    }

    private static string GetHelpRecoveryDirectory()
    {
        Version? version = typeof(PanelView).Assembly.GetName().Version;
        string versionFolder = version?.ToString(3) ?? "Unknown";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiExplorer",
            "Help",
            versionFolder);
    }

    private void ShowHelpUnavailable()
    {
        MessageBox.Show(
            this,
            "MultiExplorer could not open its help page. The installed help file is " +
            "missing and the built-in recovery copy could not be restored.\n\n" +
            "Repair or reinstall MultiExplorer to restore help.",
            "Help unavailable",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void ShowQuickLookUnavailable(QuickLookFailure failure)
    {
        string message = failure switch
        {
            QuickLookFailure.NotInstalled =>
                "QuickLook is not installed. You can download it from the QuickLook project page:",
            QuickLookFailure.NotRunning =>
                "QuickLook is installed but is not running. Start QuickLook, then press Space again.",
            _ =>
                "MultiExplorer could not communicate with QuickLook. Ensure QuickLook is running, then press Space again.",
        };

        using var dialog = new Form
        {
            Text            = "QuickLook unavailable",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox     = false,
            MinimizeBox     = false,
            ShowInTaskbar   = false,
            StartPosition   = FormStartPosition.CenterParent,
            ClientSize      = new Size(520, 190),
            Font            = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f),
        };

        var messageLabel = new Label
        {
            Text      = message,
            Dock      = DockStyle.Fill,
            AutoSize  = true,
            MaximumSize = new Size(472, 0),
        };
        var link = new LinkLabel
        {
            Text     = QuickLookProjectUrl,
            AutoSize = true,
            TabStop  = true,
        };
        link.LinkClicked += (_, _) => OpenWebLink(QuickLookProjectUrl);

        var okButton = new RoundedButton
        {
            Text         = "OK",
            DialogResult = DialogResult.OK,
            AutoSize     = true,
            MinimumSize  = new Size(88, 30),
            Anchor       = AnchorStyles.Right,
        };

        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            Padding     = new Padding(24),
            ColumnCount = 1,
            RowCount    = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(messageLabel, 0, 0);
        layout.Controls.Add(link, 0, 1);
        layout.Controls.Add(okButton, 0, 2);

        dialog.AcceptButton = okButton;
        dialog.CancelButton = okButton;
        dialog.Controls.Add(layout);
        ThemeManager.ApplyTo(dialog);
        link.LinkColor       = ThemeManager.AccentText;
        link.ActiveLinkColor = ThemeManager.Accent;
        dialog.Shown += (_, _) => ThemeManager.ApplyNativeWindow(dialog.Handle);
        dialog.ShowDialog(this);
    }

    private static void OpenWebLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OpenWebLink), $"Could not open {url}.");
        }
    }

    private void ShowAbout() => ShowAboutDialog(this);

    /// <summary>Shows the About dialog; callable from any owner (toolbar button, tray icon, etc.).</summary>
    internal static void ShowAboutDialog(IWin32Window owner)
    {
        Version? assemblyVersion = typeof(Program).Assembly.GetName().Version;
        string   version         = assemblyVersion?.ToString(3) ?? "Unknown";
        string   exePath   = Environment.ProcessPath ?? "";
        DateTime buildDate = string.IsNullOrEmpty(exePath) ? DateTime.Now
                                 : File.GetLastWriteTime(exePath);

        using var dialog = new Form
        {
            Text            = "About MultiExplorer",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox     = false,
            MinimizeBox     = false,
            ShowInTaskbar   = false,
            StartPosition   = FormStartPosition.CenterParent,
            ClientSize      = new Size(600, 540),
            Font            = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f),
        };

        var content = new RichTextBox
        {
            Dock           = DockStyle.Fill,
            BorderStyle    = BorderStyle.None,
            ReadOnly       = true,
            DetectUrls     = true,
            HideSelection  = false,
            ShortcutsEnabled = true,
            ScrollBars     = RichTextBoxScrollBars.Vertical,
            WordWrap       = true,
            Margin         = Padding.Empty,
        };

        using var titleFont   = new Font(dialog.Font.FontFamily, 16f, FontStyle.Bold);
        using var sectionFont = new Font(dialog.Font, FontStyle.Bold);

        using var copyMenu = new ContextMenuStrip();
        var copyItem = new ToolStripMenuItem("Copy");
        copyItem.Click += (_, _) => content.Copy();
        var selectAllItem = new ToolStripMenuItem("Select all");
        selectAllItem.Click += (_, _) => content.SelectAll();
        copyMenu.Items.AddRange([copyItem, selectAllItem]);
        copyMenu.Opening += (_, _) => copyItem.Enabled = content.SelectionLength > 0;
        content.ContextMenuStrip = copyMenu;
        content.LinkClicked += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.LinkText))
                OpenWebLink(e.LinkText);
        };

        var okButton = new RoundedButton
        {
            Text         = "OK",
            DialogResult = DialogResult.OK,
            AutoSize     = true,
            MinimumSize  = new Size(88, 30),
            Anchor       = AnchorStyles.Right,
            Margin       = new Padding(0, 16, 0, 0),
        };

        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            Padding     = new Padding(24),
            ColumnCount = 1,
            RowCount    = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(content, 0, 0);
        layout.Controls.Add(okButton, 0, 1);

        dialog.AcceptButton = okButton;
        dialog.CancelButton = okButton;
        dialog.Controls.Add(layout);

        // This dialog is built entirely in code, so establish the logical design
        // DPI after its control tree exists.  WinForms can then scale the window,
        // wrapping widths, padding and button size together on the owner's monitor.
        dialog.AutoScaleDimensions = new SizeF(96f, 96f);
        dialog.AutoScaleMode       = AutoScaleMode.Dpi;

        ThemeManager.ApplyTo(dialog);
        ThemeManager.ApplyToolStrip(copyMenu);

        // A single read-only rich edit control keeps all About text selectable while
        // retaining the hierarchy that the former collection of labels provided.
        // Populate it after applying the theme so inserted text uses the correct
        // foreground colour in both light and dark modes.
        content.BackColor = ThemeManager.Background;
        void Append(string value, Font font)
        {
            content.SelectionStart  = content.TextLength;
            content.SelectionLength = 0;
            content.SelectionFont   = font;
            content.SelectionColor  = ThemeManager.Text;
            content.AppendText(value);
        }

        void AppendDetail(string name, string value)
        {
            Append(name.PadRight(13), sectionFont);
            Append(value + Environment.NewLine, dialog.Font);
        }

        Append("MultiExplorer" + Environment.NewLine, titleFont);
        Append("A dual-pane file explorer for Windows" + Environment.NewLine + Environment.NewLine,
               dialog.Font);
        Append("Licence" + Environment.NewLine, sectionFont);
        Append(
            "MultiExplorer is public domain software, released under the Creative " +
            "Commons CC0 1.0 Universal Public Domain Dedication. You may copy, " +
            "modify, distribute, and use it commercially without asking permission." +
            Environment.NewLine + Environment.NewLine,
            dialog.Font);
        Append("Privacy" + Environment.NewLine, sectionFont);
        Append(
            "MultiExplorer operates entirely offline, contains no advertisements, " +
            "and collects no user data, analytics, or personal information." +
            Environment.NewLine + Environment.NewLine,
            dialog.Font);
        Append("Third-party acknowledgement" + Environment.NewLine, sectionFont);
        Append(
            QuickLookAcknowledgementText + Environment.NewLine + Environment.NewLine,
            dialog.Font);
        Append("Build information" + Environment.NewLine, sectionFont);
        AppendDetail("Version", version);
        AppendDetail("Last build", $"{buildDate:d MMMM yyyy, h:mm tt}");
        AppendDetail("Author", "David Piscopo");
        content.Select(0, 0);

        dialog.Shown += (_, _) => ThemeManager.ApplyNativeWindow(dialog.Handle);
        dialog.ShowDialog(owner);
    }
}
