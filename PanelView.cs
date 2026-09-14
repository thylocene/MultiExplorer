using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// One panel of the dual-pane window.  Contains a tab bar, path bar, command bar,
/// and one ExplorerHost per open tab.  Only the active tab's host is visible.
/// Optional Details Pane (bottom) and Preview Pane (right) are toggled from the View menu.
/// </summary>
public sealed class PanelView : UserControl
{
    internal const string QuickLookProjectUrl = "https://github.com/ql-win/quicklook";
    internal const string QuickLookAcknowledgementText =
        "QuickLook is a separate third-party application that provides an " +
        "instant-preview experience to Windows. See " + QuickLookProjectUrl;

    private readonly TabBar     _tabBar;
    private readonly PathBar    _pathBar;
    private readonly CommandBar _commandBar;
    private readonly Panel      _content;

    // Content sub-layout: panes + host container (DockStyle.Fill fills remaining space)
    private readonly Panel        _hostContainer;
    private readonly DetailsPanel _detailsPanel;
    private readonly PreviewPane  _previewPanel;

    /// <summary>Raised when the user clicks the Mirror button; payload is the current folder path.</summary>
    public event EventHandler<string>? MirrorToOtherRequested;

    /// <summary>Raised when the user toggles the QuickLook integration on or off.</summary>
    public event EventHandler? QuickLookToggled;

    /// <summary>Raised when the user chooses Exit from the command bar (or Ctrl+Q).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user chooses "Set show-window hotkey…" from the command bar.</summary>
    public event EventHandler? SetHotkeyRequested;

    /// <summary>Raised when the user asks to toggle launch at Windows sign-in.</summary>
    public event EventHandler? StartWithWindowsToggled;

    /// <summary>Raised when either panel's Appearance menu selects a theme.</summary>
    public event EventHandler<ApplicationTheme>? ThemeSelected;

    /// <summary>Raised when the initially active tab has completed its first navigation.</summary>
    internal event EventHandler? InitialBrowserReady;

    // ── Filter bar ────────────────────────────────────────────────────────────
    private readonly Panel    _filterBar;
    private readonly Label    _filterPrefix;
    private readonly TextBox  _filterTextBox;
    private readonly RoundedButton _clearBtn;
    private readonly ListView _filterListView;
    private string _filterText = "";
    private readonly System.Windows.Forms.Timer _filterDebounce;
    private CancellationTokenSource? _filterCancellation;

    private readonly List<ExplorerHost> _hosts = new();
    private int    _active         = -1;
    private string _lastActivePath = "";

    public PanelView()
    {
        _tabBar     = new TabBar     { Dock = DockStyle.Top };
        _pathBar    = new PathBar    { Dock = DockStyle.Top };
        _commandBar = new CommandBar { Dock = DockStyle.Top };
        _content    = new Panel      { Dock = DockStyle.None, BackColor = ThemeManager.Window };

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
        // Shown on top of ExplorerHost when a filter is active. Populated from
        // Directory.GetFiles/GetDirectories — no shell navigation, no new windows.
        _filterListView = new ListView
        {
            View          = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            Sorting       = SortOrder.None,
            Visible       = false,
            BorderStyle   = BorderStyle.None,
        };
        _filterListView.Columns.Add("Name",         280);
        _filterListView.Columns.Add("Type",          70);
        _filterListView.Columns.Add("Size",           80);
        _filterListView.Columns.Add("Date modified", 130);
        _filterListView.DoubleClick += OnFilterListDoubleClick;
        _filterListView.KeyPress    += OnFilterListKeyPress;
        _filterListView.KeyDown     += OnFilterListKeyDown;
        _filterListView.MouseDown   += OnFilterListMouseDown;

        // Inner _content layout (WinForms dock order: last added = topmost in dock stack)
        // DetailsPanel (outermost bottom) → FilterBar (inner bottom, hidden by default) →
        // PreviewPane (right) → HostContainer (fill)
        // FilterBar lives in _content, not in _hostContainer, so ExplorerBrowser native HWNDs
        // inside _hostContainer can never paint over it regardless of z-order.
        _detailsPanel  = new DetailsPanel { Dock = DockStyle.Bottom, Visible = false };
        _previewPanel  = new PreviewPane  { Dock = DockStyle.Right,  Visible = false };
        _hostContainer = new Panel        { Dock = DockStyle.Fill, BackColor = ThemeManager.Window };
        _content.Controls.Add(_detailsPanel);
        _content.Controls.Add(_filterBar);      // DockStyle.Bottom, hidden; sits above _detailsPanel
        _content.Controls.Add(_previewPanel);
        _content.Controls.Add(_hostContainer);

        // FilterListView fills _hostContainer. BringToFront() makes it visible above ExplorerHosts.
        _filterListView.Dock = DockStyle.Fill;
        _hostContainer.Controls.Add(_filterListView);

        _hostContainer.Resize += (_, _) => ResizeAllHosts();

        _tabBar.TabSelected       += (_, i) => ActivateTab(i);
        _tabBar.TabCloseRequested += (_, i) => CloseTab(i);
        _tabBar.AddTabRequested   += (_, _) => AddTab(CurrentPath());
        _tabBar.TabMoveRequested  += OnTabMoveRequested;

        _pathBar.Navigate += (_, path) =>
        {
            ActiveHost?.NavigateTo(path);
            ActiveHost?.FocusShellView();
        };

        _commandBar.CommandIssued     += OnCommandIssued;
        _commandBar.ViewDropDownOpening += (_, _) => UpdateCommandBarToggles();
        _commandBar.ThemeSelected += (_, theme) => ThemeSelected?.Invoke(this, theme);
    }

    public void ApplyTheme()
    {
        BackColor = ThemeManager.Background;
        ForeColor = ThemeManager.Text;
        _tabBar.ApplyTheme();
        _pathBar.ApplyTheme();
        _content.BackColor = ThemeManager.Window;
        _hostContainer.BackColor = ThemeManager.Window;
        _detailsPanel.ApplyTheme();
        _previewPanel.ApplyTheme();
        _filterBar.BackColor = ThemeManager.Filter;
        _filterPrefix.BackColor = ThemeManager.Filter;
        _filterPrefix.ForeColor = ThemeManager.MutedText;
        _filterTextBox.BackColor = ThemeManager.Filter;
        _filterTextBox.ForeColor = ThemeManager.Text;
        _clearBtn.BackColor = ThemeManager.Filter;
        _clearBtn.ForeColor = ThemeManager.Text;
        _filterListView.BackColor = ThemeManager.Window;
        _filterListView.ForeColor = ThemeManager.Text;
        _commandBar.SetThemeSelection(ThemeManager.Current);
        _commandBar.ApplyTheme();
        Invalidate(true);
    }

    internal void SetStartWithWindowsChecked(bool enabled) =>
        _commandBar.SetStartWithWindowsChecked(enabled);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _filterCancellation?.Cancel();
            _filterCancellation?.Dispose();
            _filterDebounce.Dispose();
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
        _content.SetBounds(0, barsBottom,
            Math.Max(1, ClientSize.Width),
            Math.Max(1, ClientSize.Height - barsBottom));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public string CurrentPath()
        => ActiveHost?.GetCurrentPath() ?? @"C:\";

    /// <summary>Navigates the active tab to the given path (called by MainForm for Mirror).</summary>
    public void NavigateTo(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        ActiveHost?.NavigateTo(path);
        ActiveHost?.FocusShellView();
    }

    public List<string> GetAllPaths()
        => _hosts.Select(h => h.GetCurrentPath()).ToList();

    public void SetPathHistory(IEnumerable<string>? paths) => _pathBar.SetHistory(paths);

    public List<string> GetPathHistory() => _pathBar.GetHistory();

    internal void RefreshCurrentFolder() => ActiveHost?.RefreshShellView();

    /// <summary>Called from MainForm.OnShown after the panel has its final layout dimensions.</summary>
    public void Launch(List<string> paths)
    {
        if (paths.Count == 0) paths = new List<string> { @"C:\" };
        foreach (string p in paths) AddHostInternal(p);
        ActivateTab(0);
        UpdateCommandBarToggles();
    }

    /// <summary>Refresh the path bar, tab label, and pane content (called by the poll timer).</summary>
    public void PollPath()
    {
        if (_active < 0) return;
        string p = _hosts[_active].GetCurrentPathForPolling();
        _pathBar.SetPath(p);
        _tabBar.UpdateLabel(_active, FolderLabel(p), p);

        // Feed details/preview panes if visible
        if (_detailsPanel.Visible || _previewPanel.Visible)
        {
            // Selection is sampled asynchronously.  Shell drag/drop can occupy a
            // browser STA for the duration of a copy/move; a synchronous query here
            // would freeze the WinForms timer (and therefore the whole main window).
            string? sel = _hosts[_active].GetSelectedItemPathForPolling();
            if (_detailsPanel.Visible) _detailsPanel.ShowItem(sel);
            if (_previewPanel.Visible) _previewPanel.Preview(sel);
        }

        if (!string.IsNullOrEmpty(p) && p != _lastActivePath)
        {
            _lastActivePath = p;
            _hosts[_active].EnsureColumnHeaders();
            _hosts[_active].SyncNavigationPane(p);
        }
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
        host.QuickLookUnavailable += OnHostQuickLookUnavailable;
        host.InitialNavigationCompleted += OnHostInitialNavigationCompleted;
    }

    private void DetachHostEvents(ExplorerHost host)
    {
        host.FilterCharInput -= OnFilterCharInput;
        host.FilterBackspaceTyped -= OnFilterBackspaceTyped;
        host.FilterEscapePressed -= OnHostFilterEscapePressed;
        host.ApplicationShortcutRequested -= OnHostApplicationShortcutRequested;
        host.ShellMouseDown -= OnHostShellMouseDown;
        host.QuickLookUnavailable -= OnHostQuickLookUnavailable;
        host.InitialNavigationCompleted -= OnHostInitialNavigationCompleted;
    }

    private void OnHostFilterEscapePressed(object? sender, EventArgs e) => ClearFilter();

    private void OnHostApplicationShortcutRequested(object? sender, CommandBar.Cmd command) =>
        ExecuteCommand(command);

    private void OnHostShellMouseDown(object? sender, EventArgs e) =>
        _pathBar.DismissHistoryPopup();

    private void OnHostQuickLookUnavailable(object? sender, QuickLookFailure failure) =>
        ShowQuickLookUnavailable(failure);

    private void OnHostInitialNavigationCompleted(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActiveHost))
            InitialBrowserReady?.Invoke(this, EventArgs.Empty);
    }

    private void CloseTab(int index)
    {
        if (_hosts.Count <= 1) return;

        var host = _hosts[index];
        _hosts.RemoveAt(index);
        _hostContainer.Controls.Remove(host);
        host.Dispose();

        int newActive = _active;
        if (_active == index)
            newActive = Math.Min(index, _hosts.Count - 1);
        else if (_active > index)
            newActive = _active - 1;

        _active = -1;
        ActivateTab(newActive);
    }

    private void OnTabMoveRequested(object? sender, TabMoveRequestedEventArgs e)
    {
        if (e.Source.Parent is not PanelView sourcePanel) return;
        sourcePanel.MoveTabTo(this, e.SourceIndex, e.TargetIndex);
    }

    private void MoveTabTo(PanelView target, int sourceIndex, int targetInsertionIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= _hosts.Count
            || target.IsDisposed || target.Disposing)
            return;

        if (ReferenceEquals(this, target))
        {
            MoveTabWithinPanel(sourceIndex, targetInsertionIndex);
            return;
        }

        ClearFilter();
        ExplorerHost host = _hosts[sourceIndex];
        bool movedActiveTab = sourceIndex == _active;
        host.Visible = false;
        DetachHostEvents(host);
        _hostContainer.Controls.Remove(host);
        _hosts.RemoveAt(sourceIndex);

        if (_hosts.Count == 0)
        {
            _active = -1;
            AddHostInternal(@"C:\");
            ActivateTab(0);
        }
        else if (movedActiveTab)
        {
            int replacement = Math.Min(sourceIndex, _hosts.Count - 1);
            _active = -1;
            ActivateTab(replacement);
        }
        else
        {
            if (_active > sourceIndex) _active--;
            RefreshTabBar();
        }

        target.AcceptTransferredTab(host, targetInsertionIndex);
    }

    private void MoveTabWithinPanel(int sourceIndex, int insertionIndex)
    {
        int targetIndex = ReorderTargetIndex(_hosts.Count, sourceIndex, insertionIndex);
        if (targetIndex == sourceIndex) return;

        ExplorerHost activeHost = _hosts[_active];
        ExplorerHost movedHost = _hosts[sourceIndex];
        _hosts.RemoveAt(sourceIndex);
        _hosts.Insert(targetIndex, movedHost);
        _active = _hosts.IndexOf(activeHost);
        RefreshTabBar();
    }

    internal static int ReorderTargetIndex(int tabCount, int sourceIndex, int insertionIndex)
    {
        if (tabCount <= 0) return 0;
        int targetIndex = Math.Clamp(insertionIndex, 0, tabCount);
        if (targetIndex > sourceIndex) targetIndex--;
        return Math.Clamp(targetIndex, 0, tabCount - 1);
    }

    private void AcceptTransferredTab(ExplorerHost host, int insertionIndex)
    {
        ClearFilter();
        if (_active >= 0 && _active < _hosts.Count)
            _hosts[_active].Visible = false;

        int targetIndex = Math.Clamp(insertionIndex, 0, _hosts.Count);
        AttachHostEvents(host);
        host.SetBounds(0, 0,
            Math.Max(1, _hostContainer.ClientSize.Width),
            Math.Max(1, _hostContainer.ClientSize.Height));
        host.Visible = false;
        _hostContainer.Controls.Add(host);
        _hosts.Insert(targetIndex, host);
        host.ApplyTheme();

        _active = -1;
        ActivateTab(targetIndex);
    }

    private void ActivateTab(int index)
    {
        if (index < 0 || index >= _hosts.Count) return;

        ClearFilter(); // clear outgoing tab's filter before switching

        if (_active >= 0 && _active < _hosts.Count && _active != index)
            _hosts[_active].Visible = false;

        _active = index;
        _lastActivePath = "";
        var host = _hosts[index];

        host.SetBounds(0, 0,
            Math.Max(1, _hostContainer.ClientSize.Width),
            Math.Max(1, _hostContainer.ClientSize.Height));
        host.Visible = true;
        host.CreateControl();
        host.LaunchExplorer();

        _pathBar.SetPath(host.GetCurrentPath());
        RefreshTabBar();
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

        _filterListView.Columns[0].Width = LogicalToDeviceUnits(280);
        _filterListView.Columns[1].Width = LogicalToDeviceUnits(70);
        _filterListView.Columns[2].Width = LogicalToDeviceUnits(80);
        _filterListView.Columns[3].Width = LogicalToDeviceUnits(130);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ScaleFilterControls();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ScaleFilterControls();
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
        if (e.KeyCode == Keys.Escape) { ClearFilter(); e.Handled = true; }
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
        if (e.KeyCode == Keys.Escape)
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
        if (_filterListView.SelectedItems.Count == 0) return;
        string? path = _filterListView.SelectedItems[0].Tag as string;
        if (path == null) return;

        ClearFilter();

        if (Directory.Exists(path))
            ActiveHost?.NavigateTo(path);
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
        if (e.Button != MouseButtons.Right) return;
        var hit = _filterListView.HitTest(e.Location);
        if (hit.Item == null) return;
        hit.Item.Selected = true;
        hit.Item.Focused  = true;
        string? path = hit.Item.Tag as string;
        if (path == null) return;
        ShowShellContextMenu(path, _filterListView, e.Location);
    }

    private void ShowShellContextMenu(string path, Control control, Point clientPt)
    {
        int hr = NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return;

        IntPtr menu = IntPtr.Zero;
        NativeMethods.IShellFolder? folder  = null;
        NativeMethods.IContextMenu? ctxMenu = null;
        try
        {
            var iidFolder = typeof(NativeMethods.IShellFolder).GUID;
            hr = NativeMethods.SHBindToParent(pidl, ref iidFolder, out IntPtr pFolder, out IntPtr childPidl);
            if (hr != 0 || pFolder == IntPtr.Zero) return;
            folder = (NativeMethods.IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
            Marshal.Release(pFolder);

            var iidCtx = typeof(NativeMethods.IContextMenu).GUID;
            hr = folder.GetUIObjectOf(control.Handle, 1, new[] { childPidl },
                ref iidCtx, IntPtr.Zero, out IntPtr pCM);
            if (hr != 0 || pCM == IntPtr.Zero) return;
            ctxMenu = (NativeMethods.IContextMenu)Marshal.GetObjectForIUnknown(pCM);
            Marshal.Release(pCM);

            menu = NativeMethods.CreatePopupMenu();
            ctxMenu.QueryContextMenu(menu, 0, 1, 0x7FFF, NativeMethods.CMF_EXPLORE);

            Point screen = control.PointToScreen(clientPt);
            int selected = NativeMethods.TrackPopupMenuEx(menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                screen.X, screen.Y, control.Handle, IntPtr.Zero);

            if (selected > 0)
            {
                var ici = new NativeMethods.CMINVOKECOMMANDINFO
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.CMINVOKECOMMANDINFO>(),
                    hwnd   = control.Handle,
                    lpVerb = (IntPtr)(selected - 1),
                    nShow  = 1,
                };
                ctxMenu.InvokeCommand(ref ici);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ShowShellContextMenu),
                $"Could not complete the context-menu action for \"{Path.GetFileName(path)}\".");
        }
        finally
        {
            if (menu    != IntPtr.Zero)  NativeMethods.DestroyMenu(menu);
            if (ctxMenu != null)         Marshal.ReleaseComObject(ctxMenu);
            if (folder  != null)         Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(pidl);
        }
    }

    private async void OnFilterDebounce(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        await PopulateFilterListAsync(_filterText);
    }

    private void ClearFilter()
    {
        _filterDebounce.Stop();
        _filterCancellation?.Cancel();
        _filterText = "";

        _filterTextBox.TextChanged -= OnFilterTextBoxChanged;
        _filterTextBox.Text = "";
        _filterTextBox.TextChanged += OnFilterTextBoxChanged;

        _filterBar.Visible      = false;
        _filterListView.Visible = false;
        if (ActiveHost != null) ActiveHost.IsFiltering = false;
        ActiveHost?.FocusShellView();
    }

    private void ShowFilterOverlay()
    {
        if (!_filterBar.Visible)
            _filterBar.Visible = true;  // WinForms resizes _hostContainer; Resize fires ResizeAllHosts.
        _filterListView.BringToFront(); // above ExplorerHosts inside _hostContainer
        _filterListView.Visible = true;
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
            items = await Task.Run(
                () => BuildFilterItems(folder, filterText, cancellation.Token),
                cancellation.Token);
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

        _filterListView.BeginUpdate();
        try
        {
            _filterListView.Items.Clear();
            foreach (FilterItemData item in items)
            {
                var listItem = new ListViewItem(
                    new[] { item.Name, item.Type, item.Size, item.Modified })
                {
                    Tag = item.FullPath,
                };
                _filterListView.Items.Add(listItem);
            }
        }
        finally
        {
            _filterListView.EndUpdate();
        }
    }

    private static List<FilterItemData> BuildFilterItems(
        string folder, string filterText, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists) return new List<FilterItemData>();

        var items = new List<FilterItemData>();
        var comparison = StringComparison.OrdinalIgnoreCase;

        foreach (DirectoryInfo child in directory.EnumerateDirectories()
                     .Select(d =>
                     {
                         cancellationToken.ThrowIfCancellationRequested();
                         return d;
                     })
                     .Where(d => d.Name.Contains(filterText, comparison))
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(new FilterItemData(
                child.Name, "Folder", "", child.LastWriteTime.ToString("g"), child.FullName));
        }

        foreach (FileInfo child in directory.EnumerateFiles()
                     .Select(f =>
                     {
                         cancellationToken.ThrowIfCancellationRequested();
                         return f;
                     })
                     .Where(f => f.Name.Contains(filterText, comparison))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = child.Extension.TrimStart('.').ToUpperInvariant();
            items.Add(new FilterItemData(
                child.Name,
                extension.Length > 0 ? $"{extension} file" : "File",
                FormatFileSize(child.Length),
                child.LastWriteTime.ToString("g"),
                child.FullName));
        }

        return items;
    }

    private readonly record struct FilterItemData(
        string Name, string Type, string Size, string Modified, string FullPath);

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)            return $"{bytes} B";
        if (bytes < 1024 * 1024)     return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L*1024*1024) return $"{bytes / (1024.0*1024):F1} MB";
        return $"{bytes / (1024.0*1024*1024):F2} GB";
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
        _commandBar.UpdateViewToggles(
            detailsPane:    _detailsPanel.Visible,
            previewPane:    _previewPanel.Visible,
            navPane:        host?.IsNavPaneVisible ?? true,
            compactView:    ExplorerHost.IsCompactViewEnabled(),
            checkboxes:     ExplorerHost.IsCheckboxesEnabled(),
            fileExtensions: ExplorerHost.IsFileExtensionsVisible(),
            hiddenItems:    ExplorerHost.IsHiddenItemsVisible(),
            quickLook:      ExplorerHost.QuickLookEnabled);
    }

    // ── Command bar dispatch ─────────────────────────────────────────────────

    internal void ExecuteCommand(CommandBar.Cmd cmd) => OnCommandIssued(this, cmd);

    private void OnCommandIssued(object? sender, CommandBar.Cmd cmd)
    {
        var host = ActiveHost;

        switch (cmd)
        {
            // File operations
            case CommandBar.Cmd.NewFolder:      host?.CreateNewFolder();  break;
            case CommandBar.Cmd.Cut:            host?.Cut();              break;
            case CommandBar.Cmd.Copy:           host?.Copy();             break;
            case CommandBar.Cmd.CopyPaths:      host?.CopySelectedPaths(); break;
            case CommandBar.Cmd.Paste:          host?.Paste();            break;
            case CommandBar.Cmd.Rename:         host?.Rename();           break;
            case CommandBar.Cmd.Delete:         host?.Delete();           break;
            case CommandBar.Cmd.SelectAll:      host?.SelectAll();        break;
            case CommandBar.Cmd.Properties:     host?.ShowProperties();   break;
            case CommandBar.Cmd.FolderOptions:  OpenFolderOptions();      break;
            case CommandBar.Cmd.About:          ShowAbout();              break;
            case CommandBar.Cmd.ViewLog:        AppLog.OpenLogFile();                        break;
            case CommandBar.Cmd.SetHotkey:      SetHotkeyRequested?.Invoke(this, EventArgs.Empty); break;
            case CommandBar.Cmd.ToggleStartWithWindows:
                StartWithWindowsToggled?.Invoke(this, EventArgs.Empty);
                break;
            case CommandBar.Cmd.GoToParent:     host?.NavigateUp();                               break;
            case CommandBar.Cmd.MirrorToOther:
                MirrorToOtherRequested?.Invoke(this, CurrentPath());
                break;

            // View modes
            case CommandBar.Cmd.ViewDetails:    host?.SetViewMode(4);  break; // FVM_DETAILS
            case CommandBar.Cmd.ViewList:       host?.SetViewMode(3);  break; // FVM_LIST
            case CommandBar.Cmd.ViewTiles:      host?.SetViewMode(6);  break; // FVM_TILE
            case CommandBar.Cmd.ViewIcons:      host?.SetViewMode(1);  break; // FVM_ICON (large, 96px)
            case CommandBar.Cmd.ViewMediumIcons:
                host?.SetViewModeAndIconSize(1 /*FVM_ICON*/, 48);
                break;
            case CommandBar.Cmd.ViewSmallIcons: host?.SetViewMode(2);  break; // FVM_SMALLICON
            case CommandBar.Cmd.ViewContent:    host?.SetViewMode(8);  break; // FVM_CONTENT

            // Pane toggles
            case CommandBar.Cmd.ToggleDetailsPane:
                _detailsPanel.Visible = !_detailsPanel.Visible;
                if (!_detailsPanel.Visible) _detailsPanel.ShowItem(null);
                UpdateCommandBarToggles();
                break;
            case CommandBar.Cmd.TogglePreviewPane:
                _previewPanel.Visible = !_previewPanel.Visible;
                if (!_previewPanel.Visible) _previewPanel.Preview(null);
                UpdateCommandBarToggles();
                break;

            // Show submenu
            case CommandBar.Cmd.ShowNavPane:
                host?.ToggleNavPane();
                UpdateCommandBarToggles();
                break;
            case CommandBar.Cmd.ShowCompactView:
                ExplorerHost.ToggleCompactView();
                host?.RefreshShellView();
                UpdateCommandBarToggles();
                break;
            case CommandBar.Cmd.ShowCheckboxes:
                ExplorerHost.ToggleCheckboxes();
                host?.RefreshShellView();
                UpdateCommandBarToggles();
                break;
            case CommandBar.Cmd.ShowFileExtensions:
                ExplorerHost.ToggleFileExtensions();
                host?.RefreshShellView();
                UpdateCommandBarToggles();
                break;
            case CommandBar.Cmd.ShowHiddenItems:
                ExplorerHost.ToggleHiddenItems();
                host?.RefreshShellView();
                UpdateCommandBarToggles();
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
    {
        try { Process.Start("rundll32.exe", "shell32.dll,Options_RunDLL 0"); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(OpenFolderOptions),
                "Could not open Windows Folder Options.");
        }
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
