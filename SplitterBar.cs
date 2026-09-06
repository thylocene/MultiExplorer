using System;
using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// A thin vertical bar that separates the two explorer panels.
/// Supports dragging to resize and clicking arrow buttons to collapse/expand panels.
///
/// Visibility rules when a panel is collapsed:
///   Left collapsed  → only the left  (expand) arrow is shown.
///   Right collapsed → only the right (expand) arrow is shown.
///   Both expanded   → both arrows shown.
/// </summary>
internal sealed class SplitterBar : Control
{
    public const int BarWidth = 14;

    private const int BtnHalfH = 10;
    private const int BtnGap   = 6;

    private static readonly Color ArrowNormal = Color.FromArgb(70,  70,  70);
    private static readonly Color ArrowHot    = Color.FromArgb(10,  10,  10);
    private static readonly Color ArrowHotBg  = Color.FromArgb(210, 210, 210);

    private readonly ToolTip _tooltip = new ToolTip { AutomaticDelay = 400, ReshowDelay = 200 };

    private bool _dragging;
    private int  _startScreenX;
    private int  _startBarLeft;
    private bool _leftHot;
    private bool _rightHot;

    private Rectangle _leftBtnRect;
    private Rectangle _rightBtnRect;

    /// <summary>When true the left arrow draws as ► (expand) rather than ◄ (collapse).</summary>
    public bool LeftCollapsed  { get; set; }

    /// <summary>When true the right arrow draws as ◄ (expand) rather than ► (collapse).</summary>
    public bool RightCollapsed { get; set; }

    // The left  button is only shown when the right panel is not collapsed.
    // The right button is only shown when the left  panel is not collapsed.
    private bool LeftBtnVisible  => !RightCollapsed;
    private bool RightBtnVisible => !LeftCollapsed;

    public event EventHandler?      CollapseLeftClicked;
    public event EventHandler?      CollapseRightClicked;

    /// <summary>Fired continuously during a drag; argument is the desired new Left of this bar in parent coords.</summary>
    public event EventHandler<int>? DragMoved;

    public SplitterBar()
    {
        Width  = BarWidth;
        Cursor = Cursors.SizeWE;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int cy = Height / 2;
        _leftBtnRect  = new Rectangle(0, cy - BtnGap / 2 - BtnHalfH * 2, Width, BtnHalfH * 2);
        _rightBtnRect = new Rectangle(0, cy + BtnGap / 2,                  Width, BtnHalfH * 2);
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g  = e.Graphics;
        int cx = Width / 2;
        int cy = Height / 2;

        g.Clear(SystemColors.Control);

        // Raised-edge borders
        using (var hi = new Pen(SystemColors.ControlLightLight))
            g.DrawLine(hi, 0, 0, 0, Height - 1);
        using (var sh = new Pen(SystemColors.ControlDark))
            g.DrawLine(sh, Width - 1, 0, Width - 1, Height - 1);

        // Grip dots, skipping the button zone
        int btnZoneTop    = cy - BtnGap / 2 - BtnHalfH * 2 - 10;
        int btnZoneBottom = cy + BtnGap / 2 + BtnHalfH * 2 + 10;

        using (var dot = new SolidBrush(SystemColors.ControlDark))
        {
            for (int y = 6; y < Height - 6; y += 4)
            {
                if (y >= btnZoneTop && y <= btnZoneBottom) continue;
                g.FillRectangle(dot, cx - 1, y, 2, 2);
            }
        }

        int upperCy = cy - BtnGap / 2 - BtnHalfH;
        int lowerCy = cy + BtnGap / 2 + BtnHalfH;

        if (LeftBtnVisible)
            DrawArrow(g, cx, upperCy, LeftCollapsed  ? ArrowDir.Right : ArrowDir.Left,  _leftHot);
        if (RightBtnVisible)
            DrawArrow(g, cx, lowerCy, RightCollapsed ? ArrowDir.Left  : ArrowDir.Right, _rightHot);
    }

    private enum ArrowDir { Left, Right }

    private static void DrawArrow(Graphics g, int cx, int cy, ArrowDir dir, bool hot)
    {
        const int aw = 5;
        const int al = 4;

        if (hot)
        {
            using var bg = new SolidBrush(ArrowHotBg);
            g.FillRectangle(bg, cx - al - 3, cy - aw - 3, (al + 3) * 2, (aw + 3) * 2);
        }

        Point[] pts = dir == ArrowDir.Left
            ? new[] { new Point(cx - al, cy), new Point(cx + al, cy - aw), new Point(cx + al, cy + aw) }
            : new[] { new Point(cx + al, cy), new Point(cx - al, cy - aw), new Point(cx - al, cy + aw) };

        using var brush = new SolidBrush(hot ? ArrowHot : ArrowNormal);
        g.FillPolygon(brush, pts);
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (LeftBtnVisible && _leftBtnRect.Contains(e.Location))
        {
            _tooltip.SetToolTip(this, "");
            CollapseLeftClicked?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (RightBtnVisible && _rightBtnRect.Contains(e.Location))
        {
            _tooltip.SetToolTip(this, "");
            CollapseRightClicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dragging     = true;
        _startScreenX = Control.MousePosition.X;
        _startBarLeft = Left;
        Capture       = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            DragMoved?.Invoke(this, _startBarLeft + Control.MousePosition.X - _startScreenX);
            return;
        }

        bool lh = LeftBtnVisible  && _leftBtnRect.Contains(e.Location);
        bool rh = RightBtnVisible && _rightBtnRect.Contains(e.Location);

        if (lh != _leftHot || rh != _rightHot)
        {
            _leftHot  = lh;
            _rightHot = rh;
            Cursor    = lh || rh ? Cursors.Hand : Cursors.SizeWE;
            Invalidate();

            if (lh)
                _tooltip.SetToolTip(this, LeftCollapsed  ? "Expand left panel"  : "Collapse left panel");
            else if (rh)
                _tooltip.SetToolTip(this, RightCollapsed ? "Expand right panel" : "Collapse right panel");
            else
                _tooltip.SetToolTip(this, "");
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            Capture   = false;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_leftHot || _rightHot)
        {
            _leftHot  = false;
            _rightHot = false;
            Cursor    = Cursors.SizeWE;
            _tooltip.SetToolTip(this, "");
            Invalidate();
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tooltip.Dispose();
        base.Dispose(disposing);
    }
}
