using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiExplorer;

internal sealed class TabDropRequestedEventArgs(
    TabBar source, int sourceIndex, int targetIndex, bool copy) : EventArgs
{
    internal TabBar Source { get; } = source;
    internal int SourceIndex { get; } = sourceIndex;
    internal int TargetIndex { get; } = targetIndex;
    internal bool Copy { get; } = copy;
}

public sealed class TabBar : UserControl
{
    private const string TabDragFormat = "MultiExplorer.TabDrag";
    private const int DragKeyStateControl = 0x0008;
    private static volatile TabBar? s_dragSource;
    private static int s_dragSourceIndex = -1;
    private static bool s_tabDropHandled;
    internal static bool IsTabDragInProgress => s_dragSource is not null;
    private static readonly Lazy<IntPtr> s_copyDragCursor = new(() =>
    {
        // OLE's copy-drag cursor is the standard arrow with a plus badge.
        IntPtr ole32 = NativeMethods.GetModuleHandleW("ole32.dll");
        IntPtr cursor = ole32 == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.LoadCursor(ole32, new IntPtr(6));
        return cursor == IntPtr.Zero ? Cursors.Arrow.Handle : cursor;
    });

    // Logical (96-DPI) base values — scaled at runtime via LogicalToDeviceUnits.
    private int PadH   => LogicalToDeviceUnits(12);
    private int CloseW => LogicalToDeviceUnits(18);
    private int CloseG => LogicalToDeviceUnits(4);
    private int AddW   => LogicalToDeviceUnits(28);
    private int MaxW   => LogicalToDeviceUnits(250);
    private int Radius => LogicalToDeviceUnits(7);

    private readonly Font         _font;
    private readonly ToolTip      _tabTip = new ToolTip { AutomaticDelay = 400, ShowAlways = true };
    private readonly List<string> _labels   = new();
    private readonly List<string> _tooltips = new();
    private int  _active      = 0;
    private int  _hovTab      = -1;
    private int  _hovClose    = -1;
    private int  _lastTipTab  = -2; // -2 = not yet initialised
    private bool _hovAdd      = false;
    private int _dragCandidate = -1;
    private Point _dragStart;
    private Point _dragDropPosition;
    private bool _dragDropCompleted;
    private bool _dragDropCopyRequested;
    private TabDragFeedbackWindow? _dragFeedback;
    private int _dropIndex = -1;

    private readonly List<(Rectangle Tab, Rectangle Close)> _hits = new();
    private Rectangle _addHit;

    public event EventHandler<int>? TabSelected;
    public event EventHandler<int>? TabCloseRequested;
    public event EventHandler?      AddTabRequested;
    internal event EventHandler<TabDropRequestedEventArgs>? TabDropRequested;
    internal event Action<int, PanelView, bool>? TabDroppedOnPaneRequested;
    internal event Action<int, Point>? TabDroppedOutsideRequested;
    internal bool OpenExplorerWhenTabDroppedOutside { get; set; }

    public TabBar()
    {
        _font     = new Font("Segoe UI", 9.5f);
        Height    = LogicalToDeviceUnits(42);
        BackColor = ThemeManager.Background;
        AllowDrop = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _tabTip.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = LogicalToDeviceUnits(42);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = LogicalToDeviceUnits(42);
        Invalidate();
    }

    public void SetTabs(IReadOnlyList<string> labels, IReadOnlyList<string> tooltips, int active)
    {
        _labels.Clear();
        _labels.AddRange(labels);
        _tooltips.Clear();
        _tooltips.AddRange(tooltips);
        _active      = active;
        _hovTab      = _hovClose = -1;
        _lastTipTab  = -2;
        _hovAdd      = false;
        Invalidate();
    }

    public void SetActiveTab(int index)
    {
        if (_active == index) return;
        _active = index;
        Invalidate();
    }

    internal void ApplyTheme()
    {
        BackColor = ThemeManager.Background;
        ForeColor = ThemeManager.Text;
        Invalidate();
    }

    public void UpdateLabel(int index, string label, string tooltip)
    {
        bool changed = false;
        if (index >= 0 && index < _labels.Count && _labels[index] != label)
        {
            _labels[index] = label;
            changed = true;
        }
        if (index >= 0 && index < _tooltips.Count && _tooltips[index] != tooltip)
        {
            _tooltips[index] = tooltip;
            changed = true;
        }
        if (changed) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.PixelOffsetMode    = PixelOffsetMode.HighQuality;

        _hits.Clear();

        // EndEllipsis is a drawing constraint. Passing it to MeasureText with an
        // unconstrained Size.Empty can produce a tiny measured width, which then
        // makes otherwise short folder names render as one letter plus "...".
        const TextFormatFlags MeasureTff =
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        const TextFormatFlags DrawTff =
            MeasureTff | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

        // Leave 1 px at the bottom for the separator line; tabs fill the rest.
        int tabH = ClientSize.Height - 1;
        int x    = 6;

        for (int i = 0; i < _labels.Count; i++)
        {
            int textW = TextRenderer.MeasureText(
                g, _labels[i], _font, Size.Empty, MeasureTff).Width;
            textW = Math.Min(textW, MaxW - PadH * 2 - CloseG - CloseW);
            int tabW = PadH + textW + CloseG + CloseW + PadH;

            var tabR   = new Rectangle(x, 0, tabW, tabH);
            var closeR = new Rectangle(
                tabR.Right - PadH - CloseW + 1,
                tabR.Y + (tabH - CloseW) / 2,
                CloseW, CloseW);
            _hits.Add((tabR, closeR));

            bool isActive  = i == _active;
            bool isHovered = i == _hovTab;

            // Background fill with rounded top corners
            using (var path = TopRoundedRect(tabR, Radius))
            using (var br   = new SolidBrush(isActive
                       ? ThemeManager.ActiveTabBackground
                       : isHovered ? ThemeManager.Hover : ThemeManager.Surface))
                g.FillPath(br, path);

            // Accent bar across top of active tab
            if (isActive)
            {
                var accentR = new Rectangle(tabR.X + Radius, tabR.Y + 1, tabR.Width - Radius * 2, 3);
                using var br = new SolidBrush(ThemeManager.Accent);
                g.FillRectangle(br, accentR);
            }

            // Border: left side, top arc, right side (no bottom on active tab)
            using var pen = new Pen(ThemeManager.Border);
            if (isActive)
            {
                using var borderPath = TopRoundedRectBorder(tabR, Radius);
                g.DrawPath(pen, borderPath);
            }
            else
            {
                using var path = TopRoundedRect(tabR, Radius);
                g.DrawPath(pen, path);
            }

            // Vertically centre the label on the close glyph.
            var labelR = new Rectangle(tabR.X + PadH, tabR.Y + 2, textW + 4, tabH - 4);
            Color textColor = isActive
                ? ThemeManager.ActiveTabText
                : ThemeManager.Text;
            TextRenderer.DrawText(g, _labels[i], _font, labelR, textColor, DrawTff);

            // Close button (active tab always; inactive tab on hover)
            if (isActive || isHovered)
            {
                bool cHov = i == _hovClose;
                if (cHov)
                    using (var br = new SolidBrush(ThemeManager.Pressed))
                        g.FillEllipse(br, closeR.X + 1, closeR.Y + 1, closeR.Width - 2, closeR.Height - 2);

                int cx = closeR.X + closeR.Width  / 2;
                int cy = closeR.Y + closeR.Height / 2;
                using var cp = new Pen(
                    cHov
                        ? ThemeManager.Text
                        : isActive
                            ? ThemeManager.ActiveTabText
                            : ThemeManager.MutedText,
                    1.5f);
                g.DrawLine(cp, cx - 4, cy - 4, cx + 4, cy + 4);
                g.DrawLine(cp, cx + 4, cy - 4, cx - 4, cy + 4);
            }

            x = tabR.Right + 2; // 2 px gap between tabs
        }

        // "+" add-tab button
        int addY = (tabH - AddW) / 2;
        _addHit = new Rectangle(x + 2, addY, AddW, AddW);
        if (_hovAdd)
            using (var br = new SolidBrush(ThemeManager.Hover))
                g.FillRectangle(br, _addHit);

        int ax = _addHit.X + AddW / 2;
        int ay = _addHit.Y + AddW / 2;
        using var ap = new Pen(ThemeManager.Text, 1.5f);
        g.DrawLine(ap, ax - 5, ay,     ax + 5, ay);
        g.DrawLine(ap, ax,     ay - 5, ax,     ay + 5);

        // Bottom separator line
        using var bp = new Pen(ThemeManager.Border);
        g.DrawLine(bp, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);

        if (_dropIndex >= 0)
        {
            int indicatorX = DropIndicatorX(_dropIndex);
            using var dropPen = new Pen(ThemeManager.Accent, LogicalToDeviceUnits(2));
            g.DrawLine(dropPen, indicatorX, LogicalToDeviceUnits(3),
                indicatorX, tabH - LogicalToDeviceUnits(3));
        }
    }

    // Full rounded-top-corner closed path (left + top arcs + right + bottom).
    private static GraphicsPath TopRoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X,               r.Y, radius * 2, radius * 2, 180, 90); // top-left
        p.AddArc(r.Right - radius*2, r.Y, radius * 2, radius * 2, 270, 90); // top-right
        p.AddLine(r.Right, r.Y + radius, r.Right, r.Bottom);                 // right side
        p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);                         // bottom
        p.AddLine(r.X, r.Bottom, r.X, r.Y + radius);                        // left side
        p.CloseFigure();
        return p;
    }

    // Open path: left side + top arcs + right side — no bottom line (for active tab).
    private static GraphicsPath TopRoundedRectBorder(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        p.AddLine(r.X, r.Bottom, r.X, r.Y + radius);                         // left side up
        p.AddArc(r.X,               r.Y, radius * 2, radius * 2, 180, 90);  // top-left arc
        p.AddArc(r.Right - radius*2, r.Y, radius * 2, radius * 2, 270, 90); // top-right arc
        p.AddLine(r.Right, r.Y + radius, r.Right, r.Bottom);                  // right side down
        return p;
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (TryStartTabDrag(e)) return;

        int  ht = -1, hc = -1;
        bool ha = _addHit.Contains(e.Location);

        if (!ha)
        {
            for (int i = 0; i < _hits.Count; i++)
            {
                if (!_hits[i].Tab.Contains(e.Location)) continue;
                ht = i;
                if (_hits[i].Close.Contains(e.Location)) hc = i;
                break;
            }
        }

        if (ht == _hovTab && hc == _hovClose && ha == _hovAdd) return;
        _hovTab   = ht;
        _hovClose = hc;
        _hovAdd   = ha;
        Cursor    = (ht >= 0 || ha) ? Cursors.Hand : Cursors.Default;
        Invalidate();

        // Tooltip: show full path when hovering a tab, hide otherwise.
        if (_hovTab != _lastTipTab)
        {
            _lastTipTab = _hovTab;
            _tabTip.Hide(this);
            if (_hovTab >= 0 && _hovTab < _tooltips.Count)
            {
                string tip = _tooltips[_hovTab];
                if (!string.IsNullOrEmpty(tip))
                    _tabTip.Show(tip, this, e.X, Height + 2, 6000);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tabTip.Hide(this);
        _lastTipTab = -2;
        if (_hovTab < 0 && _hovClose < 0 && !_hovAdd) return;
        _hovTab = _hovClose = -1;
        _hovAdd = false;
        Cursor  = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (_addHit.Contains(e.Location)) { AddTabRequested?.Invoke(this, EventArgs.Empty); return; }

        for (int i = 0; i < _hits.Count; i++)
        {
            if (!_hits[i].Tab.Contains(e.Location)) continue;
            if (_hits[i].Close.Contains(e.Location))
                TabCloseRequested?.Invoke(this, i);
            else
            {
                _dragCandidate = i;
                _dragStart = e.Location;
                TabSelected?.Invoke(this, i);
            }
            return;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragCandidate = -1;
    }

    protected override void OnQueryContinueDrag(QueryContinueDragEventArgs e)
    {
        base.OnQueryContinueDrag(e);
        Point position = Cursor.Position;
        UpdateDragFeedback(position);
        SetDragCursor(position);

        if (e.Action != DragAction.Drop || e.EscapePressed) return;
        _dragDropCompleted = true;
        _dragDropCopyRequested = (e.KeyState & DragKeyStateControl) != 0;
        _dragDropPosition = position;
    }

    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        base.OnGiveFeedback(e);
        Point position = Cursor.Position;
        UpdateDragFeedback(position);
        if (!IsOutsideOwner(position) && GetPaneAt(position) is null) return;

        e.UseDefaultCursors = false;
        SetDragCursor(position);
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        UpdateDragTarget(drgevent);
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        UpdateDragTarget(drgevent);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        SetDropIndex(-1);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        int targetIndex = _dropIndex;
        SetDropIndex(-1);
        if (drgevent.Data?.GetDataPresent(TabDragFormat) != true
            || !TryGetDraggedTab(out TabBar? source, out int sourceIndex)
            || (!ReferenceEquals(source, this) && targetIndex < 0))
        {
            drgevent.Effect = DragDropEffects.None;
            return;
        }

        bool samePane = ReferenceEquals(source, this);
        if (samePane) targetIndex = _labels.Count;
        bool copy = samePane || (drgevent.KeyState & DragKeyStateControl) != 0;
        drgevent.Effect = EffectForPaneDrop(samePane, copy);
        s_tabDropHandled = true;
        TabDropRequested?.Invoke(this,
            new TabDropRequestedEventArgs(source!, sourceIndex, targetIndex, copy));
    }

    private bool TryStartTabDrag(MouseEventArgs e)
    {
        if (_dragCandidate < 0 || e.Button != MouseButtons.Left) return false;
        Size dragSize = SystemInformation.DragSize;
        var threshold = new Rectangle(
            _dragStart.X - dragSize.Width / 2,
            _dragStart.Y - dragSize.Height / 2,
            dragSize.Width,
            dragSize.Height);
        if (threshold.Contains(e.Location)) return false;

        int sourceIndex = _dragCandidate;
        _dragCandidate = -1;
        if (sourceIndex >= _labels.Count) return false;
        s_dragSource = this;
        s_dragSourceIndex = sourceIndex;
        s_tabDropHandled = false;
        _dragDropCompleted = false;
        _dragDropCopyRequested = false;
        Point dropPosition;
        bool dropCompleted;
        bool copyRequested;
        bool tabDropHandled;
        using var feedback = new TabDragFeedbackWindow(
            _labels[sourceIndex],
            sourceIndex < _tooltips.Count ? _tooltips[sourceIndex] : string.Empty);
        _dragFeedback = feedback;
        try
        {
            UpdateDragFeedback(Cursor.Position);
            var data = new DataObject();
            data.SetData(TabDragFormat, "tab");
            DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
            dropCompleted = _dragDropCompleted;
            copyRequested = _dragDropCopyRequested;
            dropPosition = _dragDropPosition;
            tabDropHandled = s_tabDropHandled;
        }
        finally
        {
            s_dragSource = null;
            s_dragSourceIndex = -1;
            s_tabDropHandled = false;
            _dragDropCompleted = false;
            _dragDropCopyRequested = false;
            _dragFeedback = null;
            SetDropIndex(-1);
        }

        PanelView? targetPane = GetPaneAt(dropPosition);
        if (ShouldHandlePaneDrop(
            dropCompleted, tabDropHandled, targetPane is not null))
            TabDroppedOnPaneRequested?.Invoke(sourceIndex, targetPane!, copyRequested);
        else if (ShouldOpenExplorerAfterDrag(
            OpenExplorerWhenTabDroppedOutside,
            dropCompleted,
            tabDropHandled,
            FindForm()?.Bounds ?? Rectangle.Empty,
            dropPosition))
            TabDroppedOutsideRequested?.Invoke(sourceIndex, dropPosition);
        return true;
    }

    internal static bool ShouldOpenExplorerAfterDrag(
        bool enabled,
        bool dropCompleted,
        bool tabDropHandled,
        Rectangle ownerBounds,
        Point dropPosition) =>
        enabled
        && dropCompleted
        && !tabDropHandled
        && !ownerBounds.Contains(dropPosition);

    internal static bool ShouldHandlePaneDrop(
        bool dropCompleted, bool tabDropHandled, bool overPane) =>
        dropCompleted && !tabDropHandled && overPane;

    internal static DragDropEffects EffectForPaneDrop(bool samePane,
        bool copyRequested) =>
        samePane || copyRequested ? DragDropEffects.Copy : DragDropEffects.Move;

    internal static TabDragFeedbackState GetDragFeedbackState(
        bool isWithinSourcePane,
        bool isOutsideOwner,
        bool isOverOtherPane,
        bool enabled,
        bool copyRequested) =>
        isWithinSourcePane
            ? TabDragFeedbackState.DuplicateInCurrentPane
            : isOutsideOwner
                ? enabled
                    ? TabDragFeedbackState.OpenInFileExplorer
                    : TabDragFeedbackState.Hidden
                : isOverOtherPane
                    ? copyRequested
                        ? TabDragFeedbackState.CopyToOtherPane
                        : TabDragFeedbackState.MoveToOtherPane
                    : TabDragFeedbackState.Hidden;

    private bool IsWithinSourcePane(Point position) =>
        Parent is PanelView sourcePane
        && sourcePane.RectangleToScreen(sourcePane.ClientRectangle).Contains(position);

    private bool IsOutsideOwner(Point position) =>
        FindForm() is { } owner && !owner.Bounds.Contains(position);

    private PanelView? GetPaneAt(Point position)
    {
        if (Parent is not PanelView sourcePane
            || sourcePane.Parent is not { } container)
            return null;

        return container.Controls.OfType<PanelView>().FirstOrDefault(pane =>
            pane.Visible
            && !pane.IsDisposed
            && !pane.Disposing
            && pane.RectangleToScreen(pane.ClientRectangle).Contains(position));
    }

    private void SetDragCursor(Point position)
    {
        if (IsOutsideOwner(position))
            NativeMethods.SetCursor(
                (OpenExplorerWhenTabDroppedOutside
                    ? s_copyDragCursor.Value
                    : Cursors.No.Handle));
        else if (GetPaneAt(position) is { } pane)
            NativeMethods.SetCursor(ReferenceEquals(pane, Parent)
                || ModifierKeys.HasFlag(Keys.Control)
                ? s_copyDragCursor.Value
                : Cursors.Arrow.Handle);
    }

    private void UpdateDragFeedback(Point position)
    {
        if (_dragFeedback is null) return;
        _dragFeedback.ShowFeedback(position, GetDragFeedbackState(
            IsWithinSourcePane(position),
            IsOutsideOwner(position),
            GetPaneAt(position) is { } pane
                && !ReferenceEquals(pane, Parent),
            OpenExplorerWhenTabDroppedOutside,
            ModifierKeys.HasFlag(Keys.Control)));
    }

    private void UpdateDragTarget(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(TabDragFormat) != true
            || !TryGetDraggedTab(out TabBar? source, out _))
        {
            e.Effect = DragDropEffects.None;
            SetDropIndex(-1);
            return;
        }

        bool samePane = ReferenceEquals(source, this);
        e.Effect = EffectForPaneDrop(samePane,
            (e.KeyState & DragKeyStateControl) != 0);
        SetDropIndex(samePane
            ? -1
            : InsertionIndexAt(PointToClient(new Point(e.X, e.Y)).X));
    }

    private static bool TryGetDraggedTab(out TabBar? source, out int sourceIndex)
    {
        source = s_dragSource;
        sourceIndex = s_dragSourceIndex;
        return source != null && sourceIndex >= 0;
    }

    private int InsertionIndexAt(int x)
    {
        for (int i = 0; i < _hits.Count; i++)
        {
            if (x < _hits[i].Tab.Left + _hits[i].Tab.Width / 2)
                return i;
        }
        return _hits.Count;
    }

    private int DropIndicatorX(int index)
    {
        if (_hits.Count == 0) return LogicalToDeviceUnits(6);
        if (index <= 0) return _hits[0].Tab.Left - LogicalToDeviceUnits(2);
        if (index >= _hits.Count) return _hits[^1].Tab.Right + LogicalToDeviceUnits(1);
        return _hits[index].Tab.Left - LogicalToDeviceUnits(1);
    }

    private void SetDropIndex(int index)
    {
        if (_dropIndex == index) return;
        _dropIndex = index;
        Invalidate();
    }
}
