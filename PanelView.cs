using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// One panel of the dual-pane window.  Contains a tab bar, path bar, command bar,
/// and one ExplorerHost per open tab.  Only the active tab's host is visible.
/// Optional Details Pane (bottom) and Preview Pane (right) are toggled from the View menu.
/// </summary>
public sealed class PanelView : UserControl
{
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

    // ── Filter bar ────────────────────────────────────────────────────────────
    private readonly Panel    _filterBar;
    private readonly Label    _filterPrefix;
    private readonly TextBox  _filterTextBox;
    private readonly Button   _clearBtn;
    private readonly ListView _filterListView;
    private string _filterText = "";
    private readonly System.Windows.Forms.Timer _filterDebounce;

    private readonly List<ExplorerHost> _hosts = new();
    private int    _active         = -1;
    private string _lastActivePath = "";

    public PanelView()
    {
        _tabBar     = new TabBar     { Dock = DockStyle.Top };
        _pathBar    = new PathBar    { Dock = DockStyle.Top };
        _commandBar = new CommandBar { Dock = DockStyle.Top };
        _content    = new Panel      { Dock = DockStyle.None, BackColor = SystemColors.Window };

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
            BackColor = SystemColors.Info,
        };

        _filterPrefix = new Label
        {
            Text      = "Contains:",
            AutoSize  = false,
            Width     = 82,   // recalculated in ScaleFilterControls
            Dock      = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(6, 0, 0, 0),
            ForeColor = SystemColors.GrayText,
        };

        _filterTextBox = new TextBox
        {
            Dock        = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor   = SystemColors.Info,
        };
        _filterTextBox.TextChanged += OnFilterTextBoxChanged;
        _filterTextBox.KeyDown     += OnFilterTextBoxKeyDown;

        _clearBtn = new Button
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
        _hostContainer = new Panel        { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
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

        _pathBar.Navigate += (_, path) =>
        {
            ActiveHost?.NavigateTo(path);
            ActiveHost?.FocusShellView();
        };

        _commandBar.CommandIssued     += OnCommandIssued;
        _commandBar.ViewDropDownOpening += (_, _) => UpdateCommandBarToggles();
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
        string p = _hosts[_active].GetCurrentPath();
        _pathBar.SetPath(p);
        _tabBar.UpdateLabel(_active, FolderLabel(p), p);

        // Feed details/preview panes if visible
        if (_detailsPanel.Visible || _previewPanel.Visible)
        {
            string? sel = _hosts[_active].GetSelectedItemPath();
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
        host.FilterCharInput      += OnFilterCharInput;
        host.FilterBackspaceTyped += OnFilterBackspaceTyped;
        host.FilterEscapePressed  += (_, _) => ClearFilter();
        _hostContainer.Controls.Add(host);
        _hosts.Add(host);
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
            catch { }
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
        catch { }
        finally
        {
            if (menu    != IntPtr.Zero)  NativeMethods.DestroyMenu(menu);
            if (ctxMenu != null)         Marshal.ReleaseComObject(ctxMenu);
            if (folder  != null)         Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(pidl);
        }
    }

    private void OnFilterDebounce(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        PopulateFilterList(_filterText);
    }

    private void ClearFilter()
    {
        _filterDebounce.Stop();
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
            _filterBar.Visible = true;  // WinForms resizes _hostContainer; Resize fires ResizeAllHosts
        _filterListView.BringToFront(); // above ExplorerHosts inside _hostContainer
        _filterListView.Visible = true;
    }

    private void RestartFilterDebounce()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void PopulateFilterList(string filterText)
    {
        var host = ActiveHost;
        if (host == null) return;
        string folder = host.GetCurrentPath();
        if (!Directory.Exists(folder)) return;

        _filterListView.BeginUpdate();
        _filterListView.Items.Clear();
        try
        {
            var comp = StringComparison.OrdinalIgnoreCase;

            var dirs = Directory.GetDirectories(folder)
                .Select(Path.GetFileName)
                .Where(n => n != null && n.Contains(filterText, comp))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (string? name in dirs)
            {
                string full = Path.Combine(folder, name!);
                string mod  = Directory.GetLastWriteTime(full).ToString("g");
                var item    = new ListViewItem(new[] { name!, "Folder", "", mod }) { Tag = full };
                _filterListView.Items.Add(item);
            }

            var files = Directory.GetFiles(folder)
                .Select(Path.GetFileName)
                .Where(n => n != null && n.Contains(filterText, comp))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (string? name in files)
            {
                string full = Path.Combine(folder, name!);
                string ext  = Path.GetExtension(full).TrimStart('.').ToUpperInvariant();
                string type = ext.Length > 0 ? $"{ext} file" : "File";
                string size = FormatFileSize(new FileInfo(full).Length);
                string mod  = File.GetLastWriteTime(full).ToString("g");
                var item    = new ListViewItem(new[] { name!, type, size, mod }) { Tag = full };
                _filterListView.Items.Add(item);
            }
        }
        catch { /* permission denied or path changed */ }
        _filterListView.EndUpdate();
    }

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

    private void OnCommandIssued(object? sender, CommandBar.Cmd cmd)
    {
        var host = ActiveHost;

        switch (cmd)
        {
            // File operations
            case CommandBar.Cmd.NewFolder:      host?.CreateNewFolder();  break;
            case CommandBar.Cmd.Cut:            host?.Cut();              break;
            case CommandBar.Cmd.Copy:           host?.Copy();             break;
            case CommandBar.Cmd.Paste:          host?.Paste();            break;
            case CommandBar.Cmd.Rename:         host?.Rename();           break;
            case CommandBar.Cmd.Delete:         host?.Delete();           break;
            case CommandBar.Cmd.SelectAll:      host?.SelectAll();        break;
            case CommandBar.Cmd.Properties:     host?.ShowProperties();   break;
            case CommandBar.Cmd.FolderOptions:  OpenFolderOptions();      break;
            case CommandBar.Cmd.About:          ShowAbout();              break;
            case CommandBar.Cmd.ViewLog:        AppLog.OpenLogFile();                        break;
            case CommandBar.Cmd.SetHotkey:      SetHotkeyRequested?.Invoke(this, EventArgs.Empty); break;
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
        catch { }
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

        string message =
            "MultiExplorer is public domain software. It is released under the " +
            "Creative Commons CC0 1.0 Universal Public Domain Dedication. You can " +
            "copy, modify, distribute, and perform the work, even for commercial " +
            "purposes, all without asking permission.\n\n" +
            "Privacy Notice: Your privacy is fully respected. This application " +
            "operates entirely offline, features no advertisements, and collects " +
            "absolutely no user data, analytics, or personal information.\n\n" +
            $"Version: {version}\n" +
            $"Last build: {buildDate:d MMMM yyyy, h:mm tt}\n" +
            "Author: David Piscopo";

        MessageBox.Show(
            owner,
            message,
            "About MultiExplorer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
