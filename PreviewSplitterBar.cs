using System;
using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// Visible drag handle on the left edge of a preview pane. The standard WinForms
/// Splitter can dock on the preview's outside edge when mixed with Fill controls,
/// so this control reports a horizontal drag delta and lets PanelView own layout.
/// </summary>
internal sealed class PreviewSplitterBar : Control
{
    internal const int BarWidth = 8;

    private readonly ToolTip _toolTip = new()
    {
        AutomaticDelay = 400,
        ReshowDelay = 200,
    };

    private bool _dragging;
    private bool _hot;
    private int _startScreenX;

    internal event EventHandler? DragStarted;
    internal event EventHandler<int>? DragMoved;
    internal event EventHandler? DragCompleted;

    internal PreviewSplitterBar()
    {
        Width = BarWidth;
        Cursor = Cursors.SizeWE;
        AccessibleName = "Resize preview pane";
        AccessibleRole = AccessibleRole.Grip;
        TabStop = false;
        _toolTip.SetToolTip(this, "Drag to resize the preview pane");

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPaint(e);

        bool highlighted = _hot || _dragging;
        e.Graphics.Clear(highlighted
            ? ThemeManager.AccentHover
            : ThemeManager.Surface);

        int centreX = Width / 2;
        int centreY = Height / 2;
        using (var separator = new Pen(highlighted
            ? ThemeManager.Accent
            : ThemeManager.Border))
        {
            e.Graphics.DrawLine(separator, centreX, 0, centreX, Height - 1);
        }

        using var grip = new SolidBrush(highlighted
            ? ThemeManager.Accent
            : ThemeManager.MutedText);
        for (int offset = -6; offset <= 6; offset += 6)
            e.Graphics.FillRectangle(grip, centreX - 1, centreY + offset - 1, 3, 3);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hot = true;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        _dragging = true;
        _startScreenX = MousePosition.X;
        Capture = true;
        DragStarted?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_dragging)
            DragMoved?.Invoke(this, MousePosition.X - _startScreenX);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragging)
            return;

        _dragging = false;
        Capture = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging)
            return;

        _hot = false;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
