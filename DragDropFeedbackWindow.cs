using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// A click-through drag tooltip. Windows' native drop-description text is not
/// available on every supported shell build, so this supplies a dependable
/// visible label while retaining the normal Copy/Move cursor effect.
/// </summary>
internal sealed class DragDropFeedbackWindow : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int HtTransparent = -1;
    private const int WmNcHitTest = 0x0084;
    private string _message = string.Empty;
    private DragDropEffects _effect;

    internal DragDropFeedbackWindow()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Font = SystemFonts.MessageBoxFont;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    internal void ShowFeedback(Point cursorScreenPosition, DragDropEffects effect,
        string destinationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);

        _effect = effect;
        _message = GetMessage(effect, destinationName);

        int iconSize = Scale(18);
        int horizontalPadding = Scale(10);
        int verticalPadding = Scale(7);
        Size textSize = TextRenderer.MeasureText(_message, Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        Size = new Size(
            (horizontalPadding * 2) + iconSize + Scale(7) + textSize.Width,
            (verticalPadding * 2) + Math.Max(iconSize, textSize.Height));
        Location = new Point(
            cursorScreenPosition.X + Scale(18),
            cursorScreenPosition.Y + Scale(20));

        if (!Visible) Show();
        Invalidate();
    }

    internal void HideFeedback()
    {
        if (Visible) Hide();
    }

    internal static string GetMessage(DragDropEffects effect, string destinationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);
        return effect == DragDropEffects.Copy
            ? $"Copy to {destinationName}"
            : $"Move to {destinationName}";
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            // The OLE drag target underneath continues to receive DragOver
            // events even if the pointer is visually above this feedback.
            message.Result = new IntPtr(HtTransparent);
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = new(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using GraphicsPath path = CreateRoundedPath(bounds, Scale(6));
        using var background = new SolidBrush(ThemeManager.Surface);
        using var border = new Pen(ThemeManager.Border);
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);

        int horizontalPadding = Scale(10);
        int iconSize = Scale(18);
        Rectangle iconBounds = new(
            horizontalPadding,
            (ClientSize.Height - iconSize) / 2,
            iconSize,
            iconSize);
        DrawOperationGlyph(e.Graphics, iconBounds, _effect, ThemeManager.AccentText);

        Rectangle textBounds = new(
            iconBounds.Right + Scale(7),
            0,
            ClientSize.Width - iconBounds.Right - Scale(17),
            ClientSize.Height);
        TextRenderer.DrawText(e.Graphics, _message, Font, textBounds, ThemeManager.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private int Scale(int logicalPixels) =>
        Math.Max(1, (logicalPixels * DeviceDpi) / 96);

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawOperationGlyph(Graphics graphics, Rectangle bounds,
        DragDropEffects effect, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.5f, bounds.Width / 10f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        if (effect == DragDropEffects.Copy)
        {
            Rectangle rear = Rectangle.Inflate(bounds, -Scale(bounds, 3), -Scale(bounds, 3));
            rear.Offset(-Scale(bounds, 2), -Scale(bounds, 2));
            Rectangle front = rear;
            front.Offset(Scale(bounds, 4), Scale(bounds, 4));
            graphics.DrawRectangle(pen, rear);
            graphics.DrawRectangle(pen, front);
            return;
        }

        Point center = new(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
        int reach = Scale(bounds, 7);
        int head = Scale(bounds, 3);
        graphics.DrawLine(pen, center.X - reach, center.Y, center.X + reach, center.Y);
        graphics.DrawLine(pen, center.X, center.Y - reach, center.X, center.Y + reach);
        DrawArrowHead(graphics, pen, new Point(center.X + reach, center.Y), new Point(head, 0));
        DrawArrowHead(graphics, pen, new Point(center.X - reach, center.Y), new Point(-head, 0));
        DrawArrowHead(graphics, pen, new Point(center.X, center.Y + reach), new Point(0, head));
        DrawArrowHead(graphics, pen, new Point(center.X, center.Y - reach), new Point(0, -head));
    }

    private static int Scale(Rectangle bounds, int logicalPixels) =>
        Math.Max(1, (logicalPixels * bounds.Width) / 18);

    private static void DrawArrowHead(Graphics graphics, Pen pen, Point tip, Point direction)
    {
        int wing = Math.Max(2, (int)Math.Ceiling(pen.Width * 1.75));
        if (direction.X != 0)
        {
            int sign = Math.Sign(direction.X);
            graphics.DrawLine(pen, tip, new Point(tip.X - (sign * wing), tip.Y - wing));
            graphics.DrawLine(pen, tip, new Point(tip.X - (sign * wing), tip.Y + wing));
            return;
        }

        int verticalSign = Math.Sign(direction.Y);
        graphics.DrawLine(pen, tip, new Point(tip.X - wing, tip.Y - (verticalSign * wing)));
        graphics.DrawLine(pen, tip, new Point(tip.X + wing, tip.Y - (verticalSign * wing)));
    }
}
