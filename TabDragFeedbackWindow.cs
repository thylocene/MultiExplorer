using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

internal enum TabDragFeedbackState
{
    Hidden,
    DuplicateInCurrentPane,
    MoveToOtherPane,
    CopyToOtherPane,
    OpenInFileExplorer,
}

/// <summary>A click-through preview of the tab and the action at the cursor.</summary>
internal sealed class TabDragFeedbackWindow : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private readonly string _label;
    private readonly string _path;
    private readonly Font _titleFont;
    private TabDragFeedbackState _state;

    internal TabDragFeedbackWindow(string label, string path)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(path);

        _label = label;
        _path = path;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Font = SystemFonts.MessageBoxFont;
        _titleFont = new Font(Font, FontStyle.Bold);
        SetStyle(ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTransparent;
            return parameters;
        }
    }

    internal void ShowFeedback(Point cursorPosition, TabDragFeedbackState state)
    {
        if (state == TabDragFeedbackState.Hidden)
        {
            if (Visible) Hide();
            return;
        }

        Screen screen = Screen.FromPoint(cursorPosition);
        Rectangle area = screen.WorkingArea;
        Size = new Size(
            Math.Min(Scale(350), Math.Max(1, area.Width - Scale(16))),
            Math.Min(Scale(80), Math.Max(1, area.Height - Scale(16))));

        int offset = Scale(24);
        int x = cursorPosition.X + offset;
        int y = cursorPosition.Y + offset;
        if (x + Width > area.Right) x = cursorPosition.X - Width - offset;
        if (y + Height > area.Bottom) y = cursorPosition.Y - Height - offset;
        Location = new Point(
            Math.Clamp(x, area.Left, area.Right - Width),
            Math.Clamp(y, area.Top, area.Bottom - Height));

        if (_state != state)
        {
            _state = state;
            Invalidate();
        }
        if (!Visible) Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _titleFont.Dispose();
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = new IntPtr(HtTransparent);
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Color actionColor = ThemeManager.Accent;

        e.Graphics.Clear(ThemeManager.Surface);
        using (var border = new Pen(ThemeManager.Border))
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var accent = new SolidBrush(actionColor))
            e.Graphics.FillRectangle(accent, 0, 0, Scale(4), Height);

        int iconX = Scale(15);
        int iconY = Scale(17);
        if (_state is TabDragFeedbackState.DuplicateInCurrentPane
            or TabDragFeedbackState.CopyToOtherPane)
        {
            using var duplicate = new Pen(actionColor, Math.Max(1, Scale(2)));
            e.Graphics.DrawRectangle(duplicate,
                iconX, iconY, Scale(20), Scale(17));
            e.Graphics.DrawRectangle(duplicate,
                iconX + Scale(5), iconY + Scale(6), Scale(20), Scale(17));
        }
        else if (_state == TabDragFeedbackState.MoveToOtherPane)
        {
            using var move = new Pen(actionColor, Math.Max(1, Scale(2)));
            e.Graphics.DrawRectangle(move,
                iconX, iconY + Scale(2), Scale(12), Scale(18));
            e.Graphics.DrawRectangle(move,
                iconX + Scale(19), iconY + Scale(2), Scale(12), Scale(18));
            e.Graphics.DrawLine(move,
                iconX + Scale(9), iconY + Scale(11),
                iconX + Scale(21), iconY + Scale(11));
            Point[] arrow =
            [
                new Point(iconX + Scale(17), iconY + Scale(7)),
                new Point(iconX + Scale(21), iconY + Scale(11)),
                new Point(iconX + Scale(17), iconY + Scale(15)),
            ];
            e.Graphics.DrawLines(move, arrow);
        }
        else
        {
            using var folder = new SolidBrush(actionColor);
            e.Graphics.FillRectangle(folder, iconX, iconY, Scale(15), Scale(5));
            e.Graphics.FillRectangle(folder,
                iconX, iconY + Scale(4), Scale(26), Scale(19));
        }

        int textX = Scale(51);
        int textWidth = Math.Max(1, Width - textX - Scale(12));
        const TextFormatFlags textFlags = TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, _label, _titleFont,
            new Rectangle(textX, Scale(7), textWidth, Scale(20)),
            ThemeManager.Text, textFlags);
        TextRenderer.DrawText(e.Graphics, _path, Font,
            new Rectangle(textX, Scale(27), textWidth, Scale(18)),
            ThemeManager.MutedText, textFlags);
        TextRenderer.DrawText(e.Graphics, GetActionText(_state), Font,
            new Rectangle(textX, Scale(50), textWidth, Scale(21)),
            actionColor, textFlags);
    }

    internal static string GetActionText(TabDragFeedbackState state) => state switch
    {
        TabDragFeedbackState.DuplicateInCurrentPane => "Duplicate in current pane",
        TabDragFeedbackState.MoveToOtherPane => "Move tab to other pane",
        TabDragFeedbackState.CopyToOtherPane => "Copy tab to other pane",
        TabDragFeedbackState.OpenInFileExplorer => "Open in File Explorer",
        _ => string.Empty,
    };

    private int Scale(int logicalPixels) =>
        Math.Max(1, (logicalPixels * DeviceDpi) / 96);
}
