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
    // Logical (96-DPI) base values — scaled at runtime via LogicalToDeviceUnits.
    private int SegPadH => LogicalToDeviceUnits(8);
    private int SegPadV => LogicalToDeviceUnits(3);
    private int HoverR  => LogicalToDeviceUnits(4);
    private int SepGap  => LogicalToDeviceUnits(5);
    private int LeftMgn => LogicalToDeviceUnits(8);

    private readonly TextBox _editor;
    private readonly Font    _font;

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
        _path    = path;
        _hovered = -1;
        if (!_editing) Invalidate();
    }

    public PathBar()
    {
        _font = new Font("Segoe UI Semibold", 10f);

        Height      = LogicalToDeviceUnits(32);
        BackColor   = ThemeManager.Surface;
        BorderStyle = BorderStyle.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.ResizeRedraw, true);

        _editor = new TextBox
        {
            Font               = new Font("Segoe UI Semibold", 10f),
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
            if (_editing) SwitchToNav();
        };

        Controls.Add(_editor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _editor.Font?.Dispose(); }
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
        if (_editor == null || ClientSize.Width <= 0) return;
        const int margin = 3;
        int textH = _editor.PreferredHeight;
        int y     = Math.Max(margin, (ClientSize.Height - textH) / 2);
        _editor.SetBounds(margin, y, ClientSize.Width - margin * 2, textH);
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
        _editor.Focus();
        _editor.SelectAll();
        Invalidate();
    }

    private void SwitchToNav()
    {
        _editing        = false;
        _editor.Visible = false;
        _hovered        = -1;
        _hoveredSep     = -1;
        Invalidate();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return)
        {
            e.SuppressKeyPress = true;
            string typed = _editor.Text.Trim();
            SwitchToNav();
            if (!string.IsNullOrEmpty(typed))
            {
                string expanded = Environment.ExpandEnvironmentVariables(typed);
                Navigate?.Invoke(this, expanded);
            }
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            SwitchToNav();
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
