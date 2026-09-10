using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// A DPI-aware push button whose fill and border are drawn as one anti-aliased
/// shape. Fully owning the paint avoids the uneven default-button frame produced
/// when a native rectangular button is clipped to a rounded region.
/// </summary>
internal sealed class RoundedButton : Button
{
    private const float LogicalCornerRadius = 6f;
    private bool _hovered;
    private bool _pressed;
    private bool _isDefault;

    internal RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
    }

    public override void NotifyDefault(bool value)
    {
        base.NotifyDefault(value);
        if (_isDefault == value) return;
        _isDefault = value;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color outside = Parent?.BackColor ?? ThemeManager.Background;
        e.Graphics.Clear(outside);
        if (ClientSize.Width < 3 || ClientSize.Height < 3) return;

        float scale = DeviceDpi / 96f;
        float borderWidth = (_isDefault ? 2f : 1f) * scale;
        float inset = borderWidth / 2f;
        var bounds = new RectangleF(
            inset,
            inset,
            Math.Max(1f, ClientSize.Width - 1f - borderWidth),
            Math.Max(1f, ClientSize.Height - 1f - borderWidth));
        float radius = LogicalCornerRadius * scale;

        using GraphicsPath path = CreateRoundedPath(bounds, radius);
        SmoothingMode previousMode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill = !Enabled ? ThemeManager.Surface
            : _pressed ? ThemeManager.Pressed
            : _hovered ? ThemeManager.Hover
            : BackColor;
        using (var brush = new SolidBrush(fill))
            e.Graphics.FillPath(brush, path);

        Color border = !Enabled ? ThemeManager.MutedText
            : _isDefault ? ThemeManager.Accent
            : ThemeManager.Border;
        using (var pen = new Pen(border, borderWidth))
            e.Graphics.DrawPath(pen, path);

        e.Graphics.SmoothingMode = previousMode;

        TextFormatFlags flags = TextFormatFlags.HorizontalCenter
                                | TextFormatFlags.VerticalCenter
                                | TextFormatFlags.SingleLine
                                | TextFormatFlags.EndEllipsis;
        if (!ShowKeyboardCues)
            flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            Enabled ? ForeColor : ThemeManager.MutedText, flags);

        if (Focused && ShowFocusCues)
        {
            int focusInset = Math.Max(3, (int)Math.Ceiling(4f * scale));
            var focusBounds = Rectangle.Inflate(ClientRectangle,
                -focusInset, -focusInset);
            if (focusBounds.Width > 0 && focusBounds.Height > 0)
                ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, ForeColor, fill);
        }
    }

    private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
            diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
