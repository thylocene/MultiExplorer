using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MultiExplorer;

public sealed class PathBar : UserControl
{
    internal const int MaxHistoryEntries = 50;
    // Logical (96-DPI) base values — scaled at runtime via LogicalToDeviceUnits.
    private int SegPadH => LogicalToDeviceUnits(8);
    private int SegPadV => LogicalToDeviceUnits(3);
    private int HoverR  => LogicalToDeviceUnits(4);
    private int SepGap  => LogicalToDeviceUnits(5);
    private int LeftMgn => LogicalToDeviceUnits(8);

    private readonly TextBox _editor;
    private readonly Button  _historyButton;
    private readonly Font    _font;
    private readonly Font    _editorFont;
    private readonly List<string> _history = new();
    private HistoryPopup? _historyPopup;
    private bool _committingEdit;

    private bool   _editing;
    private bool   _editorFocused;
    private bool   _autoCompleteInitialized;
    private string _path       = "";
    private int    _hovered    = -1;
    private int    _hoveredSep = -1;

    private readonly List<(Rectangle Hit, string FullPath, string Display)> _segs = new();
    // SegIndex = index into _segs of the segment immediately LEFT of this ›
    private readonly List<(Rectangle Hit, int SegIndex)> _seps = new();

    public event EventHandler<string>? Navigate;

    public void SetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == _path) return;
        AddToHistory(path);
        _path    = path;
        _hovered = -1;
        if (!_editing) Invalidate();
    }

    public void SetHistory(IEnumerable<string>? paths)
    {
        _history.Clear();
        if (paths == null) return;
        foreach (string path in paths.Reverse())
            AddToHistory(path);
    }

    public List<string> GetHistory() => new(_history);

    public PathBar()
    {
        _font       = new Font("Segoe UI Semibold", 10f);
        _editorFont = new Font("Segoe UI", 10f, FontStyle.Regular);

        Height      = LogicalToDeviceUnits(32);
        BackColor   = ThemeManager.Surface;
        BorderStyle = BorderStyle.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.ResizeRedraw, true);

        _editor = new TextBox
        {
            Font               = _editorFont,
            BorderStyle        = BorderStyle.None,
            BackColor          = ThemeManager.Surface,
            ForeColor          = ThemeManager.Text,
            Visible            = false,
        };
        _editor.KeyDown   += OnEditorKeyDown;
        _editor.GotFocus  += (_, _) => { _editorFocused = true;  Invalidate(); };
        _editor.LostFocus += (_, _) =>
        {
            _editorFocused = false;
            if (!_editing || IsDisposed || Disposing) return;
            BeginInvoke(() =>
            {
                if (_editing && !_committingEdit && !ContainsFocus
                    && _historyPopup?.ContainsFocus != true)
                    SwitchToNav();
            });
        };

        _historyButton = new Button
        {
            Text      = "×",
            Font      = new Font("Segoe UI", 13f, FontStyle.Regular),
            FlatStyle = FlatStyle.Flat,
            TabStop   = false,
            Visible   = false,
            BackColor = ThemeManager.Surface,
            ForeColor = ThemeManager.MutedText,
            Cursor    = Cursors.Hand,
            AccessibleName = "Clear address",
        };
        _historyButton.FlatAppearance.BorderSize = 0;
        _historyButton.FlatAppearance.MouseOverBackColor = ThemeManager.AccentHover;
        _historyButton.FlatAppearance.MouseDownBackColor = ThemeManager.AccentHover;
        _historyButton.Click += (_, _) => ClearEditor();

        Controls.Add(_editor);
        Controls.Add(_historyButton);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _historyPopup?.Dispose();
            _font.Dispose();
            _editorFont.Dispose();
            _historyButton.Font?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = LogicalToDeviceUnits(32);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = LogicalToDeviceUnits(32);
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_editor == null || _historyButton == null || ClientSize.Width <= 0) return;
        const int margin = 3;
        int buttonWidth = LogicalToDeviceUnits(28);
        int textH = _editor.PreferredHeight;
        int y     = Math.Max(margin, (ClientSize.Height - textH) / 2);
        _historyButton.SetBounds(
            ClientSize.Width - buttonWidth - 1, 1,
            buttonWidth, Math.Max(1, ClientSize.Height - 2));
        _editor.SetBounds(
            LogicalToDeviceUnits(8), y,
            Math.Max(1, _historyButton.Left - LogicalToDeviceUnits(12)), textH);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_editing && (keyData == Keys.F4 || keyData == Keys.Return))
        { SwitchToEdit(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        using (var pen = new Pen((_editing && _editorFocused)
            ? ThemeManager.Accent
            : ThemeManager.NavigationBorder))
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        if (_editing || string.IsNullOrEmpty(_path)) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        _segs.Clear();
        _seps.Clear();
        var raw = BuildSegments(_path);
        if (raw.Count == 0) return;

        const string Sep = "›";
        const TextFormatFlags TFF = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        int textH = TextRenderer.MeasureText(g, "Ag", _font, Size.Empty, TFF).Height;
        int textY = (ClientSize.Height - textH) / 2;
        int sepW  = TextRenderer.MeasureText(g, Sep, _font, Size.Empty, TFF).Width;
        int x     = LeftMgn;

        for (int i = 0; i < raw.Count; i++)
        {
            var (display, fullPath) = raw[i];
            int segW = TextRenderer.MeasureText(g, display, _font, Size.Empty, TFF).Width;
            var hit  = new Rectangle(x, 0, segW + SegPadH * 2, ClientSize.Height);
            _segs.Add((hit, fullPath, display));

            if (i == _hovered)
            {
                var pill = new Rectangle(hit.X, SegPadV, hit.Width, hit.Height - SegPadV * 2);
                using var segBrush = new SolidBrush(ThemeManager.AccentHover);
                using var segPath  = MakeRoundRect(pill, HoverR);
                g.FillPath(segBrush, segPath);
            }

            Color textColor = i == _hovered || i == raw.Count - 1
                ? ThemeManager.AccentText
                : ThemeManager.Text;
            TextRenderer.DrawText(g, display, _font, new Point(hit.X + SegPadH, textY),
                textColor, TFF);

            x = hit.Right + SepGap;

            if (i < raw.Count - 1)
            {
                var sepHit = new Rectangle(x - SepGap, 0, sepW + SepGap * 2, ClientSize.Height);
                int sepIdx = _seps.Count;
                _seps.Add((sepHit, i));

                if (sepIdx == _hoveredSep)
                {
                    var pill = new Rectangle(sepHit.X, SegPadV, sepHit.Width, sepHit.Height - SegPadV * 2);
                    using var sepBrush = new SolidBrush(ThemeManager.AccentHover);
                    using var sepPath  = MakeRoundRect(pill, HoverR);
                    g.FillPath(sepBrush, sepPath);
                }

                Color sepColor = sepIdx == _hoveredSep ? ThemeManager.AccentText : ThemeManager.MutedText;
                TextRenderer.DrawText(g, Sep, _font, new Point(x, textY), sepColor, TFF);
                x += sepW + SepGap;
            }
        }
    }

    public void ApplyTheme()
    {
        BackColor = ThemeManager.Surface;
        ForeColor = ThemeManager.Text;
        _editor.BackColor = ThemeManager.Surface;
        _editor.ForeColor = ThemeManager.Text;
        _historyButton.BackColor = ThemeManager.Surface;
        _historyButton.ForeColor = ThemeManager.MutedText;
        _historyButton.FlatAppearance.MouseOverBackColor = ThemeManager.AccentHover;
        _historyButton.FlatAppearance.MouseDownBackColor = ThemeManager.AccentHover;
        _historyPopup?.ApplyTheme();
        Invalidate(true);
    }

    private static GraphicsPath MakeRoundRect(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X,         r.Y,          d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        p.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_editing) return;
        int h  = HitTest(e.Location);
        int hs = h < 0 ? HitTestSep(e.Location) : -1;
        if (h == _hovered && hs == _hoveredSep) return;
        _hovered    = h;
        _hoveredSep = hs;
        Cursor = (h >= 0 || hs >= 0) ? Cursors.Hand : Cursors.IBeam;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        bool changed = _hovered != -1 || _hoveredSep != -1;
        _hovered    = -1;
        _hoveredSep = -1;
        Cursor = Cursors.IBeam;
        if (changed) Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_editing || e.Button != MouseButtons.Left) return;
        int h = HitTest(e.Location);
        if (h >= 0) { Navigate?.Invoke(this, _segs[h].FullPath); return; }
        int hs = HitTestSep(e.Location);
        if (hs >= 0) { ShowSeparatorMenu(hs); return; }
        SwitchToEdit();
    }

    private int HitTest(Point pt)    => _segs.FindIndex(s => s.Hit.Contains(pt));
    private int HitTestSep(Point pt) => _seps.FindIndex(s => s.Hit.Contains(pt));

    private void SwitchToEdit()
    {
        // File-system autocomplete initializes Windows shell services when the
        // TextBox handle is created. Defer that cold-start cost until the user
        // actually opens the address editor.
        if (!_autoCompleteInitialized)
        {
            _editor.AutoCompleteSource = AutoCompleteSource.FileSystemDirectories;
            _editor.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _autoCompleteInitialized = true;
        }

        _editing        = true;
        _editor.Text    = _path;
        _editor.Visible = true;
        _historyButton.Visible = true;
        _editor.Focus();
        _editor.SelectAll();
        Invalidate();
        // Explorer immediately exposes recent locations while the address is editable.
        BeginInvoke(() =>
        {
            if (_editing && !IsDisposed && !Disposing)
                ShowHistoryMenu();
        });
    }

    private void SwitchToNav()
    {
        _editing        = false;
        _historyPopup?.Close();
        _historyPopup = null;
        _editor.Visible = false;
        _historyButton.Visible = false;
        _hovered        = -1;
        _hoveredSep     = -1;
        Invalidate();
    }

    internal void DismissHistoryPopup()
    {
        if (_historyPopup?.Visible == true)
            _historyPopup.Close();
    }

    private void ClearEditor()
    {
        _editor.Clear();
        _editor.Focus();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return)
        {
            e.SuppressKeyPress = true;
            CommitEditorPath();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            SwitchToNav();
        }
        else if (e.KeyCode == Keys.F4
                 || (e.KeyCode == Keys.Down && e.Alt))
        {
            e.SuppressKeyPress = true;
            ShowHistoryMenu();
        }
    }

    private void CommitEditorPath(string? selectedPath = null)
    {
        string typed = (selectedPath ?? _editor.Text).Trim();
        _committingEdit = true;
        try { SwitchToNav(); }
        finally { _committingEdit = false; }
        if (typed.Length == 0) return;
        Navigate?.Invoke(this, Environment.ExpandEnvironmentVariables(typed));
    }

    private void ShowHistoryMenu()
    {
        if (!_editing) return;

        // Include the current path, as Explorer does. This also gives a newly
        // used pane a visible dropdown before it has accumulated older entries.
        string[] entries = _history.ToArray();
        if (entries.Length == 0) return;

        int itemHeight = LogicalToDeviceUnits(32);
        var popup = new HistoryPopup(
            entries,
            Math.Max(LogicalToDeviceUnits(240), ClientSize.Width),
            itemHeight,
            _editorFont);
        popup.PathChosen += (_, path) => CommitEditorPath(path);
        popup.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_historyPopup, popup)) return;
            _historyPopup = null;
            if (!_editing || IsDisposed || Disposing) return;
            BeginInvoke(() =>
            {
                if (_editing && !IsDisposed && !Disposing)
                    SwitchToNav();
            });
        };
        HistoryPopup? previousPopup = _historyPopup;
        _historyPopup = popup;
        previousPopup?.Close();

        Point location = PointToScreen(new Point(0, Height - 1));
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        if (location.X + popup.Width > workingArea.Right)
            location.X = workingArea.Right - popup.Width;
        if (location.Y + popup.Height > workingArea.Bottom)
            location.Y = PointToScreen(Point.Empty).Y - popup.Height + 1;
        popup.Location = location;
        popup.Show(FindForm());
    }

    private void AddToHistory(string path)
    {
        string candidate = path.Trim();
        if (candidate.Length == 0) return;
        _history.RemoveAll(existing => PathsEqual(existing, candidate));
        _history.Insert(0, candidate);
        if (_history.Count > MaxHistoryEntries)
            _history.RemoveRange(MaxHistoryEntries, _history.Count - MaxHistoryEntries);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            second.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed class HistoryPopup : Form
    {
        private readonly ListBox _list;
        private readonly int _cornerRadius;

        internal event EventHandler<string>? PathChosen;

        internal HistoryPopup(
            IReadOnlyList<string> entries, int width, int itemHeight, Font font)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Width = width;
            Height = Math.Min(entries.Count, 12) * itemHeight + 7;
            Padding = new Padding(1, 1, 1, 5);
            BackColor = ThemeManager.Surface;
            _cornerRadius = Math.Max(8,
                PopupVisuals.CornerRadiusLogical * DeviceDpi / 96);

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = itemHeight,
                Font = font,
                BackColor = ThemeManager.Surface,
                ForeColor = ThemeManager.Text,
                HorizontalScrollbar = false,
            };
            foreach (string entry in entries) _list.Items.Add(entry);
            _list.DrawItem += DrawHistoryItem;
            _list.MouseMove += (_, e) =>
            {
                int index = _list.IndexFromPoint(e.Location);
                if (index >= 0 && _list.SelectedIndex != index)
                    _list.SelectedIndex = index;
            };
            _list.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                int index = _list.IndexFromPoint(e.Location);
                if (index < 0 || index >= _list.Items.Count) return;

                // A non-activating popup must commit on mouse-down: waiting for
                // MouseClick allows the address editor's focus-loss callback to
                // close the popup before the item can be chosen.
                _list.SelectedIndex = index;
                ChooseSelectedPath();
            };
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ChooseSelectedPath();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    Close();
                }
            };
            Controls.Add(_list);
        }

        // Keep the address TextBox focused so the user can type as soon as edit
        // mode opens. The list still receives mouse clicks to select a history item.
        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            PopupVisuals.DisableNonClientShadow(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) UpdateRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using GraphicsPath path = PopupVisuals.CreateBottomRoundedPath(
                new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f),
                    Math.Max(1, Height - 1f)), _cornerRadius);
            using var pen = new Pen(ThemeManager.Border);
            SmoothingMode oldMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = oldMode;
        }

        internal void ApplyTheme()
        {
            BackColor = ThemeManager.Surface;
            _list.BackColor = ThemeManager.Surface;
            _list.ForeColor = ThemeManager.Text;
            Invalidate(true);
        }

        private void ChooseSelectedPath()
        {
            if (_list.SelectedItem is not string path) return;
            PathChosen?.Invoke(this, path);
            Close();
        }

        private void DrawHistoryItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var background = new SolidBrush(
                selected ? ThemeManager.MenuHighlight : ThemeManager.Surface))
                e.Graphics.FillRectangle(background, e.Bounds);

            string text = _list.Items[e.Index]?.ToString() ?? string.Empty;
            int left = Math.Max(8, DeviceDpi * 10 / 96);
            var textBounds = new Rectangle(
                e.Bounds.Left + left, e.Bounds.Top,
                Math.Max(1, e.Bounds.Width - left * 2), e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics, text, _list.Font, textBounds, ThemeManager.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using GraphicsPath path = PopupVisuals.CreateBottomRoundedPath(
                new Rectangle(0, 0, Width, Height), _cornerRadius);
            Region? previous = Region;
            Region = new Region(path);
            previous?.Dispose();
        }

    }

    private void ShowSeparatorMenu(int sepIndex)
    {
        if (sepIndex < 0 || sepIndex >= _seps.Count) return;
        int segIdx = _seps[sepIndex].SegIndex;
        if (segIdx < 0 || segIdx >= _segs.Count) return;

        string parentPath = _segs[segIdx].FullPath;
        string[] dirs;
        try { dirs = Directory.GetDirectories(parentPath); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppLog.Debug(ex, nameof(ShowSeparatorMenu),
                $"Could not enumerate \"{parentPath}\" for the breadcrumb menu.");
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ShowSeparatorMenu),
                $"Could not build the breadcrumb menu for \"{parentPath}\".");
            return;
        }

        if (dirs.Length == 0) return;

        var menu = new ContextMenuStrip { Font = _font };
        foreach (string dir in dirs.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            string? name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) continue;
            string navTarget = dir;
            // Defer navigation via BeginInvoke so it runs after the menu has fully
            // closed and disposed — avoids reentrancy when SetFocus sends WM_KILLFOCUS
            // back to the menu while its click handler is still on the stack.
            menu.Items.Add(name, null, (_, _) => BeginInvoke(() => Navigate?.Invoke(this, navTarget)));
        }
        if (menu.Items.Count == 0) { menu.Dispose(); return; }

        ThemeManager.ApplyToolStrip(menu);

        menu.Show(this, new Point(_seps[sepIndex].Hit.Left, Height));
    }

    private static List<(string Display, string FullPath)> BuildSegments(string rawPath)
    {
        var result = new List<(string, string)>();
        try
        {
            string path = Path.GetFullPath(rawPath);
            while (true)
            {
                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name))
                {
                    result.Insert(0, (path.TrimEnd(Path.DirectorySeparatorChar), path));
                    break;
                }
                result.Insert(0, (name, path));
                string? parent = Path.GetDirectoryName(path);
                if (parent == null || parent == path) break;
                path = parent;
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException)
        {
            AppLog.Debug(ex, nameof(BuildSegments),
                $"Could not construct breadcrumbs for \"{rawPath}\".");
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(BuildSegments),
                $"Unexpected failure constructing breadcrumbs for \"{rawPath}\".");
        }
        return result;
    }
}
