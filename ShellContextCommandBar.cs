using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiExplorer;

internal enum ShellContextCommand
{
    Cut,
    Copy,
    Rename,
    Share,
    Delete,
}

/// <summary>
/// Adds the Windows 11-style horizontal command row to a native Shell menu.
/// The glyphs are drawn directly into the menu's final device-pixel surface,
/// so no bitmap scaling is involved on high-DPI displays.
/// </summary>
internal sealed class ShellContextCommandBar
{
    internal const uint CommandId = 0xF001;

    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;
    private const uint OdtMenu = 1;
    private const uint OdsSelected = 0x0001;
    private const uint MiimState = 0x0001;
    private const uint MiimId = 0x0002;
    private const uint MiimFType = 0x0100;
    private const uint MftOwnerDraw = 0x0100;
    private const uint MftSeparator = 0x0800;
    private const uint MfsEnabled = 0;
    private const int LogicalCellWidth = 76;
    private const int LogicalHeight = 62;
    private const int LogicalIconSize = 16;
    private const int LogicalIconAreaTop = 5;
    private const int LogicalIconAreaSize = 20;
    private const int LogicalLabelTop = 28;
    private const int LogicalLabelHeight = 22;

    internal static readonly ShellContextCommand[] Commands =
    [
        ShellContextCommand.Cut,
        ShellContextCommand.Copy,
        ShellContextCommand.Rename,
        ShellContextCommand.Share,
        ShellContextCommand.Delete,
    ];

    private readonly IntPtr _menu;
    private readonly int _dpi;
    private readonly Func<ShellContextCommand, bool> _isEnabled;
    private readonly ShellContextCommand[] _visibleCommands;
    private Rectangle _lastScreenBounds;
    private ShellContextCommand? _lastHoveredCommand;

    internal ShellContextCommandBar(IntPtr ownerWindow, IntPtr menu, int dpi,
        Func<ShellContextCommand, bool>? isEnabled = null,
        Func<ShellContextCommand, bool>? isVisible = null)
    {
        if (ownerWindow == IntPtr.Zero)
            throw new ArgumentException("An owner window is required.", nameof(ownerWindow));
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        _menu = menu;
        _dpi = Math.Max(96, dpi);
        _isEnabled = isEnabled ?? (static _ => true);
        _visibleCommands = Commands
            .Where(command => isVisible?.Invoke(command) != false)
            .ToArray();
    }

    internal IReadOnlyList<ShellContextCommand> VisibleCommands => _visibleCommands;

    internal bool Insert()
    {
        int insertPosition = NativeMethods.GetMenuItemCount(_menu);
        if (insertPosition < 0)
            return false;

        var separator = new MenuItemInfo
        {
            cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
            fMask = MiimFType,
            fType = MftSeparator,
        };
        if (!InsertMenuItem(_menu, (uint)insertPosition, true, ref separator))
            return false;

        var commandItem = new MenuItemInfo
        {
            cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
            fMask = MiimFType | MiimState | MiimId,
            fType = MftOwnerDraw,
            fState = MfsEnabled,
            wID = CommandId,
        };
        if (InsertMenuItem(_menu, (uint)(insertPosition + 1), true, ref commandItem))
            return true;

        NativeMethods.DeleteMenu(_menu, (uint)insertPosition,
            NativeMethods.MF_BYPOSITION);
        return false;
    }

    internal bool TryHandleMessage(ref Message message)
    {
        if (message.Msg == WmMeasureItem && message.LParam != IntPtr.Zero)
        {
            var measure = Marshal.PtrToStructure<MeasureItemStruct>(message.LParam);
            if (measure.CtlType != OdtMenu || measure.itemID != CommandId)
                return false;

            Size size = GetPreferredSize(_dpi, _visibleCommands.Length);
            measure.itemWidth = (uint)size.Width;
            measure.itemHeight = (uint)size.Height;
            Marshal.StructureToPtr(measure, message.LParam, false);
            message.Result = (IntPtr)1;
            return true;
        }

        if (message.Msg != WmDrawItem || message.LParam == IntPtr.Zero)
            return false;

        var draw = Marshal.PtrToStructure<DrawItemStruct>(message.LParam);
        if (draw.CtlType != OdtMenu || draw.itemID != CommandId)
            return false;

        Draw(draw);
        message.Result = (IntPtr)1;
        return true;
    }

    internal bool TryResolveSelectedCommand(int selectedId,
        out ShellContextCommand command)
    {
        command = default;
        if (selectedId != CommandId) return false;

        ShellContextCommand? candidate = null;
        if (TryGetScreenBounds(out Rectangle bounds)
            && GetCursorPos(out NativeMethods.POINT cursor))
        {
            candidate = HitTest(_visibleCommands, bounds,
                new Point(cursor.x, cursor.y));
        }

        candidate ??= _lastHoveredCommand;
        if (candidate is not { } resolved || !_isEnabled(resolved)) return false;

        command = resolved;
        return true;
    }

    internal static Size GetPreferredSize(int dpi)
        => GetPreferredSize(dpi, Commands.Length);

    internal static Size GetPreferredSize(int dpi, int commandCount)
    {
        float scale = Math.Max(96, dpi) / 96f;
        return new Size(
            (int)Math.Ceiling(LogicalCellWidth * Math.Max(1, commandCount) * scale),
            (int)Math.Ceiling(LogicalHeight * scale));
    }

    internal static int GetIconPixelSize(int dpi) =>
        Math.Max(LogicalIconSize,
            (int)Math.Round(LogicalIconSize * Math.Max(96, dpi) / 96f));

    internal static ShellContextCommand? HitTest(Rectangle bounds, Point point)
        => HitTest(Commands, bounds, point);

    internal static ShellContextCommand? HitTest(
        IReadOnlyList<ShellContextCommand> commands,
        Rectangle bounds, Point point)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0
            || !bounds.Contains(point))
            return null;

        int index = Math.Min(commands.Count - 1,
            (point.X - bounds.Left) * commands.Count / bounds.Width);
        return commands[index];
    }

    internal IDisposable StartHoverTracking()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) => RefreshHoveredCommand();
        timer.Start();
        return timer;
    }

    internal static string GetLabel(ShellContextCommand command) => command switch
    {
        ShellContextCommand.Cut => "Cut",
        ShellContextCommand.Copy => "Copy",
        ShellContextCommand.Rename => "Rename",
        ShellContextCommand.Share => "Share",
        ShellContextCommand.Delete => "Delete",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private void Draw(DrawItemStruct draw)
    {
        var bounds = Rectangle.FromLTRB(draw.rcItem.Left, draw.rcItem.Top,
            draw.rcItem.Right, draw.rcItem.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        TryGetScreenBounds(out _);

        using Graphics graphics = Graphics.FromHdc(draw.hDC);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var background = new SolidBrush(ThemeManager.Surface))
            graphics.FillRectangle(background, bounds);

        int hoveredIndex = (draw.itemState & OdsSelected) != 0
            ? GetHoveredIndex()
            : -1;
        if (hoveredIndex >= 0)
            _lastHoveredCommand = _visibleCommands[hoveredIndex];
        float scale = _dpi / 96f;
        int iconSize = GetIconPixelSize(_dpi);
        int iconAreaTop = bounds.Top + Scale(LogicalIconAreaTop, scale);
        int iconAreaSize = Scale(LogicalIconAreaSize, scale);
        int iconTop = iconAreaTop + (iconAreaSize - iconSize) / 2;
        int labelTop = bounds.Top + Scale(LogicalLabelTop, scale);
        int labelHeight = Math.Min(Scale(LogicalLabelHeight, scale),
            Math.Max(1, bounds.Bottom - labelTop));

        for (int index = 0; index < _visibleCommands.Length; index++)
        {
            int left = bounds.Left + bounds.Width * index / _visibleCommands.Length;
            int right = bounds.Left + bounds.Width * (index + 1) / _visibleCommands.Length;
            var cell = Rectangle.FromLTRB(left, bounds.Top, right, bounds.Bottom);

            if (index == hoveredIndex)
            {
                using var hover = new SolidBrush(ThemeManager.Hover);
                graphics.FillRectangle(hover, cell);
            }

            if (index > 0)
            {
                using var separator = new Pen(Color.FromArgb(80, ThemeManager.Border));
                graphics.DrawLine(separator, left, bounds.Top + Scale(8, scale),
                    left, bounds.Bottom - Scale(8, scale));
            }

            ShellContextCommand command = _visibleCommands[index];
            bool enabled = _isEnabled(command);
            Color textColor = enabled ? ThemeManager.Text : ThemeManager.MutedText;
            Color iconColor = !enabled
                ? ThemeManager.MutedText
                : command == ShellContextCommand.Delete
                    ? ThemeManager.MutedText
                    : ThemeManager.AccentText;
            int iconLeft = cell.Left + (cell.Width - iconSize) / 2;
            DrawIcon(graphics, command,
                new Rectangle(iconLeft, iconTop, iconSize, iconSize), iconColor, scale);

            TextRenderer.DrawText(graphics, GetLabel(command), SystemFonts.MenuFont,
                new Rectangle(cell.Left, labelTop, cell.Width, labelHeight), textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    private int GetHoveredIndex()
    {
        if (!TryGetScreenBounds(out Rectangle bounds)
            || !GetCursorPos(out NativeMethods.POINT cursor))
        {
            return -1;
        }

        ShellContextCommand? command = HitTest(_visibleCommands, bounds,
            new Point(cursor.x, cursor.y));
        return command is { } hit ? Array.IndexOf(_visibleCommands, hit) : -1;
    }

    private void RefreshHoveredCommand()
    {
        ShellContextCommand? hoveredCommand = null;
        if (TryGetScreenBounds(out Rectangle bounds)
            && GetCursorPos(out NativeMethods.POINT cursor))
        {
            hoveredCommand = HitTest(_visibleCommands, bounds,
                new Point(cursor.x, cursor.y));
        }

        if (hoveredCommand == _lastHoveredCommand) return;

        _lastHoveredCommand = hoveredCommand;
        RedrawOpenMenuWindow();
    }

    private void RedrawOpenMenuWindow()
    {
        EnumThreadWindows(NativeMethods.GetCurrentThreadId(),
            (window, _) =>
            {
                var className = new StringBuilder(32);
                NativeMethods.GetClassName(window, className, className.Capacity);
                if (!className.ToString().Equals("#32768", StringComparison.Ordinal)
                    || NativeMethods.SendMessageI(window, 0x01E1,
                        IntPtr.Zero, IntPtr.Zero) != _menu)
                    return true;

                NativeMethods.RedrawWindow(window, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW);
                return false;
            }, IntPtr.Zero);
    }

    private bool TryGetScreenBounds(out Rectangle bounds)
    {
        // For popup menus GetMenuItemRect requires a null HWND. Supplying the
        // owner window offsets the returned rectangle and can shift a click
        // into the command cell to its right (for example Copy -> Rename).
        int commandItemPosition = FindCommandItemPosition();
        if (commandItemPosition >= 0
            && GetMenuItemRect(IntPtr.Zero, _menu, (uint)commandItemPosition,
                out NativeMethods.RECT nativeBounds))
        {
            _lastScreenBounds = Rectangle.FromLTRB(nativeBounds.Left, nativeBounds.Top,
                nativeBounds.Right, nativeBounds.Bottom);
        }

        bounds = _lastScreenBounds;
        return bounds.Width > 0 && bounds.Height > 0;
    }

    internal int FindCommandItemPosition()
    {
        int itemCount = NativeMethods.GetMenuItemCount(_menu);
        for (int position = 0; position < itemCount; position++)
        {
            if (GetMenuItemID(_menu, position) == CommandId)
                return position;
        }

        return -1;
    }

    private static void DrawIcon(Graphics graphics, ShellContextCommand command,
        Rectangle bounds, Color color, float scale)
    {
        using var pen = new Pen(color, Math.Max(1.25f, 1.35f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        switch (command)
        {
            case ShellContextCommand.Cut:
                DrawCut(graphics, pen, bounds);
                break;
            case ShellContextCommand.Copy:
                DrawCopy(graphics, pen, bounds);
                break;
            case ShellContextCommand.Rename:
                DrawRename(graphics, pen, bounds);
                break;
            case ShellContextCommand.Share:
                DrawShare(graphics, pen, bounds);
                break;
            case ShellContextCommand.Delete:
                DrawDelete(graphics, pen, bounds);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static void DrawCut(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawEllipse(pen, R(bounds, 1, 13, 5, 5));
        graphics.DrawEllipse(pen, R(bounds, 14, 13, 5, 5));
        graphics.DrawLine(pen, P(bounds, 5, 14), P(bounds, 15, 1));
        graphics.DrawLine(pen, P(bounds, 15, 14), P(bounds, 5, 1));
        graphics.DrawLine(pen, P(bounds, 9, 8), P(bounds, 11, 10));
    }

    private static void DrawCopy(Graphics graphics, Pen pen, Rectangle bounds)
    {
        DrawRoundedRectangle(graphics, pen, R(bounds, 2, 5, 12, 13), N(bounds, 2));
        DrawRoundedRectangle(graphics, pen, R(bounds, 7, 1, 11, 12), N(bounds, 2));
    }

    private static void DrawRename(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 1, 4, 18, 13)));
        graphics.DrawLine(pen, P(bounds, 6, 2), P(bounds, 6, 19));
        graphics.DrawLine(pen, P(bounds, 4, 2), P(bounds, 8, 2));
        graphics.DrawLine(pen, P(bounds, 4, 19), P(bounds, 8, 19));
        graphics.DrawLine(pen, P(bounds, 11, 14), P(bounds, 14, 6));
        graphics.DrawLine(pen, P(bounds, 14, 6), P(bounds, 17, 14));
        graphics.DrawLine(pen, P(bounds, 12, 11), P(bounds, 16, 11));
    }

    private static void DrawShare(Graphics graphics, Pen pen, Rectangle bounds)
    {
        DrawRoundedRectangle(graphics, pen, R(bounds, 2, 8, 14, 10), N(bounds, 2));
        graphics.DrawLine(pen, P(bounds, 10, 2), P(bounds, 18, 2));
        graphics.DrawLine(pen, P(bounds, 18, 2), P(bounds, 18, 10));
        graphics.DrawLine(pen, P(bounds, 18, 2), P(bounds, 9, 11));
    }

    private static void DrawDelete(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawLine(pen, P(bounds, 3, 5), P(bounds, 17, 5));
        graphics.DrawLine(pen, P(bounds, 7, 2), P(bounds, 13, 2));
        graphics.DrawLine(pen, P(bounds, 5, 5), P(bounds, 6, 18));
        graphics.DrawLine(pen, P(bounds, 6, 18), P(bounds, 14, 18));
        graphics.DrawLine(pen, P(bounds, 14, 18), P(bounds, 15, 5));
        graphics.DrawLine(pen, P(bounds, 9, 8), P(bounds, 9, 15));
        graphics.DrawLine(pen, P(bounds, 12, 8), P(bounds, 12, 15));
    }

    private static PointF P(Rectangle bounds, float x, float y) => new(
        bounds.Left + x * bounds.Width / 20f,
        bounds.Top + y * bounds.Height / 20f);

    private static RectangleF R(Rectangle bounds, float x, float y, float width, float height) =>
        new(bounds.Left + x * bounds.Width / 20f,
            bounds.Top + y * bounds.Height / 20f,
            width * bounds.Width / 20f,
            height * bounds.Height / 20f);

    private static float N(Rectangle bounds, float value) => value * bounds.Width / 20f;

    private static void DrawRoundedRectangle(Graphics graphics, Pen pen,
        RectangleF bounds, float radius)
    {
        float diameter = Math.Min(Math.Min(radius * 2, bounds.Width), bounds.Height);
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
            diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }

    private static int Scale(int logicalPixels, float scale) =>
        Math.Max(1, (int)Math.Round(logicalPixels * scale));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MenuItemInfo
    {
        internal uint cbSize;
        internal uint fMask;
        internal uint fType;
        internal uint fState;
        internal uint wID;
        internal IntPtr hSubMenu;
        internal IntPtr hbmpChecked;
        internal IntPtr hbmpUnchecked;
        internal nuint dwItemData;
        internal IntPtr dwTypeData;
        internal uint cch;
        internal IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeasureItemStruct
    {
        internal uint CtlType;
        internal uint CtlID;
        internal uint itemID;
        internal uint itemWidth;
        internal uint itemHeight;
        internal nuint itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawItemStruct
    {
        internal uint CtlType;
        internal uint CtlID;
        internal uint itemID;
        internal uint itemAction;
        internal uint itemState;
        internal IntPtr hwndItem;
        internal IntPtr hDC;
        internal NativeMethods.RECT rcItem;
        internal nuint itemData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "InsertMenuItemW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenuItem(IntPtr menu, uint item, bool byPosition,
        ref MenuItemInfo itemInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item,
        out NativeMethods.RECT bounds);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    private delegate bool EnumThreadWindowCallback(
        IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId,
        EnumThreadWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativeMethods.POINT point);
}
