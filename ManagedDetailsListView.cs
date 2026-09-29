using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CheckBoxState = System.Windows.Forms.VisualStyles.CheckBoxState;

namespace MultiExplorer;

/// <summary>
/// Application-owned responsive Details view displayed over the Shell file pane.
/// The native ExplorerBrowser remains alive underneath it for navigation, shell
/// integration, and the other native view modes.
/// </summary>
internal sealed class ManagedDetailsListView : ListView
{
    private const int WmSize = 0x0005;
    private const int WmPaint = 0x000F;
    private const int WmNcPaint = 0x0085;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonUp = 0x00A2;
    private const int WmTimer = 0x0113;
    private const int WmVerticalScroll = 0x0115;
    private const int WmSystemTimer = 0x0118;
    private const int WmMouseWheel = 0x020A;
    private const int WmCaptureChanged = 0x0215;
    private const int WmNcMouseLeave = 0x02A2;
    private const int MaximumAutomaticNameWidth = 520;
    private const int AdditionalRowPadding = 5;
    private const int CompactMinimumRowHeight = 24;
    private const int StandardMinimumRowHeight = 32;
    private const int IconPadding = 4;
    private const int MkShift = 0x0004;
    private const int MkControl = 0x0008;
    private const string DropDescriptionFormat = "DropDescription";
    internal const int VirtualizationThreshold = 512;
    private CancellationTokenSource? _loadCancellation;
    private readonly System.Windows.Forms.Timer _fileSystemChangeDebounce;
    private readonly ImageList _rowHeightImageList;
    private readonly DragDropFeedbackWindow _dragFeedbackWindow = new();
    private Bitmap? _rowHeightImage;
    private readonly List<Bitmap> _rowIconImages = [];
    private readonly Dictionary<string, int> _rowIconIndexes =
        new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _folderWatchSubscription;
    private string _folderPath = string.Empty;
    private bool _showHiddenItems;
    private bool _showFileExtensions = true;
    private bool _compactMode;
    private bool _synchronizingItemChecks;
    private bool _applyingWidths;
    private bool _hasInputFocus;
    private int _hoveredItemIndex = -1;
    private int _dragPaintItemIndex = -1;
    private string _sortColumnId = "Name";
    private bool _sortAscending = true;
    private bool _sortFoldersWithFilesByName;
    private ManagedDetailsGroupColumn? _groupColumn;
    private bool _groupAscending = true;
    private string? _pendingRenamePath;
    private DateTime _suppressFolderRefreshUntilUtc;
    private long _lastBlankAreaClickTick;
    private Point _lastBlankAreaClickPoint;
    private IReadOnlyList<ManagedFileItem> _entries = [];
    private IReadOnlyList<ManagedFileItem> _displayEntries = [];
    private string _completedLoadPath = string.Empty;
    private readonly VirtualListItemCache _virtualItemCache = new();
    private bool _materializeForInlineRename;
    private int _editingItemIndex = -1;
    private bool _ownedResourcesDisposed;
    private IReadOnlyList<ColumnDefinition>? _availableColumns;
    private readonly HeaderWindowHook _headerWindowHook;
    private ColumnChooserPopup? _columnChooserPopup;
    private bool _columnChooserPending;
    private readonly List<ColumnDefinition> _visibleColumns =
    [
        new("Name", "Name", 180, 180),
        new("DateModified", "Date modified", 120, 120),
        new("Type", "Type", 100, 100),
        new("Size", "Size", 80, 80, HorizontalAlignment.Right),
    ];

    internal event EventHandler<string>? ItemActivatedPath;
    internal event EventHandler<ManagedContextMenuEventArgs>? ContextMenuRequested;
    internal event EventHandler<ManagedBackgroundContextMenuEventArgs>? BackgroundContextMenuRequested;
    internal event EventHandler<ManagedRenameEventArgs>? RenameRequested;
    internal event EventHandler<CommandBar.Cmd>? CommandRequested;
    internal event EventHandler<string>? QuickLookRequested;
    internal event EventHandler<char>? FilterCharInput;
    internal event EventHandler<string>? DirectoryLoadCompleted;
    internal event EventHandler? VisibleColumnsChanged;

    internal ManagedDetailsListView()
    {
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        LabelEdit = true;
        MultiSelect = true;
        AllowDrop = true;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        OwnerDraw = true;
        DoubleBuffered = true;
        _headerWindowHook = new HeaderWindowHook(this);
        _rowHeightImageList = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
        SmallImageList = _rowHeightImageList;
        UpdateRowHeight();

        RebuildColumns();

        ItemActivate += OnItemActivate;
        MouseUp += OnMouseUp;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        KeyDown += OnKeyDown;
        ItemCheck += OnItemCheck;
        ItemSelectionChanged += OnItemSelectionChanged;
        RetrieveVirtualItem += OnRetrieveVirtualItem;
        AfterLabelEdit += OnAfterLabelEdit;
        DrawColumnHeader += OnDrawColumnHeader;
        DrawItem += OnDrawItem;
        DrawSubItem += OnDrawSubItem;
        ColumnClick += OnColumnClick;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        DragDrop += OnDragDrop;
        ItemDrag += OnItemDrag;
        ColumnWidthChanging += OnColumnWidthChanging;
        ClipboardCutState.Changed += OnClipboardCutStateChanged;

        _fileSystemChangeDebounce = new System.Windows.Forms.Timer { Interval = 250 };
        _fileSystemChangeDebounce.Tick += (_, _) =>
        {
            _fileSystemChangeDebounce.Stop();
            Reload(_showHiddenItems);
        };
    }

    internal string? SelectedPath => SelectedIndices.Count == 0
        ? null
        : GetPathAt(SelectedIndices[0]);

    internal bool HasCompletedLoad(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(path, _completedLoadPath,
            StringComparison.OrdinalIgnoreCase);

    internal IReadOnlyList<string> SelectedPaths => SelectedIndices
        .Cast<int>()
        .Select(GetPathAt)
        .OfType<string>()
        .ToArray();

    internal void SelectAllItems()
    {
        BeginUpdate();
        try
        {
            if (VirtualMode)
            {
                for (int index = 0; index < VirtualListSize; index++)
                    Items[index].Selected = true;
            }
            else
            {
                foreach (ListViewItem item in Items)
                    item.Selected = true;
            }
        }
        finally
        {
            EndUpdate();
        }
    }

    internal ManagedDetailsSortColumn? SortColumn => _sortColumnId switch
    {
        "Name" => ManagedDetailsSortColumn.Name,
        "DateModified" => ManagedDetailsSortColumn.DateModified,
        "Type" => ManagedDetailsSortColumn.Type,
        "Size" => ManagedDetailsSortColumn.Size,
        _ => null,
    };

    internal bool IsSortAscending => _sortAscending;

    internal void SetSortFoldersWithFilesByName(bool enabled)
    {
        if (_sortFoldersWithFilesByName == enabled) return;
        _sortFoldersWithFilesByName = enabled;
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Invalidate(true);
    }

    internal IReadOnlyList<ColumnDefinition> VisibleColumnDefinitions =>
        _visibleColumns.ToArray();

    internal void SetVisibleColumns(IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0 || columns.All(static column => column.Id != "Name"))
            return;
        if (_visibleColumns.SequenceEqual(columns)) return;

        _visibleColumns.Clear();
        _visibleColumns.AddRange(columns);
        if (_visibleColumns.All(column => column.Id != _sortColumnId))
        {
            _sortColumnId = "Name";
            _sortAscending = true;
        }
        RebuildColumns();
        VisibleColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal ManagedDetailsGroupColumn? GroupColumn => _groupColumn;

    internal bool IsGroupAscending => _groupAscending;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // This list is normally hidden while the Shell view starts, so its native
        // handle may be created after the main theme pass has finished. Apply the
        // native theme here as well so Windows draws its scroll bars using the
        // current light or dark colours.
        ThemeManager.ApplyNativeWindow(Handle, includeChildren: false);
        InstallHeaderWindowHook();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _headerWindowHook.Detach();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);

        if (!ThemeManager.IsDark)
            return;

        switch (message.Msg)
        {
            case WmSize:
            case WmPaint:
            case WmNcPaint:
            case WmNcMouseMove:
            case WmNcLeftButtonDown:
            case WmNcLeftButtonUp:
            case WmTimer:
            case WmVerticalScroll:
            case WmSystemTimer:
            case WmMouseWheel:
            case WmCaptureChanged:
            case WmNcMouseLeave:
                DrawDarkVerticalScrollBar();
                break;
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateRowHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateRowHeight();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (IsFilterCharacter(e.KeyChar))
        {
            FilterCharInput?.Invoke(this, e.KeyChar);
            e.Handled = true;
            return;
        }

        base.OnKeyPress(e);
    }

    internal static bool IsFilterCharacter(char character) =>
        character >= 0x20 && character != 0x7F && character != ' ';

    internal void ShowDirectory(string path, bool showHiddenItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _folderPath = path;
        _showHiddenItems = showHiddenItems;
        WatchFolder(path);
        StartLoad(path, showHiddenItems);
    }

    internal void Reload(bool showHiddenItems)
    {
        if (string.IsNullOrWhiteSpace(_folderPath)) return;
        _showHiddenItems = showHiddenItems;
        StartLoad(_folderPath, showHiddenItems);
    }

    internal void CancelDirectoryLoad()
    {
        _loadCancellation?.Cancel();
        _completedLoadPath = string.Empty;
    }

    internal void ApplyShellDisplaySettings(
        bool compactMode,
        bool showItemCheckBoxes,
        bool showFileExtensions,
        bool showHiddenItems)
    {
        bool rowHeightChanged = _compactMode != compactMode;
        bool directoryDisplayChanged = _showFileExtensions != showFileExtensions
            || _showHiddenItems != showHiddenItems;

        _compactMode = compactMode;
        _showFileExtensions = showFileExtensions;
        _showHiddenItems = showHiddenItems;

        if (CheckBoxes != showItemCheckBoxes)
        {
            HashSet<string> previousSelections = SelectedPaths.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            _synchronizingItemChecks = true;
            try
            {
                if (showItemCheckBoxes)
                    DisableVirtualMode();
                CheckBoxes = showItemCheckBoxes;
                PopulateItems(previousSelections);
            }
            finally
            {
                _synchronizingItemChecks = false;
            }
        }

        if (rowHeightChanged)
            UpdateRowHeight();
        if (directoryDisplayChanged && !string.IsNullOrWhiteSpace(_folderPath))
            StartLoad(_folderPath, _showHiddenItems);

        Invalidate(true);
    }

    internal void BeginRename()
    {
        int itemIndex = FocusedItem?.Index
            ?? (SelectedIndices.Count > 0 ? SelectedIndices[0] : -1);
        string? path = GetPathAt(itemIndex);
        if (path is not null)
            BeginInlineRename(path);
    }

    internal void CreateNewFolderAndBeginRename()
    {
        if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath)) return;

        string name = "New folder";
        string path = Path.Combine(_folderPath, name);
        for (int suffix = 2; Directory.Exists(path) || File.Exists(path); suffix++)
        {
            name = $"New folder ({suffix})";
            path = Path.Combine(_folderPath, name);
        }

        try
        {
            Directory.CreateDirectory(path);
            BeginRenameCreatedItem(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ManagedDetailsListView),
                "Could not create the new folder.");
        }
    }

    internal void BeginRenameCreatedItem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!ExplorerHost.PathsReferToSameFolder(
                Path.GetDirectoryName(path), _folderPath))
            return;

        DirectorySnapshotCache.Invalidate(_folderPath);
        _pendingRenamePath = path;
        _suppressFolderRefreshUntilUtc = DateTime.UtcNow.AddSeconds(5);
        Reload(_showHiddenItems);
        StartRenameRetry(path);
    }

    internal void SortBy(ManagedDetailsSortColumn column)
    {
        _sortColumnId = column switch
        {
            ManagedDetailsSortColumn.Name => "Name",
            ManagedDetailsSortColumn.DateModified => "DateModified",
            ManagedDetailsSortColumn.Type => "Type",
            ManagedDetailsSortColumn.Size => "Size",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
        };
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Invalidate(true);
    }

    internal void SetSortDirection(bool ascending)
    {
        _sortAscending = ascending;
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Invalidate(true);
    }

    internal void SetSortState(string columnId, bool ascending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnId);
        if (_visibleColumns.All(column => column.Id != columnId)) return;
        if (_sortColumnId == columnId && _sortAscending == ascending) return;
        _sortColumnId = columnId;
        _sortAscending = ascending;
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Invalidate(true);
    }

    internal void GroupBy(ManagedDetailsGroupColumn column)
    {
        _groupColumn = column;
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    internal void SetGroupDirection(bool ascending)
    {
        _groupAscending = ascending;
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    internal void OpenColumnChooser() => RequestColumnChooser();

    internal void ApplyTheme()
    {
        BackColor = ThemeManager.Window;
        ForeColor = ThemeManager.Text;
        if (IsHandleCreated)
            ThemeManager.ApplyNativeWindow(Handle, includeChildren: false);
        Invalidate(true);
        if (ThemeManager.IsDark && IsHandleCreated)
            BeginInvoke(DrawDarkVerticalScrollBar);
    }

    private void DrawDarkVerticalScrollBar()
    {
        if (!IsHandleCreated
            || (NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_STYLE)
                & NativeMethods.WS_VSCROLL) == 0
            || !NativeMethods.GetWindowRect(Handle, out NativeMethods.RECT windowRectangle)
            || !NativeMethods.GetClientRect(Handle, out NativeMethods.RECT clientRectangle))
            return;

        int windowWidth = windowRectangle.Right - windowRectangle.Left;
        int windowHeight = windowRectangle.Bottom - windowRectangle.Top;
        int scrollBarLeft = clientRectangle.Right;
        int scrollBarWidth = windowWidth - scrollBarLeft;
        if (scrollBarWidth <= 0 || windowHeight <= 0)
            return;

        IntPtr deviceContext = NativeMethods.GetWindowDC(Handle);
        if (deviceContext == IntPtr.Zero)
            return;

        try
        {
            using Graphics graphics = Graphics.FromHdc(deviceContext);
            using var trackBrush = new SolidBrush(ThemeManager.Surface);
            using var thumbBrush = new SolidBrush(ThemeManager.NavigationBorder);
            using var arrowPen = new Pen(ThemeManager.MutedText,
                Math.Max(1f, DeviceDpi / 96f));

            var scrollBarBounds = new Rectangle(
                scrollBarLeft, 0, scrollBarWidth, windowHeight);
            graphics.FillRectangle(trackBrush, scrollBarBounds);

            int buttonHeight = Math.Min(scrollBarWidth, windowHeight / 2);
            DrawScrollArrow(graphics, arrowPen,
                new Rectangle(scrollBarLeft, 0, scrollBarWidth, buttonHeight), pointsUp: true);
            DrawScrollArrow(graphics, arrowPen,
                new Rectangle(scrollBarLeft, windowHeight - buttonHeight,
                    scrollBarWidth, buttonHeight), pointsUp: false);

            var scrollInfo = new NativeMethods.SCROLLINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SCROLLINFO>(),
                fMask = NativeMethods.SIF_ALL,
            };
            if (!NativeMethods.GetScrollInfo(Handle, NativeMethods.SB_VERT, ref scrollInfo))
                return;

            int trackTop = buttonHeight;
            int trackHeight = Math.Max(0, windowHeight - (buttonHeight * 2));
            long range = (long)scrollInfo.nMax - scrollInfo.nMin + 1;
            if (trackHeight == 0 || range <= 0 || scrollInfo.nPage >= range)
                return;

            int minimumThumbHeight = Math.Min(trackHeight,
                Math.Max(LogicalToDeviceUnits(18), scrollBarWidth));
            int thumbHeight = Math.Clamp(
                (int)((long)trackHeight * scrollInfo.nPage / range),
                minimumThumbHeight,
                trackHeight);
            long maximumPosition = Math.Max(1,
                range - Math.Max(1L, scrollInfo.nPage));
            long currentPosition = Math.Clamp(
                (long)scrollInfo.nPos - scrollInfo.nMin,
                0,
                maximumPosition);
            int thumbTop = trackTop + (int)(
                (long)(trackHeight - thumbHeight) * currentPosition / maximumPosition);
            int inset = Math.Max(2, scrollBarWidth / 5);
            var thumbBounds = new Rectangle(
                scrollBarLeft + inset,
                thumbTop + 1,
                Math.Max(1, scrollBarWidth - (inset * 2)),
                Math.Max(1, thumbHeight - 2));
            graphics.FillRectangle(thumbBrush, thumbBounds);
        }
        finally
        {
            NativeMethods.ReleaseDC(Handle, deviceContext);
        }
    }

    private static void DrawScrollArrow(Graphics graphics, Pen pen,
        Rectangle bounds, bool pointsUp)
    {
        int halfWidth = Math.Max(2, bounds.Width / 5);
        int halfHeight = Math.Max(1, bounds.Height / 8);
        int centerX = bounds.Left + (bounds.Width / 2);
        int centerY = bounds.Top + (bounds.Height / 2);
        Point[] points = pointsUp
            ? [
                new(centerX - halfWidth, centerY + halfHeight),
                new(centerX, centerY - halfHeight),
                new(centerX + halfWidth, centerY + halfHeight),
            ]
            : [
                new(centerX - halfWidth, centerY - halfHeight),
                new(centerX, centerY + halfHeight),
                new(centerX + halfWidth, centerY - halfHeight),
            ];
        graphics.DrawLines(pen, points);
    }

    /// <summary>Updates the owner-drawn selection palette for the focused pane.</summary>
    internal void SetInputFocus(bool hasInputFocus)
    {
        if (_hasInputFocus == hasInputFocus) return;

        _hasInputFocus = hasInputFocus;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyResponsiveColumnWidths();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_ownedResourcesDisposed)
        {
            _ownedResourcesDisposed = true;
            ClipboardCutState.Changed -= OnClipboardCutStateChanged;
            CancellationTokenSource? loadCancellation = Interlocked.Exchange(
                ref _loadCancellation, null);
            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            _fileSystemChangeDebounce.Dispose();
            _folderWatchSubscription?.Dispose();
            _dragFeedbackWindow.Dispose();
            _rowHeightImageList.Images.Clear();
            _rowHeightImage?.Dispose();
            foreach (Bitmap image in _rowIconImages) image.Dispose();
            _rowHeightImageList.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnClipboardCutStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (IsHandleCreated)
                BeginInvoke((MethodInvoker)Invalidate);
            return;
        }

        Invalidate();
    }

    private void UpdateRowHeight()
    {
        // Control's base constructor can raise FontChanged before this class has
        // created the backing image list.
        if (_rowHeightImageList is null) return;

        int rowHeight = Math.Max(
            LogicalToDeviceUnits(GetMinimumRowHeight(_compactMode)),
            Font.Height + LogicalToDeviceUnits(AdditionalRowPadding));
        int imageWidth = LogicalToDeviceUnits(20);
        if (_rowHeightImageList.ImageSize == new Size(imageWidth, rowHeight)) return;

        _rowHeightImageList.Images.Clear();
        _rowHeightImage?.Dispose();
        foreach (Bitmap image in _rowIconImages) image.Dispose();
        _rowIconImages.Clear();
        _rowIconIndexes.Clear();
        _rowHeightImageList.ImageSize = new Size(imageWidth, rowHeight);
        _rowHeightImage = new Bitmap(imageWidth, rowHeight);
        _rowHeightImageList.Images.Add(_rowHeightImage);

        if (_entries.Count > 0)
            PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private void StartLoad(string path, bool showHiddenItems)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _completedLoadPath = string.Empty;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        ColumnDefinition[] columns = _visibleColumns.ToArray();
        bool showFileExtensions = _showFileExtensions;
        _ = LoadDirectoryAsync(
            path,
            showHiddenItems,
            showFileExtensions,
            columns,
            cancellation.Token);
    }

    private void WatchFolder(string path)
    {
        _folderWatchSubscription?.Dispose();
        _folderWatchSubscription = null;

        // Network views are refreshed explicitly. Creating a watcher against a
        // disconnected share can block the UI thread before the async load starts.
        if (ExplorerHost.IsNetworkPath(path)) return;

        try
        {
            _folderWatchSubscription = DirectorySnapshotCache.Watch(
                path, OnFolderSnapshotChanged);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Debug(ex, nameof(ManagedDetailsListView),
                $"Could not watch \"{path}\" for file-system changes.");
        }
    }

    private void OnFolderSnapshotChanged()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((MethodInvoker)DebounceFileSystemRefresh); }
            catch (InvalidOperationException)
            {
                // The control closed while the shared watcher was notifying it.
            }
            return;
        }

        DebounceFileSystemRefresh();
    }

    private void DebounceFileSystemRefresh()
    {
        if (IsDisposed || Disposing) return;
        if (DateTime.UtcNow < _suppressFolderRefreshUntilUtc) return;
        _fileSystemChangeDebounce.Stop();
        _fileSystemChangeDebounce.Start();
    }

    private async Task LoadDirectoryAsync(
        string path,
        bool showHiddenItems,
        bool showFileExtensions,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken)
    {
        List<ManagedFileItem> entries;
        try
        {
            DirectorySnapshot snapshot = await DirectorySnapshotCache.GetAsync(
                path, cancellationToken);
            entries = await Task.Run(
                () => BuildEntries(
                    snapshot,
                    showHiddenItems,
                    showFileExtensions,
                    columns,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            AppLog.Debug(ex, nameof(ManagedDetailsListView),
                $"Could not enumerate \"{path}\" for the managed Details view.");
            CompleteLoad(path, cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ManagedDetailsListView),
                $"The managed Details view could not display \"{path}\".");
            CompleteLoad(path, cancellationToken);
            return;
        }

        if (cancellationToken.IsCancellationRequested
            || IsDisposed
            || !string.Equals(path, _folderPath, StringComparison.OrdinalIgnoreCase))
            return;

        _entries = entries;
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        ApplyResponsiveColumnWidths();
        CompleteLoad(path, cancellationToken);
    }

    private void CompleteLoad(string path, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || IsDisposed
            || !string.Equals(path, _folderPath,
                StringComparison.OrdinalIgnoreCase))
            return;

        _completedLoadPath = path;
        DirectoryLoadCompleted?.Invoke(this, path);
    }

    private void PopulateItems(IReadOnlySet<string> previousSelections)
    {
        ArgumentNullException.ThrowIfNull(previousSelections);

        _displayEntries = GetDisplayEntries();
        bool useVirtualMode = ShouldVirtualizeRows(
            _displayEntries.Count,
            grouping: _groupColumn is not null,
            checkBoxes: CheckBoxes,
            inlineRename: _materializeForInlineRename);

        BeginUpdate();
        try
        {
            _hoveredItemIndex = -1;
            _virtualItemCache.Clear();
            if (useVirtualMode)
            {
                if (VirtualMode)
                {
                    VirtualListSize = 0;
                }
                else
                {
                    Items.Clear();
                    Groups.Clear();
                    ShowGroups = false;
                    VirtualMode = true;
                }

                VirtualListSize = _displayEntries.Count;
                RestoreVirtualSelections(previousSelections);
            }
            else
            {
                DisableVirtualMode();
                Items.Clear();
                Groups.Clear();
                ShowGroups = _groupColumn is not null;
                var groups = new Dictionary<string, ListViewGroup>(
                    StringComparer.CurrentCultureIgnoreCase);
                foreach (ManagedFileItem entry in _displayEntries)
                {
                    ListViewItem item = CreateListViewItem(entry);
                    Items.Add(item);

                    if (previousSelections.Contains(entry.FullPath))
                    {
                        item.Selected = true;
                        if (CheckBoxes) item.Checked = true;
                    }

                    if (_groupColumn is { } groupColumn)
                    {
                        string groupName = GetGroupName(entry, groupColumn);
                        if (!groups.TryGetValue(groupName, out ListViewGroup? group))
                        {
                            group = new ListViewGroup(groupName, HorizontalAlignment.Left);
                            groups.Add(groupName, group);
                            Groups.Add(group);
                        }

                        item.Group = group;
                    }
                }
            }
        }
        finally
        {
            EndUpdate();
        }

        if (_pendingRenamePath is { } pendingPath)
            BeginInvoke((MethodInvoker)(() => BeginRenameIfAvailable(pendingPath)));
    }

    internal static bool ShouldVirtualizeRows(
        int itemCount,
        bool grouping,
        bool checkBoxes,
        bool inlineRename)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        return itemCount >= VirtualizationThreshold
            && !grouping
            && !checkBoxes
            && !inlineRename;
    }

    private ManagedFileItem[] GetDisplayEntries()
    {
        IEnumerable<ManagedFileItem> entries = GetSortedEntries();
        if (_groupColumn is { } selectedGroupColumn)
        {
            entries = _groupAscending
                ? entries.OrderBy(entry => GetGroupName(entry, selectedGroupColumn),
                    StringComparer.CurrentCultureIgnoreCase)
                : entries.OrderByDescending(entry => GetGroupName(entry, selectedGroupColumn),
                    StringComparer.CurrentCultureIgnoreCase);
        }

        return entries.ToArray();
    }

    private void RestoreVirtualSelections(IReadOnlySet<string> previousSelections)
    {
        for (int index = 0; index < _displayEntries.Count; index++)
        {
            if (previousSelections.Contains(_displayEntries[index].FullPath))
                Items[index].Selected = true;
        }
    }

    private void DisableVirtualMode()
    {
        if (!VirtualMode) return;

        VirtualListSize = 0;
        VirtualMode = false;
        _virtualItemCache.Clear();
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _displayEntries.Count)
        {
            e.Item = new ListViewItem();
            return;
        }

        e.Item = _virtualItemCache.GetOrCreate(
            e.ItemIndex,
            () => CreateListViewItem(_displayEntries[e.ItemIndex]));
    }

    private ListViewItem CreateListViewItem(ManagedFileItem entry)
    {
        var values = _visibleColumns.Select(entry.GetDisplayValue).ToArray();
        return new ListViewItem(values)
        {
            Tag = entry.FullPath,
            ImageIndex = GetRowIconIndex(entry),
        };
    }

    private string? GetPathAt(int index) => index >= 0 && index < _displayEntries.Count
        ? _displayEntries[index].FullPath
        : null;

    private void StartRenameRetry(string path)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            if (BeginRenameIfAvailable(path) || ++attempts >= 50)
            {
                timer.Stop();
                timer.Dispose();
                if (attempts >= 50 && string.Equals(_pendingRenamePath,
                        path, StringComparison.OrdinalIgnoreCase))
                    _pendingRenamePath = null;
                return;
            }

            // An application-owned ShellNew command can create its file after
            // the first folder load. Refresh once the file exists so the retry
            // can find and edit the new row.
            if (attempts % 10 == 0
                && (File.Exists(path) || Directory.Exists(path))
                && ExplorerHost.PathsReferToSameFolder(
                    _completedLoadPath, _folderPath))
            {
                DirectorySnapshotCache.Invalidate(_folderPath);
                Reload(_showHiddenItems);
            }
        };
        timer.Start();
    }

    private bool BeginRenameIfAvailable(string path)
    {
        if (IsDisposed || !Visible || !IsHandleCreated) return false;
        if (!string.Equals(_pendingRenamePath, path,
                StringComparison.OrdinalIgnoreCase))
        {
            // The reload callback and retry timer can both observe the new row.
            // Once either path claims it, later attempts must be harmless: a
            // second materialization would close the active label editor.
            return true;
        }

        int itemIndex = FindDisplayEntry(path);
        if (itemIndex < 0) return false;

        _pendingRenamePath = null;
        if (BeginInlineRename(path)) return true;

        _pendingRenamePath = path;
        return false;
    }

    private bool BeginInlineRename(string path)
    {
        HashSet<string> selections = SelectedPaths.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        selections.Add(path);
        _materializeForInlineRename = true;
        PopulateItems(selections);

        int itemIndex = FindDisplayEntry(path);
        if (itemIndex < 0 || itemIndex >= Items.Count)
        {
            _materializeForInlineRename = false;
            return false;
        }

        ListViewItem item = Items[itemIndex];
        SelectedItems.Clear();
        item.Selected = true;
        item.Focused = true;
        item.EnsureVisible();
        Focus();
        _editingItemIndex = itemIndex;
        item.BeginEdit();
        int selectionLength = GetInitialRenameSelectionLength(
            item.Text,
            Directory.Exists(path),
            _showFileExtensions);
        if (!TrySelectRenameText(selectionLength) && IsHandleCreated)
        {
            BeginInvoke((MethodInvoker)(() =>
                TrySelectRenameText(selectionLength)));
        }
        return true;
    }

    private bool TrySelectRenameText(int selectionLength)
    {
        IntPtr editor = NativeMethods.SendMessageI(
            Handle,
            NativeMethods.LVM_GETEDITCONTROL,
            IntPtr.Zero,
            IntPtr.Zero);
        if (editor == IntPtr.Zero) return false;

        NativeMethods.SendMessageI(
            editor,
            NativeMethods.EM_SETSEL,
            IntPtr.Zero,
            (IntPtr)selectionLength);
        return true;
    }

    internal static int GetInitialRenameSelectionLength(
        string displayName,
        bool isDirectory,
        bool fileExtensionsVisible)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        if (isDirectory || !fileExtensionsVisible)
            return displayName.Length;

        int extensionSeparator = displayName.LastIndexOf('.');
        return extensionSeparator > 0
            ? extensionSeparator
            : displayName.Length;
    }

    private int FindDisplayEntry(string path)
    {
        for (int index = 0; index < _displayEntries.Count; index++)
        {
            if (string.Equals(_displayEntries[index].FullPath, path,
                    StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private IEnumerable<ManagedFileItem> GetSortedEntries()
    {
        var previousOrder = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        if (_sortColumnId != "Name")
        {
            for (int index = 0; index < _displayEntries.Count; index++)
                previousOrder.TryAdd(_displayEntries[index].FullPath, index);
        }
        bool groupFoldersFirst = _sortColumnId != "Name"
            || !_sortFoldersWithFilesByName;
        return _entries
            .OrderBy(entry => groupFoldersFirst && !entry.IsDirectory ? 1 : 0)
            .ThenBy(static entry => entry, Comparer<ManagedFileItem>.Create(CompareEntries))
            .ThenBy(entry => previousOrder.GetValueOrDefault(entry.FullPath, int.MaxValue));
    }

    private int CompareEntries(ManagedFileItem left, ManagedFileItem right)
    {
        int comparison = _sortColumnId switch
        {
            "DateModified" => left.Modified.CompareTo(right.Modified),
            "DateCreated" => left.Created.CompareTo(right.Created),
            "Type" => string.Compare(left.Type, right.Type,
                StringComparison.CurrentCultureIgnoreCase),
            "Size" => left.SizeBytes.CompareTo(right.SizeBytes),
            "Attributes" => left.Attributes.CompareTo(right.Attributes),
            "Extension" => string.Compare(left.Extension, right.Extension,
                StringComparison.CurrentCultureIgnoreCase),
            "FullPath" => string.Compare(left.FullPath, right.FullPath,
                StringComparison.CurrentCultureIgnoreCase),
            "Name" => string.Compare(left.Name, right.Name,
                StringComparison.CurrentCultureIgnoreCase),
            _ => string.Compare(
                left.Properties.GetValueOrDefault(_sortColumnId),
                right.Properties.GetValueOrDefault(_sortColumnId),
                StringComparison.CurrentCultureIgnoreCase),
        };
        comparison = _sortAscending ? comparison : -comparison;
        return comparison;
    }

    private static string GetGroupName(ManagedFileItem entry, ManagedDetailsGroupColumn column) =>
        column switch
        {
            ManagedDetailsGroupColumn.DateModified => entry.Modified.Date == DateTime.Today
                ? "Today"
                : entry.Modified.Date == DateTime.Today.AddDays(-1)
                    ? "Yesterday"
                    : entry.Modified.ToString("MMMM yyyy"),
            ManagedDetailsGroupColumn.Type => entry.Type,
            ManagedDetailsGroupColumn.Size => entry.IsDirectory
                ? "Folders"
                : entry.SizeBytes == 0 ? "Empty"
                    : entry.SizeBytes < 1024 * 1024 ? "Small files"
                        : entry.SizeBytes < 1024L * 1024 * 1024 ? "Medium files" : "Large files",
            _ => string.IsNullOrWhiteSpace(entry.Name)
                ? "Other"
                : char.ToUpperInvariant(entry.Name[0]).ToString(),
        };

    private int GetRowIconIndex(ManagedFileItem entry)
    {
        string key = entry.IsDirectory
            ? "<folder>"
            : string.IsNullOrWhiteSpace(entry.Extension) ? "<file>" : entry.Extension;
        if (_rowIconIndexes.TryGetValue(key, out int existingIndex))
            return existingIndex;

        int imageIndex = TryAddRowIcon(entry.FullPath, entry.IsDirectory) ?? 0;
        _rowIconIndexes[key] = imageIndex;
        return imageIndex;
    }

    private int? TryAddRowIcon(string path, bool isDirectory)
    {
        var fileInfo = new NativeMethods.SHFILEINFOW();
        uint infoSize = (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>();
        if (NativeMethods.SHGetFileInfoW(path, 0, ref fileInfo, infoSize,
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON) == IntPtr.Zero
            || fileInfo.hIcon == IntPtr.Zero)
            return null;

        try
        {
            using Icon systemIcon = Icon.FromHandle(fileInfo.hIcon);
            using Bitmap source = systemIcon.ToBitmap();
            using Bitmap icon = isDirectory ? TintFolderIcon(source) : (Bitmap)source.Clone();
            Size imageSize = _rowHeightImageList.ImageSize;
            int iconSize = Math.Min(LogicalToDeviceUnits(16),
                Math.Min(imageSize.Width, imageSize.Height));
            var canvas = new Bitmap(imageSize.Width, imageSize.Height);
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(icon, new Rectangle(
                    (imageSize.Width - iconSize) / 2,
                    (imageSize.Height - iconSize) / 2,
                    iconSize,
                    iconSize));
            }

            _rowHeightImageList.Images.Add(canvas);
            _rowIconImages.Add(canvas);
            return _rowHeightImageList.Images.Count - 1;
        }
        catch (ArgumentException ex)
        {
            AppLog.Debug(ex, nameof(ManagedDetailsListView),
                $"Could not retrieve the icon for \"{path}\".");
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(fileInfo.hIcon);
        }
    }

    private static Bitmap TintFolderIcon(Bitmap source)
    {
        var tinted = new Bitmap(source.Width, source.Height);
        Color blue = ThemeManager.DetailsFolderIcon;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Color pixel = source.GetPixel(x, y);
                if (pixel.A == 0) continue;

                // Retain the folder artwork's light and dark detail while
                // replacing its yellow hue with a restrained blue tint.
                float sourceBrightness = (pixel.R * 0.299f + pixel.G * 0.587f + pixel.B * 0.114f) / 255f;
                float brightness = 0.65f + sourceBrightness * 0.35f;
                tinted.SetPixel(x, y, Color.FromArgb(pixel.A,
                    (int)(blue.R * brightness),
                    (int)(blue.G * brightness),
                    (int)(blue.B * brightness)));
            }
        }

        return tinted;
    }

    private static List<ManagedFileItem> BuildEntries(
        DirectorySnapshot snapshot,
        bool showHiddenItems,
        bool showFileExtensions,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool Include(DirectorySnapshotItem item) => showHiddenItems
            || (item.Attributes & FileAttributes.Hidden) == 0;

        var items = new List<ManagedFileItem>();
        foreach (DirectorySnapshotItem child in snapshot.Items
                     .Where(Include)
                     .OrderBy(static item => item.IsDirectory ? 0 : 1)
                     .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(CreateEntry(child, showFileExtensions, columns));
        }

        return items;
    }

    internal static string[] GetFilterDisplayValues(
        DirectorySnapshotItem item,
        IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ManagedFileItem entry = CreateEntry(item, showFileExtensions: true, columns);
        return columns.Select(entry.GetDisplayValue).ToArray();
    }

    private static ManagedFileItem CreateEntry(
        DirectorySnapshotItem child,
        bool showFileExtensions,
        IReadOnlyList<ColumnDefinition> columns)
    {
        string extension = child.Extension.TrimStart('.').ToUpperInvariant();
        return new ManagedFileItem(
            GetDisplayName(child.Name, child.IsDirectory, showFileExtensions),
            child.FullPath,
            child.IsDirectory
                ? "Folder"
                : extension.Length > 0 ? $"{extension} file" : "File",
            child.IsDirectory ? string.Empty : FormatFileSize(child.Length),
            child.LastWriteTime,
            child.CreationTime,
            child.Attributes,
            child.Extension,
            child.IsDirectory,
            child.Length,
            GetShellProperties(child.FullPath, columns));
    }

    private static IReadOnlyDictionary<string, string> GetShellProperties(
        string path,
        IReadOnlyList<ColumnDefinition> columns)
    {
        ColumnDefinition[] propertyColumns = columns
            .Where(static column => column.PropertyKey is not null)
            .ToArray();
        if (propertyColumns.Length == 0) return EmptyPropertyValues;

        IntPtr storePointer = IntPtr.Zero;
        try
        {
            Guid propertyStoreId = typeof(NativeMethods.IPropertyStore).GUID;
            if (NativeMethods.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0,
                    ref propertyStoreId, out storePointer) < 0
                || storePointer == IntPtr.Zero)
                return EmptyPropertyValues;

            var propertyStore = (NativeMethods.IPropertyStore)
                Marshal.GetObjectForIUnknown(storePointer);
            Marshal.Release(storePointer);
            storePointer = IntPtr.Zero;
            try
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (ColumnDefinition column in propertyColumns)
                {
                    NativeMethods.PROPERTYKEY key = column.PropertyKey!.Value;
                    if (propertyStore.GetValue(ref key, out NativeMethods.PROPVARIANT value) < 0)
                        continue;

                    try
                    {
                        if (value.vt == 0) continue;
                        if (NativeMethods.PSFormatForDisplayAlloc(ref key, ref value, 0,
                                out IntPtr displayPointer) < 0
                            || displayPointer == IntPtr.Zero)
                            continue;
                        try
                        {
                            string? displayValue = Marshal.PtrToStringUni(displayPointer);
                            if (!string.IsNullOrWhiteSpace(displayValue))
                                values[column.Id] = displayValue;
                        }
                        finally
                        {
                            NativeMethods.CoTaskMemFree(displayPointer);
                        }
                    }
                    finally
                    {
                        NativeMethods.PropVariantClear(ref value);
                    }
                }

                return values;
            }
            finally
            {
                Marshal.ReleaseComObject(propertyStore);
            }
        }
        catch (COMException)
        {
            return EmptyPropertyValues;
        }
        catch (UnauthorizedAccessException)
        {
            return EmptyPropertyValues;
        }
        catch (IOException)
        {
            return EmptyPropertyValues;
        }
        finally
        {
            if (storePointer != IntPtr.Zero) Marshal.Release(storePointer);
        }
    }

    private void ApplyResponsiveColumnWidths()
    {
        if (_applyingWidths || Columns.Count != _visibleColumns.Count || ClientSize.Width <= 0)
            return;

        _applyingWidths = true;
        try
        {
            int available = Math.Max(1, ClientSize.Width - LogicalToDeviceUnits(2));
            int nameIndex = _visibleColumns.FindIndex(
                static column => column.Id == "Name");
            if (nameIndex < 0) return;

            ColumnDefinition name = _visibleColumns[nameIndex];
            int nameMinimum = LogicalToDeviceUnits(name.MinimumWidth);
            int nameMaximum = LogicalToDeviceUnits(MaximumAutomaticNameWidth);
            List<int> metadata = Enumerable.Range(0, _visibleColumns.Count)
                .Where(index => index != nameIndex)
                .ToList();
            int metadataMinimum = metadata.Sum(index =>
                LogicalToDeviceUnits(_visibleColumns[index].MinimumWidth));
            int metadataPreferred = metadata.Sum(index =>
                LogicalToDeviceUnits(_visibleColumns[index].PreferredWidth));

            if (available <= nameMinimum + metadataMinimum)
            {
                Columns[nameIndex].Width = nameMinimum;
                foreach (int index in metadata)
                    Columns[index].Width = LogicalToDeviceUnits(_visibleColumns[index].MinimumWidth);
                return;
            }

            int metadataBudget = Math.Min(metadataPreferred, available - nameMinimum);
            int metadataRange = Math.Max(0, metadataPreferred - metadataMinimum);
            int expansion = metadataBudget - metadataMinimum;
            int allocated = 0;
            foreach (int index in metadata)
            {
                ColumnDefinition column = _visibleColumns[index];
                int minimum = LogicalToDeviceUnits(column.MinimumWidth);
                int range = LogicalToDeviceUnits(column.PreferredWidth) - minimum;
                int width = minimum + (metadataRange == 0
                    ? 0
                    : (int)((long)expansion * range / metadataRange));
                Columns[index].Width = width;
                allocated += width;
            }

            for (int index = 0; allocated < metadataBudget && index < metadata.Count; index++)
            {
                int columnIndex = metadata[index];
                int preferred = LogicalToDeviceUnits(_visibleColumns[columnIndex].PreferredWidth);
                if (Columns[columnIndex].Width >= preferred) continue;
                Columns[columnIndex].Width++;
                allocated++;
                if (index == metadata.Count - 1 && allocated < metadataBudget)
                    index = -1;
            }

            // Once the other columns have their useful width, retain empty space
            // instead of turning the Name column into an impractically wide field.
            Columns[nameIndex].Width = Math.Clamp(available - allocated, nameMinimum, nameMaximum);
        }
        finally
        {
            _applyingWidths = false;
        }
    }

    private void RebuildColumns()
    {
        Columns.Clear();
        foreach (ColumnDefinition column in _visibleColumns)
        {
            Columns.Add(column.Text,
                LogicalToDeviceUnits(column.PreferredWidth), column.Alignment);
        }
    }

    private void OnColumnWidthChanging(object? sender, ColumnWidthChangingEventArgs e)
    {
        if (_applyingWidths || e.ColumnIndex < 0 || e.ColumnIndex >= _visibleColumns.Count)
            return;

        int minimum = LogicalToDeviceUnits(_visibleColumns[e.ColumnIndex].MinimumWidth);
        e.NewWidth = Math.Max(e.NewWidth, minimum);
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column < 0 || e.Column >= _visibleColumns.Count) return;

        string selectedColumnId = _visibleColumns[e.Column].Id;
        (_sortColumnId, _sortAscending) = GetNextSortState(
            _sortColumnId, _sortAscending, selectedColumnId);
        PopulateItems(SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Invalidate(true);
    }

    internal static (string ColumnId, bool Ascending) GetNextSortState(
        string currentColumnId, bool currentAscending, string selectedColumnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentColumnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedColumnId);
        return string.Equals(currentColumnId, selectedColumnId, StringComparison.Ordinal)
            ? (selectedColumnId, !currentAscending)
            : (selectedColumnId, true);
    }

    private void InstallHeaderWindowHook()
    {
        IntPtr headerHandle = NativeMethods.SendMessageI(
            Handle, NativeMethods.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (headerHandle != IntPtr.Zero)
            _headerWindowHook.Attach(headerHandle);
    }

    private void RequestColumnChooser()
    {
        if (_columnChooserPending || IsDisposed || !IsHandleCreated) return;

        _columnChooserPending = true;
        BeginInvoke((MethodInvoker)(() =>
        {
            _columnChooserPending = false;
            if (!IsDisposed)
                ShowColumnChooser();
        }));
    }

    private void ShowColumnChooser()
    {
        IReadOnlyList<ColumnDefinition> availableColumns = GetAvailableColumns();
        _columnChooserPopup?.Close();
        _columnChooserPopup = new ColumnChooserPopup(this, availableColumns, _visibleColumns);
        _columnChooserPopup.Closed += (_, _) => _columnChooserPopup = null;
        _columnChooserPopup.Show(Cursor.Position);
    }

    private void ApplyColumnSelection(IReadOnlySet<string> selectedColumnIds)
    {
        ArgumentNullException.ThrowIfNull(selectedColumnIds);
        var selectedIds = selectedColumnIds.ToHashSet(StringComparer.Ordinal);
        selectedIds.Add("Name");

        var selected = _visibleColumns
            .Where(column => selectedIds.Contains(column.Id))
            .ToList();
        selected.AddRange(GetAvailableColumns().Where(column =>
            selectedIds.Contains(column.Id)
            && selected.All(existing => existing.Id != column.Id)));

        _visibleColumns.Clear();
        _visibleColumns.AddRange(selected);
        if (_visibleColumns.All(column => column.Id != _sortColumnId))
        {
            _sortColumnId = "Name";
            _sortAscending = true;
        }
        RebuildColumns();
        StartLoad(_folderPath, _showHiddenItems);
        VisibleColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<ColumnDefinition> GetAvailableColumns()
    {
        if (_availableColumns is not null) return _availableColumns;

        var columns = new List<ColumnDefinition>(BaseColumns);
        IntPtr listPointer = IntPtr.Zero;
        try
        {
            Guid listId = typeof(NativeMethods.IPropertyDescriptionList).GUID;
            if (NativeMethods.PSEnumeratePropertyDescriptions(6 /* PDEF_COLUMN */,
                    ref listId, out listPointer) < 0
                || listPointer == IntPtr.Zero)
                return _availableColumns = columns;

            var descriptions = (NativeMethods.IPropertyDescriptionList)
                Marshal.GetObjectForIUnknown(listPointer);
            Marshal.Release(listPointer);
            listPointer = IntPtr.Zero;
            try
            {
                if (descriptions.GetCount(out uint count) < 0)
                    return _availableColumns = columns;

                Guid descriptionId = typeof(NativeMethods.IPropertyDescription).GUID;
                for (uint index = 0; index < count; index++)
                {
                    if (descriptions.GetAt(index, ref descriptionId, out IntPtr descriptionPointer) < 0
                        || descriptionPointer == IntPtr.Zero)
                        continue;

                    var description = (NativeMethods.IPropertyDescription)
                        Marshal.GetObjectForIUnknown(descriptionPointer);
                    Marshal.Release(descriptionPointer);
                    try
                    {
                        if (description.GetPropertyKey(out NativeMethods.PROPERTYKEY key) < 0
                            || !TryGetDescriptionText(description, out string canonicalName,
                                out string displayName)
                            || columns.Any(column => string.Equals(column.Text, displayName,
                                StringComparison.CurrentCultureIgnoreCase)))
                            continue;

                        columns.Add(new ColumnDefinition(
                            canonicalName, displayName, 150, 100, PropertyKey: key));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(description);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(descriptions);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ManagedDetailsListView),
                "Could not enumerate the Windows Shell column catalogue.");
        }
        finally
        {
            if (listPointer != IntPtr.Zero) Marshal.Release(listPointer);
        }

        return _availableColumns = columns
            .Take(BaseColumns.Length)
            .Concat(columns.Skip(BaseColumns.Length)
                .OrderBy(static column => column.Text, StringComparer.CurrentCultureIgnoreCase))
            .ToArray();
    }

    private static bool TryGetDescriptionText(
        NativeMethods.IPropertyDescription description,
        out string canonicalName,
        out string displayName)
    {
        canonicalName = string.Empty;
        displayName = string.Empty;
        IntPtr canonicalPointer = IntPtr.Zero;
        IntPtr displayPointer = IntPtr.Zero;
        try
        {
            if (description.GetCanonicalName(out canonicalPointer) < 0
                || canonicalPointer == IntPtr.Zero
                || description.GetDisplayName(out displayPointer) < 0
                || displayPointer == IntPtr.Zero)
                return false;

            canonicalName = Marshal.PtrToStringUni(canonicalPointer) ?? string.Empty;
            displayName = Marshal.PtrToStringUni(displayPointer) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(canonicalName)
                && !string.IsNullOrWhiteSpace(displayName);
        }
        finally
        {
            if (canonicalPointer != IntPtr.Zero) NativeMethods.CoTaskMemFree(canonicalPointer);
            if (displayPointer != IntPtr.Zero) NativeMethods.CoTaskMemFree(displayPointer);
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e) =>
        SetHoveredItem(HitTest(e.Location).Item?.Index ?? -1);

    private void OnMouseLeave(object? sender, EventArgs e) => SetHoveredItem(-1);

    private void SetHoveredItem(int itemIndex)
    {
        if (_hoveredItemIndex == itemIndex) return;

        int previousIndex = _hoveredItemIndex;
        _hoveredItemIndex = itemIndex;
        RedrawItem(previousIndex);
        RedrawItem(itemIndex);
    }

    private void RedrawItem(int itemIndex)
    {
        int itemCount = VirtualMode ? VirtualListSize : Items.Count;
        if (!IsHandleCreated || itemIndex < 0 || itemIndex >= itemCount) return;
        RedrawItems(itemIndex, itemIndex, invalidateOnly: true);
    }

    private Color GetItemBackColor(ListViewItem item) => item.Selected
        ? _hasInputFocus ? ThemeManager.ActiveSelection : ThemeManager.InactiveSelection
        : item.Index == _hoveredItemIndex
            ? ThemeManager.AccentHover
            : ThemeManager.Window;

    private Color GetItemTextColor(ListViewItem item) => !item.Selected
        ? ThemeManager.Text
        : _hasInputFocus ? Color.White : ThemeManager.InactiveSelectionText;

    private void OnItemActivate(object? sender, EventArgs e)
    {
        if (SelectedPath is { } path)
            ItemActivatedPath?.Invoke(this, path);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        ListViewHitTestInfo hit = HitTest(e.Location);
        if (hit.Item?.Tag is not string path)
        {
            if (e.Location.Y < Font.Height + LogicalToDeviceUnits(12))
                RequestColumnChooser();
            else
                BackgroundContextMenuRequested?.Invoke(this,
                    new ManagedBackgroundContextMenuEventArgs(e.Location));
            return;
        }

        if (!hit.Item.Selected)
        {
            SelectedIndices.Clear();
            hit.Item.Selected = true;
        }
        hit.Item.Focused = true;
        ContextMenuRequested?.Invoke(this, new ManagedContextMenuEventArgs(path, e.Location));
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left
            || e.Location.Y < Font.Height + LogicalToDeviceUnits(12)
            || HitTest(e.Location).Item is not null)
        {
            _lastBlankAreaClickTick = 0;
            return;
        }

        long now = Environment.TickCount64;
        bool isDoubleClick = _lastBlankAreaClickTick > 0
            && (now - _lastBlankAreaClickTick) <= SystemInformation.DoubleClickTime
            && Math.Abs(e.Location.X - _lastBlankAreaClickPoint.X)
                <= SystemInformation.DoubleClickSize.Width
            && Math.Abs(e.Location.Y - _lastBlankAreaClickPoint.Y)
                <= SystemInformation.DoubleClickSize.Height;

        _lastBlankAreaClickTick = isDoubleClick ? 0 : now;
        _lastBlankAreaClickPoint = e.Location;
        if (!isDoubleClick) return;

        // Preserve the ExplorerBrowser gesture that was present before the
        // managed Details view replaced its native file-list surface.
        CommandRequested?.Invoke(this, CommandBar.Cmd.GoToParent);
    }

    /// <summary>
    /// The native Details header is its own SysHeader32 child window, so mouse
    /// messages sent there do not reach the ListView's MouseUp event.  Subclass
    /// it only to expose the same column picker as Windows File Explorer.
    /// </summary>
    private sealed class HeaderWindowHook(ManagedDetailsListView owner) : NativeWindow
    {
        private readonly ManagedDetailsListView _owner = owner;

        internal void Attach(IntPtr handle)
        {
            if (Handle == handle) return;
            Detach();
            AssignHandle(handle);
        }

        internal void Detach()
        {
            if (Handle != IntPtr.Zero)
                ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_CONTEXTMENU)
            {
                _owner.RequestColumnChooser();
                return;
            }

            base.WndProc(ref m);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        CommandBar.Cmd? command = e.KeyCode switch
        {
            Keys.C when e.Modifiers == (Keys.Control | Keys.Shift) =>
                CommandBar.Cmd.CopyPaths,
            Keys.Delete => CommandBar.Cmd.Delete,
            Keys.F2 => CommandBar.Cmd.Rename,
            Keys.A when e.Control => CommandBar.Cmd.SelectAll,
            Keys.C when e.Control => CommandBar.Cmd.Copy,
            Keys.X when e.Control => CommandBar.Cmd.Cut,
            Keys.V when e.Control => CommandBar.Cmd.Paste,
            _ => null,
        };

        if (e.KeyCode == Keys.Space && !e.Control && !e.Alt && !e.Shift)
        {
            if (SelectedPath is { } path)
                QuickLookRequested?.Invoke(this, path);
            e.Handled = true;
            return;
        }

        if (command is not { } value) return;
        CommandRequested?.Invoke(this, value);
        e.Handled = true;
    }

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_synchronizingItemChecks
            || !CheckBoxes
            || e.Index < 0
            || e.Index >= _displayEntries.Count)
            return;

        ListViewItem item = Items[e.Index];
        bool shouldSelect = e.NewValue == CheckState.Checked;
        BeginInvoke((MethodInvoker)(() =>
        {
            if (IsDisposed || item.ListView != this) return;
            _synchronizingItemChecks = true;
            try
            {
                item.Selected = shouldSelect;
                item.Focused = shouldSelect;
            }
            finally
            {
                _synchronizingItemChecks = false;
            }
            RedrawItem(item.Index);
        }));
    }

    private void OnItemSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        if (e.Item is not { } item) return;

        if (CheckBoxes && !_synchronizingItemChecks)
        {
            _synchronizingItemChecks = true;
            try
            {
                item.Checked = e.IsSelected;
            }
            finally
            {
                _synchronizingItemChecks = false;
            }
        }

        RedrawItem(item.Index);
    }

    private void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        e.CancelEdit = true;
        _editingItemIndex = -1;
        HashSet<string> selections = SelectedPaths.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        _materializeForInlineRename = false;
        if (e.Label is null || GetPathAt(e.Item) is not { } path)
        {
            BeginInvoke((MethodInvoker)(() => PopulateItems(selections)));
            return;
        }

        string newName = BuildFileSystemName(
            Path.GetFileName(path),
            e.Label,
            Directory.Exists(path),
            _showFileExtensions);
        RenameRequested?.Invoke(this, new ManagedRenameEventArgs(path, newName));
        BeginInvoke((MethodInvoker)(() => PopulateItems(selections)));
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragFeedback(e);
        RepaintDragRows(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragFeedback(e);
        RepaintDragRows(e);
    }

    private void OnDragLeave(object? sender, EventArgs e)
    {
        EndDragFeedback();
    }

    private void RepaintDragRows(DragEventArgs e)
    {
        if (!IsHandleCreated) return;

        // The OLE drag image can erase owner-drawn subitem text as it moves.
        // Redraw both the row it left and the row under the cursor, including
        // horizontal moves within the same row.
        int itemIndex = HitTest(PointToClient(new Point(e.X, e.Y))).Item?.Index ?? -1;
        if (_dragPaintItemIndex != itemIndex)
            RedrawItem(_dragPaintItemIndex);
        _dragPaintItemIndex = itemIndex;
        RedrawItem(itemIndex);
        Update();
    }

    private void EndDragFeedback()
    {
        _dragFeedbackWindow.HideFeedback();
        _dragPaintItemIndex = -1;
        if (!IsHandleCreated) return;

        // Restore any adjacent cells that were covered by the drag image or
        // feedback window when the drag was cancelled or completed.
        Invalidate();
        Update();
    }

    private void UpdateDragFeedback(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] sourcePaths
            || sourcePaths.Length == 0
            || string.IsNullOrWhiteSpace(_folderPath))
        {
            e.Effect = DragDropEffects.None;
            _dragFeedbackWindow.HideFeedback();
            return;
        }

        string destinationPath = GetDropDestination(
            PointToClient(new Point(e.X, e.Y)));
        e.Effect = ChooseDropEffect(
            e.AllowedEffect,
            e.KeyState,
            sourcePaths,
            destinationPath);
        if (e.Effect == DragDropEffects.None)
        {
            _dragFeedbackWindow.HideFeedback();
            return;
        }

        if (e.Effect == DragDropEffects.Move
            && IsRedundantMove(sourcePaths, destinationPath))
        {
            e.Effect = DragDropEffects.None;
            _dragFeedbackWindow.HideFeedback();
            return;
        }

        string destinationName = GetFolderDisplayName(destinationPath);
        SetDropDescription(e.Data, e.Effect, destinationName);
        _dragFeedbackWindow.ShowFeedback(new Point(e.X, e.Y), e.Effect, destinationName);
    }

    internal static DragDropEffects ChooseDropEffect(
        DragDropEffects allowedEffect,
        int keyState,
        IEnumerable<string> sourcePaths,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string[] paths = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (paths.Length == 0) return DragDropEffects.None;

        DragDropEffects requestedEffect;
        if ((keyState & MkControl) != 0)
        {
            requestedEffect = DragDropEffects.Copy;
        }
        else if ((keyState & MkShift) != 0)
        {
            requestedEffect = DragDropEffects.Move;
        }
        else
        {
            requestedEffect = FileDragDropPolicy.ShouldMoveByDefault(
                paths, destinationPath)
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
        }

        if ((allowedEffect & requestedEffect) != 0) return requestedEffect;
        if ((allowedEffect & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
        if ((allowedEffect & DragDropEffects.Move) != 0) return DragDropEffects.Move;
        return DragDropEffects.None;
    }

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        string[] paths = SelectedPaths.ToArray();
        if (paths.Length == 0) return;

        var fileDropList = new StringCollection();
        fileDropList.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(fileDropList);
        try
        {
            DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            EndDragFeedback();
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        EndDragFeedback();
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths
            || paths.Length == 0
            || string.IsNullOrWhiteSpace(_folderPath))
            return;

        string destinationPath = GetDropDestination(PointToClient(new Point(e.X, e.Y)));

        e.Effect = ChooseDropEffect(
            e.AllowedEffect,
            e.KeyState,
            paths,
            destinationPath);
        if (e.Effect == DragDropEffects.None)
            return;

        FileOperationKind operation = e.Effect == DragDropEffects.Move
            ? FileOperationKind.Move
            : FileOperationKind.Copy;
        if (operation == FileOperationKind.Move && IsRedundantMove(paths, destinationPath))
            return;

        OperationManager.Current?.Start(operation, paths, destinationPath);
    }

    private string GetDropDestination(Point clientPoint) =>
        HitTest(clientPoint).Item?.Tag is string targetPath && Directory.Exists(targetPath)
            ? targetPath
            : _folderPath;

    internal static bool IsRedundantMove(IEnumerable<string> sourcePaths, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string[] paths = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (paths.Length == 0) return false;

        // A folder cannot be moved onto itself; moving all items that already
        // belong to the destination is a no-op and must not open a conflict UI.
        return paths.Any(path => ExplorerHost.PathsReferToSameFolder(path, destinationPath))
            || paths.All(path => ExplorerHost.PathsReferToSameFolder(
                Path.GetDirectoryName(path), destinationPath));
    }

    internal static NativeMethods.DROPDESCRIPTION CreateDropDescription(
        DragDropEffects effect, string destinationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);

        bool isCopy = effect == DragDropEffects.Copy;
        return new NativeMethods.DROPDESCRIPTION
        {
            type = isCopy
                ? NativeMethods.DROPIMAGETYPE.Copy
                : NativeMethods.DROPIMAGETYPE.Move,
            szMessage = isCopy ? "Copy to %1" : "Move to %1",
            szInsert = destinationName,
        };
    }

    private static void SetDropDescription(
        IDataObject dataObject, DragDropEffects effect, string destinationName)
    {
        // Only our DataObject can be changed.  Windows Explorer's IDataObject
        // is intentionally treated as read-only when it is the drag source.
        if (dataObject is not DataObject) return;

        NativeMethods.DROPDESCRIPTION description = CreateDropDescription(effect, destinationName);
        int descriptionSize = Marshal.SizeOf<NativeMethods.DROPDESCRIPTION>();
        byte[] buffer = new byte[descriptionSize];
        IntPtr nativeDescription = Marshal.AllocHGlobal(descriptionSize);
        try
        {
            Marshal.StructureToPtr(description, nativeDescription, false);
            Marshal.Copy(nativeDescription, buffer, 0, buffer.Length);
        }
        finally
        {
            Marshal.DestroyStructure<NativeMethods.DROPDESCRIPTION>(nativeDescription);
            Marshal.FreeHGlobal(nativeDescription);
        }

        dataObject.SetData(
            DropDescriptionFormat,
            autoConvert: false,
            new MemoryStream(buffer, writable: false));
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        string trimmedPath = folderPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmedPath);
        return string.IsNullOrEmpty(name) ? folderPath : name;
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var brush = new SolidBrush(ThemeManager.Surface);
        using var pen = new Pen(ThemeManager.Border);
        e.Graphics.FillRectangle(brush, e.Bounds);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        if (e.Bounds.Right < ClientSize.Width)
            e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top,
                e.Bounds.Right - 1, e.Bounds.Bottom - 1);

        var textBounds = Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(6), 0);
        bool isSortColumn = e.ColumnIndex >= 0
            && e.ColumnIndex < _visibleColumns.Count
            && _visibleColumns[e.ColumnIndex].Id == _sortColumnId;
        if (isSortColumn)
        {
            int glyphWidth = LogicalToDeviceUnits(8);
            int glyphHeight = LogicalToDeviceUnits(5);
            int glyphRight = e.Bounds.Right - LogicalToDeviceUnits(8);
            int centerX = glyphRight - (glyphWidth / 2);
            int centerY = e.Bounds.Top + (e.Bounds.Height / 2);
            Point[] points = _sortAscending
                ?
                [
                    new(centerX, centerY - (glyphHeight / 2)),
                    new(centerX - (glyphWidth / 2), centerY + (glyphHeight / 2)),
                    new(centerX + (glyphWidth / 2), centerY + (glyphHeight / 2)),
                ]
                :
                [
                    new(centerX - (glyphWidth / 2), centerY - (glyphHeight / 2)),
                    new(centerX + (glyphWidth / 2), centerY - (glyphHeight / 2)),
                    new(centerX, centerY + (glyphHeight / 2)),
                ];
            using var glyphBrush = new SolidBrush(ThemeManager.MutedText);
            e.Graphics.FillPolygon(glyphBrush, points);
            textBounds.Width = Math.Max(1,
                textBounds.Width - glyphWidth - LogicalToDeviceUnits(6));
        }

        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Font,
            textBounds, ThemeManager.Text,
            GetTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left));
    }

    private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.Item is not { } item) return;
        Color background = GetItemBackColor(item);
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, new Rectangle(0, e.Bounds.Top, ClientSize.Width, e.Bounds.Height));
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is not { } item || e.SubItem is not { } subItem) return;
        Color background = GetItemBackColor(item);
        Color foreground = GetItemTextColor(item);
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        int textInset = LogicalToDeviceUnits(6);
        Rectangle textBounds = Rectangle.Inflate(e.Bounds, -textInset, 0);
        if (e.ColumnIndex == 0)
        {
            int contentLeft = e.Bounds.Left;
            if (CheckBoxes)
            {
                CheckBoxState state = item.Selected
                    ? CheckBoxState.CheckedNormal
                    : CheckBoxState.UncheckedNormal;
                Size checkBoxSize = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
                var checkBoxLocation = new Point(
                    e.Bounds.Left + LogicalToDeviceUnits(4),
                    e.Bounds.Top + Math.Max(0, (e.Bounds.Height - checkBoxSize.Height) / 2));
                CheckBoxRenderer.DrawCheckBox(e.Graphics, checkBoxLocation, state);
                contentLeft = checkBoxLocation.X + checkBoxSize.Width
                    + LogicalToDeviceUnits(4);
            }

            if (item.ImageIndex >= 0 && item.ImageIndex < _rowHeightImageList.Images.Count)
            {
                if (item.Tag is string path && ClipboardCutState.Contains(path))
                    DrawMutedRowIcon(e.Graphics, item.ImageIndex,
                        new Point(contentLeft, e.Bounds.Top));
                else
                    _rowHeightImageList.Draw(e.Graphics,
                        new Point(contentLeft, e.Bounds.Top), item.ImageIndex);
            }

            // Match the compact icon-to-label gap used by the native navigation
            // tree. The image-list canvas already reserves the icon's left edge;
            // do not add the general column inset a second time.
            textBounds.X = contentLeft + _rowHeightImageList.ImageSize.Width
                + LogicalToDeviceUnits(IconPadding);
            textBounds.Width = Math.Max(1,
                e.Bounds.Right - textInset - textBounds.X);
        }
        // Owner drawing normally repaints the label even while ListView's edit
        // control is open. That leaves pieces of "New folder" behind the text
        // the user types. Keep the row background and icon, but let the editor
        // be the only painter for the Name cell during inline rename.
        if (e.ColumnIndex != 0 || item.Index != _editingItemIndex)
            TextRenderer.DrawText(e.Graphics, subItem.Text, Font,
                textBounds, foreground,
                GetTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left));

        if (e.ColumnIndex == Columns.Count - 1 && item.Selected && item.Focused)
        {
            var focusBounds = new Rectangle(1, e.Bounds.Top,
                Math.Max(1, ClientSize.Width - 2), e.Bounds.Height);
            ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, foreground, background);
        }
    }

    private void DrawMutedRowIcon(Graphics graphics, int imageIndex, Point location)
    {
        int ownedImageIndex = imageIndex - 1;
        if (ownedImageIndex < 0 || ownedImageIndex >= _rowIconImages.Count)
            return;

        Bitmap image = _rowIconImages[ownedImageIndex];
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix
        {
            Matrix00 = 1f,
            Matrix11 = 1f,
            Matrix22 = 1f,
            Matrix33 = 0.42f,
            Matrix44 = 1f,
        });
        graphics.DrawImage(image,
            new Rectangle(location, _rowHeightImageList.ImageSize),
            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    private static TextFormatFlags GetTextFlags(HorizontalAlignment alignment)
    {
        TextFormatFlags flags = TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis;
        return alignment switch
        {
            HorizontalAlignment.Center => flags | TextFormatFlags.HorizontalCenter,
            HorizontalAlignment.Right => flags | TextFormatFlags.Right,
            _ => flags | TextFormatFlags.Left,
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    internal static int GetMinimumRowHeight(bool compactMode) => compactMode
        ? CompactMinimumRowHeight
        : StandardMinimumRowHeight;

    internal static string GetDisplayName(
        string name,
        bool isDirectory,
        bool showFileExtensions)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (isDirectory || showFileExtensions) return name;

        ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());
        return extension.IsEmpty ? name : name[..^extension.Length];
    }

    internal static string BuildFileSystemName(
        string originalName,
        string editedName,
        bool isDirectory,
        bool showFileExtensions)
    {
        ArgumentNullException.ThrowIfNull(originalName);
        ArgumentNullException.ThrowIfNull(editedName);
        if (isDirectory || showFileExtensions) return editedName;

        string extension = Path.GetExtension(originalName);
        return string.Concat(editedName, extension);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyPropertyValues =
        new Dictionary<string, string>();

    private static readonly ColumnDefinition[] BaseColumns =
    [
        new("Name", "Name", 180, 180),
        new("DateModified", "Date modified", 120, 120),
        new("Type", "Type", 100, 100),
        new("Size", "Size", 80, 80, HorizontalAlignment.Right),
        new("DateCreated", "Date created", 120, 120),
        new("Attributes", "Attributes", 110, 100),
        new("Extension", "Extension", 90, 80),
        new("FullPath", "Full path", 220, 160),
    ];

    internal readonly record struct ColumnDefinition(
        string Id,
        string Text,
        int PreferredWidth,
        int MinimumWidth,
        HorizontalAlignment Alignment = HorizontalAlignment.Left,
        NativeMethods.PROPERTYKEY? PropertyKey = null);

    private readonly record struct ManagedFileItem(
        string Name,
        string FullPath,
        string Type,
        string Size,
        DateTime Modified,
        DateTime Created,
        FileAttributes Attributes,
        string Extension,
        bool IsDirectory,
        long SizeBytes,
        IReadOnlyDictionary<string, string> Properties)
    {
        internal string GetDisplayValue(ColumnDefinition column) => column.Id switch
        {
            "Name" => Name,
            "DateModified" => Modified.ToString("g"),
            "Type" => Type,
            "Size" => Size,
            "DateCreated" => Created.ToString("g"),
            "Attributes" => Attributes.ToString(),
            "Extension" => Extension,
            "FullPath" => FullPath,
            _ when Properties.TryGetValue(column.Id, out string? value) => value,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// A compact, scrollable picker hosted in a ToolStripDropDown.  It has the
    /// interaction model of File Explorer's header popup, while still exposing
    /// the complete Windows Shell column catalogue through filtering.
    /// </summary>
    private sealed class ColumnChooserPopup : ToolStripDropDown
    {
        private readonly ManagedDetailsListView _owner;
        private readonly IReadOnlyList<ColumnDefinition> _availableColumns;
        private readonly HashSet<string> _selectedColumnIds;
        private readonly TextBox _searchBox;
        private readonly CheckedListBox _columnList;
        private bool _rebuildingList;

        internal ColumnChooserPopup(
            ManagedDetailsListView owner,
            IReadOnlyList<ColumnDefinition> availableColumns,
            IReadOnlyList<ColumnDefinition> existingColumns)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(availableColumns);
            ArgumentNullException.ThrowIfNull(existingColumns);
            _owner = owner;
            _availableColumns = availableColumns;
            _selectedColumnIds = existingColumns.Select(static column => column.Id)
                .ToHashSet(StringComparer.Ordinal);

            int width = Scale(owner, 420);
            int height = Scale(owner, 510);
            AutoSize = false;
            AutoClose = true;
            Padding = Padding.Empty;
            Margin = Padding.Empty;
            Size = new Size(width, height);
            BackColor = ThemeManager.Surface;

            _searchBox = new TextBox
            {
                Dock = DockStyle.Top,
                PlaceholderText = "Search columns",
                Margin = Padding.Empty,
                BackColor = ThemeManager.Window,
                ForeColor = ThemeManager.Text,
            };
            _searchBox.TextChanged += (_, _) => RebuildList();

            _columnList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                DisplayMember = nameof(ColumnDefinition.Text),
                BackColor = ThemeManager.Window,
                ForeColor = ThemeManager.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _columnList.ItemCheck += OnColumnItemCheck;

            var caption = new Label
            {
                Dock = DockStyle.Top,
                Height = Scale(owner, 28),
                Text = "Details columns",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Scale(owner, 8), 0, 0, 0),
                BackColor = ThemeManager.Surface,
                ForeColor = ThemeManager.Text,
            };
            var tip = new Label
            {
                Dock = DockStyle.Bottom,
                Height = Scale(owner, 28),
                Text = "Tick a column to add or remove it",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Scale(owner, 8), 0, 0, 0),
                BackColor = ThemeManager.Surface,
                ForeColor = ThemeManager.MutedText,
            };

            var searchContainer = new Panel
            {
                Dock = DockStyle.Top,
                Height = Scale(owner, 40),
                Padding = new Padding(Scale(owner, 8)),
                BackColor = ThemeManager.Surface,
            };
            searchContainer.Controls.Add(_searchBox);

            var content = new Panel
            {
                Size = new Size(width, height),
                BackColor = ThemeManager.Surface,
            };
            content.Controls.Add(_columnList);
            content.Controls.Add(searchContainer);
            content.Controls.Add(caption);
            content.Controls.Add(tip);
            Items.Add(new ToolStripControlHost(content)
            {
                AutoSize = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Size = content.Size,
            });
            Opened += (_, _) => _searchBox.Focus();
            RebuildList();
        }

        private void RebuildList()
        {
            string filter = _searchBox.Text.Trim();
            IEnumerable<ColumnDefinition> filtered = _availableColumns;
            if (!string.IsNullOrEmpty(filter))
            {
                filtered = filtered.Where(column =>
                    column.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
            }

            _columnList.BeginUpdate();
            try
            {
                _rebuildingList = true;
                _columnList.Items.Clear();
                foreach (ColumnDefinition column in filtered)
                {
                    int index = _columnList.Items.Add(column);
                    _columnList.SetItemChecked(index, _selectedColumnIds.Contains(column.Id));
                }
            }
            finally
            {
                _rebuildingList = false;
                _columnList.EndUpdate();
            }
        }

        private void OnColumnItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (_rebuildingList) return;
            if (_columnList.Items[e.Index] is not ColumnDefinition column) return;
            if (column.Id == "Name" && e.NewValue == CheckState.Unchecked)
            {
                e.NewValue = CheckState.Checked;
                return;
            }

            if (e.NewValue == CheckState.Checked)
                _selectedColumnIds.Add(column.Id);
            else
                _selectedColumnIds.Remove(column.Id);

            _owner.BeginInvoke((MethodInvoker)(() =>
            {
                if (!_owner.IsDisposed)
                    _owner.ApplyColumnSelection(_selectedColumnIds);
            }));
        }

        private static int Scale(Control control, int logicalPixels) =>
            (int)Math.Round(logicalPixels * control.DeviceDpi / 96f);

    }
}

internal sealed class ManagedContextMenuEventArgs(string path, Point location) : EventArgs
{
    internal string Path { get; } = path;
    internal Point Location { get; } = location;
}

internal sealed class ManagedBackgroundContextMenuEventArgs(Point location) : EventArgs
{
    internal Point Location { get; } = location;
}

internal sealed class ManagedRenameEventArgs(string path, string newName) : EventArgs
{
    internal string Path { get; } = path;
    internal string NewName { get; } = newName;
}

internal enum ManagedDetailsSortColumn
{
    Name,
    DateModified,
    Type,
    Size,
}

internal enum ManagedDetailsGroupColumn
{
    Name,
    DateModified,
    Type,
    Size,
}
