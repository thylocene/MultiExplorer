using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiExplorer;

/// <summary>
/// Paints ordinary HMENU rows with MultiExplorer's dark palette. Shell-owned
/// owner-draw rows are deliberately left untouched and continue to receive the
/// IContextMenu2/IContextMenu3 messages expected by their handlers.
/// </summary>
internal sealed class DarkShellMenuRenderer : IDisposable
{
    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;
    private const uint OdtMenu = 1;
    private const uint OdsSelected = 0x0001;
    private const uint MiimState = 0x0001;
    private const uint MiimId = 0x0002;
    private const uint MiimSubMenu = 0x0004;
    private const uint MiimData = 0x0020;
    private const uint MiimFType = 0x0100;
    private const uint MiimBitmap = 0x0080;
    private const uint MftOwnerDraw = 0x0100;
    private const uint MftSeparator = 0x0800;
    private const uint MfsDisabled = 0x0003;
    private const uint MfsChecked = 0x0008;
    private const uint MfsDefault = 0x1000;
    private const uint MimBackground = 0x00000002;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;

    private readonly int _dpi;
    private readonly IntPtr _backgroundBrush;
    private readonly Dictionary<(IntPtr Menu, uint Position), RegisteredMenuItem>
        _itemsByPosition = [];
    private readonly Dictionary<(uint Id, nuint Data), List<RegisteredMenuItem>>
        _itemsByMessage = [];
    private readonly Dictionary<(IntPtr Menu, uint Position), NativeOwnerDrawItem>
        _nativeItemsByPosition = [];
    private readonly Dictionary<(uint Id, nuint Data), List<NativeOwnerDrawItem>>
        _nativeItemsByMessage = [];
    private bool _disposed;

    internal DarkShellMenuRenderer(int dpi)
    {
        _dpi = Math.Max(96, dpi);
        _backgroundBrush = CreateSolidBrush(ToColorRef(ThemeManager.Surface));
        if (_backgroundBrush == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the dark menu brush.");
    }

    internal void Apply(IntPtr menu)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (menu == IntPtr.Zero) return;

        RemoveStaleRegistrations(menu);
        RemoveNativeOwnerDrawRegistrations(menu);

        int count = NativeMethods.GetMenuItemCount(menu);
        var label = new StringBuilder(512);
        var entries = new List<(uint Position, MenuItemInfo Info, string Label)>(count);
        for (int position = 0; position < count; position++)
        {
            var info = new MenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                fMask = MiimFType | MiimState | MiimId | MiimSubMenu
                    | MiimBitmap | MiimData,
            };
            if (!GetMenuItemInfo(menu, (uint)position, true, ref info))
                continue;

            label.Clear();
            NativeMethods.GetMenuString(menu, (uint)position, label,
                label.Capacity, NativeMethods.MF_BYPOSITION);
            string itemLabel = label.ToString();
            entries.Add(((uint)position, info, itemLabel));

            bool rendererOwned = IsRegistered(menu, (uint)position, info);
            if ((info.fType & MftOwnerDraw) != 0
                && !rendererOwned
                && info.wID != ShellContextCommandBar.CommandId)
            {
                RegisterNativeOwnerDraw(new NativeOwnerDrawItem(menu,
                    (uint)position, info.wID, info.dwItemData));
            }
        }

        SetMenuBackground(menu, _backgroundBrush);

        foreach ((uint position, MenuItemInfo info, string itemLabel) in entries)
        {
            // The command bar and Shell extensions that already opted into
            // owner drawing must retain their own data and message handling.
            if ((info.fType & MftOwnerDraw) != 0)
                continue;

            CompactMenuGlyph? glyph = info.wID switch
            {
                PanelView.CopyToOtherPaneMenuCommandId =>
                    CompactMenuGlyph.CopyToOtherPane,
                PanelView.MoveToOtherPaneMenuCommandId =>
                    CompactMenuGlyph.MoveToOtherPane,
                _ => PanelView.TryGetFolderMenuGlyph(itemLabel,
                    out CompactMenuGlyph matchedGlyph)
                    ? matchedGlyph
                    : null,
            };
            var visual = new MenuItemVisual(
                itemLabel,
                (info.fType & MftSeparator) != 0,
                (info.fState & MfsDisabled) == 0,
                (info.fState & MfsChecked) != 0,
                (info.fState & MfsDefault) != 0,
                info.hSubMenu != IntPtr.Zero,
                info.hbmpItem,
                glyph);
            var registration = new RegisteredMenuItem(menu, (uint)position,
                info.wID, info.dwItemData, visual);
            Register(registration);

            var ownerDraw = new MenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                // Only the drawing flag is changed. Command IDs and dwItemData
                // remain exactly as supplied by the Shell extension; dynamic
                // submenu handlers can depend on both values.
                fMask = MiimFType,
                fType = info.fType | MftOwnerDraw,
            };
            if (!SetMenuItemInfo(menu, (uint)position, true, ref ownerDraw))
                Unregister(registration);
        }
    }

    internal bool TryHandleMessage(ref Message message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.Msg == WmMeasureItem && message.LParam != IntPtr.Zero)
        {
            var measure = Marshal.PtrToStructure<MeasureItemStruct>(message.LParam);
            if (measure.CtlType != OdtMenu
                || _nativeItemsByMessage.ContainsKey(
                    (measure.itemID, measure.itemData))
                || !_itemsByMessage.TryGetValue(
                    (measure.itemID, measure.itemData),
                    out List<RegisteredMenuItem>? registrations))
                return false;

            Size size = registrations
                .Select(static registration => registration.Visual)
                .Select(Measure)
                .Aggregate(static (largest, candidate) => new Size(
                    Math.Max(largest.Width, candidate.Width),
                    Math.Max(largest.Height, candidate.Height)));
            measure.itemWidth = unchecked((uint)size.Width);
            measure.itemHeight = unchecked((uint)size.Height);
            Marshal.StructureToPtr(measure, message.LParam, false);
            message.Result = (IntPtr)1;
            return true;
        }

        if (message.Msg != WmDrawItem || message.LParam == IntPtr.Zero)
            return false;

        var draw = Marshal.PtrToStructure<DrawItemStruct>(message.LParam);
        if (draw.CtlType != OdtMenu
            || TryResolveNativeDrawItem(draw, out _)
            || !_itemsByMessage.TryGetValue(
                (draw.itemID, draw.itemData),
                out List<RegisteredMenuItem>? drawRegistrations)
            || !TryResolveDrawRegistration(draw, drawRegistrations,
                out RegisteredMenuItem registration))
            return false;

        Draw(draw, registration.Visual);
        message.Result = (IntPtr)1;
        return true;
    }

    internal Size Measure(MenuItemVisual item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsSeparator)
            return new Size(Scale(220), Scale(9));

        (string text, string shortcut) = SplitLabel(item.Label);
        Size textSize = TextRenderer.MeasureText(text, SystemFonts.MenuFont,
            Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        Size shortcutSize = string.IsNullOrEmpty(shortcut)
            ? Size.Empty
            : TextRenderer.MeasureText(shortcut, SystemFonts.MenuFont,
                Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        int width = Scale(58) + textSize.Width + shortcutSize.Width;
        if (shortcutSize.Width > 0) width += Scale(30);
        if (item.HasSubMenu) width += Scale(16);
        return new Size(Math.Max(Scale(220), width), Scale(32));
    }

    internal static (string Text, string Shortcut) SplitLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        string[] parts = label.Split('\t', 2);
        return (parts[0], parts.Length == 2 ? parts[1] : string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteObject(_backgroundBrush);
        _itemsByPosition.Clear();
        _itemsByMessage.Clear();
        _nativeItemsByPosition.Clear();
        _nativeItemsByMessage.Clear();
    }

    private void Register(RegisteredMenuItem registration)
    {
        if (_itemsByPosition.TryGetValue(
                (registration.Menu, registration.Position),
                out RegisteredMenuItem? previous))
            Unregister(previous);

        _itemsByPosition.Add(
            (registration.Menu, registration.Position), registration);
        var messageKey = (registration.Id, registration.Data);
        if (!_itemsByMessage.TryGetValue(messageKey,
                out List<RegisteredMenuItem>? registrations))
        {
            registrations = [];
            _itemsByMessage.Add(messageKey, registrations);
        }
        registrations.Add(registration);
    }

    private void Unregister(RegisteredMenuItem registration)
    {
        _itemsByPosition.Remove((registration.Menu, registration.Position));
        var messageKey = (registration.Id, registration.Data);
        if (!_itemsByMessage.TryGetValue(messageKey,
                out List<RegisteredMenuItem>? registrations))
            return;

        registrations.Remove(registration);
        if (registrations.Count == 0)
            _itemsByMessage.Remove(messageKey);
    }

    private bool IsRegistered(IntPtr menu, uint position, MenuItemInfo info) =>
        _itemsByPosition.TryGetValue((menu, position),
            out RegisteredMenuItem? registration)
        && registration.Id == info.wID
        && registration.Data == info.dwItemData;

    private void RegisterNativeOwnerDraw(NativeOwnerDrawItem item)
    {
        _nativeItemsByPosition[(item.Menu, item.Position)] = item;
        var messageKey = (item.Id, item.Data);
        if (!_nativeItemsByMessage.TryGetValue(messageKey,
                out List<NativeOwnerDrawItem>? items))
        {
            items = [];
            _nativeItemsByMessage.Add(messageKey, items);
        }

        items.Add(item);
    }

    private void RemoveNativeOwnerDrawRegistrations(IntPtr menu)
    {
        NativeOwnerDrawItem[] items = _nativeItemsByPosition.Values
            .Where(item => item.Menu == menu)
            .ToArray();
        foreach (NativeOwnerDrawItem item in items)
        {
            _nativeItemsByPosition.Remove((item.Menu, item.Position));
            var messageKey = (item.Id, item.Data);
            if (!_nativeItemsByMessage.TryGetValue(messageKey,
                    out List<NativeOwnerDrawItem>? registrations))
                continue;

            registrations.Remove(item);
            if (registrations.Count == 0)
                _nativeItemsByMessage.Remove(messageKey);
        }
    }

    private static void SetMenuBackground(IntPtr menu, IntPtr brush)
    {
        var menuInfo = new MenuInfo
        {
            cbSize = (uint)Marshal.SizeOf<MenuInfo>(),
            fMask = MimBackground,
            hbrBack = brush,
        };
        SetMenuInfo(menu, ref menuInfo);
    }

    private void RemoveStaleRegistrations(IntPtr menu)
    {
        RegisteredMenuItem[] existing = _itemsByPosition.Values
            .Where(registration => registration.Menu == menu)
            .ToArray();
        if (existing.Length == 0) return;

        var label = new StringBuilder(512);
        foreach (RegisteredMenuItem registration in existing)
        {
            var info = new MenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                fMask = MiimFType | MiimId | MiimData,
            };
            label.Clear();
            bool stillRegistered = GetMenuItemInfo(menu,
                    registration.Position, true, ref info)
                && (info.fType & MftOwnerDraw) != 0
                && info.wID == registration.Id
                && info.dwItemData == registration.Data
                && NativeMethods.GetMenuString(menu, registration.Position,
                    label, label.Capacity, NativeMethods.MF_BYPOSITION) >= 0
                && label.ToString().Equals(registration.Visual.Label,
                    StringComparison.Ordinal);
            if (!stillRegistered)
                Unregister(registration);
        }
    }

    internal bool TryGetRegisteredVisual(IntPtr menu, uint position,
        out MenuItemVisual? visual)
    {
        visual = null;
        if (!_itemsByPosition.TryGetValue((menu, position),
                out RegisteredMenuItem? registration))
            return false;

        visual = registration.Visual;
        return true;
    }

    private bool TryResolveRegisteredDrawItem(DrawItemStruct draw,
        out RegisteredMenuItem registration)
    {
        registration = null!;
        return _itemsByMessage.TryGetValue((draw.itemID, draw.itemData),
                out List<RegisteredMenuItem>? registrations)
            && TryResolveDrawRegistration(draw, registrations, out registration);
    }

    private bool TryResolveNativeDrawItem(DrawItemStruct draw,
        out NativeOwnerDrawItem item)
    {
        item = null!;
        if (!_nativeItemsByMessage.TryGetValue((draw.itemID, draw.itemData),
                out List<NativeOwnerDrawItem>? items))
            return false;

        NativeOwnerDrawItem? best = null;
        long bestDistance = long.MaxValue;
        NativeMethods.POINT dcOrigin = default;
        bool hasDcOrigin = GetDCOrgEx(draw.hDC, ref dcOrigin);
        foreach (NativeOwnerDrawItem candidate in items.Where(candidate =>
                     candidate.Menu == draw.hwndItem))
        {
            if (!GetMenuItemRect(IntPtr.Zero, candidate.Menu,
                    candidate.Position, out NativeMethods.RECT itemRect))
                continue;

            long directDistance = RectangleDistance(draw.rcItem, itemRect);
            long translatedDistance = hasDcOrigin
                ? RectangleDistance(draw.rcItem, itemRect, dcOrigin.x, dcOrigin.y)
                : long.MaxValue;
            long distance = Math.Min(directDistance, translatedDistance);
            if (distance >= bestDistance) continue;

            best = candidate;
            bestDistance = distance;
        }

        item = best!;
        return best is not null && bestDistance <= 16;
    }

    internal void PrepareShellOwnerDrawMessage(ref Message message)
    {
        if (message.Msg != WmDrawItem || message.LParam == IntPtr.Zero) return;

        var draw = Marshal.PtrToStructure<DrawItemStruct>(message.LParam);
        if (draw.CtlType != OdtMenu
            || TryResolveRegisteredDrawItem(draw, out _))
            return;

        Color textColor = (draw.itemState & 0x0006) == 0
            ? ThemeManager.Text
            : ThemeManager.MutedText;
        SetTextColor(draw.hDC, ToColorRef(textColor));
        SetBkColor(draw.hDC, ToColorRef(ThemeManager.Surface));
        SetBkMode(draw.hDC, 1); // TRANSPARENT
    }

    internal void FinishShellOwnerDrawMessage(ref Message message)
    {
        if (message.Msg != WmDrawItem || message.LParam == IntPtr.Zero) return;

        var draw = Marshal.PtrToStructure<DrawItemStruct>(message.LParam);
        if (draw.CtlType != OdtMenu
            || TryResolveRegisteredDrawItem(draw, out _)
            || !TryFindMenuItem(draw, out MenuItemInfo info,
                out string label))
            return;

        if (string.IsNullOrWhiteSpace(label))
        {
            CorrectUnlabelledDarkText(draw);
            return;
        }

        Rectangle bounds = Rectangle.FromLTRB(draw.rcItem.Left, draw.rcItem.Top,
            draw.rcItem.Right, draw.rcItem.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        bool selected = (draw.itemState & OdsSelected) != 0;
        bool enabled = (draw.itemState & 0x0006) == 0;
        Color background = selected ? ThemeManager.Hover : ThemeManager.Surface;
        Color textColor = enabled ? ThemeManager.Text : ThemeManager.MutedText;
        bool usesMultiExplorerGutter = _itemsByPosition.Keys.Any(key =>
            key.Menu == draw.hwndItem);
        int textLeft = bounds.Left + Scale(usesMultiExplorerGutter ? 44 : 24);
        int rightReserve = Scale(info.hSubMenu != IntPtr.Zero ? 25 : 10);
        var textBounds = Rectangle.FromLTRB(textLeft, bounds.Top,
            Math.Max(textLeft + 1, bounds.Right - rightReserve), bounds.Bottom);

        using Graphics graphics = Graphics.FromHdc(draw.hDC);
        using (var backgroundBrush = new SolidBrush(background))
            graphics.FillRectangle(backgroundBrush, textBounds);

        (string text, string shortcut) = SplitLabel(label);
        Font menuFont = SystemFonts.MenuFont ?? Control.DefaultFont;
        bool isDefault = (draw.itemState & 0x0020) != 0;
        Font font = isDefault
            ? new Font(menuFont, FontStyle.Bold)
            : menuFont;
        try
        {
            TextRenderer.DrawText(graphics, text, font, textBounds, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                | TextFormatFlags.HidePrefix | TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(shortcut))
            {
                TextRenderer.DrawText(graphics, shortcut, font, textBounds, textColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
        finally
        {
            if (isDefault) font.Dispose();
        }
    }

    private static bool TryFindMenuItem(DrawItemStruct draw,
        out MenuItemInfo resolvedInfo, out string resolvedLabel)
    {
        resolvedInfo = default;
        resolvedLabel = string.Empty;
        int count = NativeMethods.GetMenuItemCount(draw.hwndItem);
        if (count <= 0) return false;

        NativeMethods.POINT dcOrigin = default;
        bool hasDcOrigin = GetDCOrgEx(draw.hDC, ref dcOrigin);
        long bestDistance = long.MaxValue;
        var label = new StringBuilder(512);
        for (int position = 0; position < count; position++)
        {
            var info = new MenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                fMask = MiimFType | MiimState | MiimId | MiimSubMenu
                    | MiimBitmap | MiimData,
            };
            if (!GetMenuItemInfo(draw.hwndItem, (uint)position, true, ref info)
                || info.wID != draw.itemID
                || info.dwItemData != draw.itemData
                || !GetMenuItemRect(IntPtr.Zero, draw.hwndItem,
                    (uint)position, out NativeMethods.RECT itemRect))
                continue;

            long directDistance = RectangleDistance(draw.rcItem, itemRect);
            long translatedDistance = hasDcOrigin
                ? RectangleDistance(draw.rcItem, itemRect,
                    dcOrigin.x, dcOrigin.y)
                : long.MaxValue;
            long distance = Math.Min(directDistance, translatedDistance);
            if (distance >= bestDistance) continue;

            label.Clear();
            NativeMethods.GetMenuString(draw.hwndItem, (uint)position,
                label, label.Capacity, NativeMethods.MF_BYPOSITION);
            bestDistance = distance;
            resolvedInfo = info;
            resolvedLabel = label.ToString();
        }

        return bestDistance <= 16;
    }

    private static bool TryResolveDrawRegistration(DrawItemStruct draw,
        IReadOnlyList<RegisteredMenuItem> registrations,
        out RegisteredMenuItem resolved)
    {
        RegisteredMenuItem[] menuRegistrations = registrations
            .Where(registration => registration.Menu == draw.hwndItem)
            .ToArray();
        if (menuRegistrations.Length == 0)
        {
            resolved = null!;
            return false;
        }

        NativeMethods.POINT dcOrigin = default;
        bool hasDcOrigin = GetDCOrgEx(draw.hDC, ref dcOrigin);
        RegisteredMenuItem? best = null;
        long bestDistance = long.MaxValue;
        foreach (RegisteredMenuItem candidate in menuRegistrations)
        {
            if (!GetMenuItemRect(IntPtr.Zero, candidate.Menu,
                    candidate.Position, out NativeMethods.RECT itemRect))
                continue;

            long directDistance = RectangleDistance(draw.rcItem, itemRect);
            long translatedDistance = hasDcOrigin
                ? RectangleDistance(draw.rcItem, itemRect,
                    dcOrigin.x, dcOrigin.y)
                : long.MaxValue;
            long distance = Math.Min(directDistance, translatedDistance);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = candidate;
        }

        resolved = best!;
        return best is not null && bestDistance <= 16;
    }

    private static long RectangleDistance(NativeMethods.RECT drawn,
        NativeMethods.RECT screen, int offsetX = 0, int offsetY = 0) =>
        Math.Abs((long)drawn.Left + offsetX - screen.Left)
        + Math.Abs((long)drawn.Top + offsetY - screen.Top)
        + Math.Abs((long)drawn.Right + offsetX - screen.Right)
        + Math.Abs((long)drawn.Bottom + offsetY - screen.Bottom);

    private void CorrectUnlabelledDarkText(DrawItemStruct draw)
    {
        Rectangle bounds = Rectangle.FromLTRB(draw.rcItem.Left, draw.rcItem.Top,
            draw.rcItem.Right, draw.rcItem.Bottom);
        int leftInset = Scale(24);
        int rightInset = Scale(18);
        if (bounds.Width <= leftInset + rightInset || bounds.Height <= 0)
            return;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height,
            PixelFormat.Format32bppArgb);
        using (Graphics copy = Graphics.FromImage(bitmap))
        {
            IntPtr destination = copy.GetHdc();
            try
            {
                BitBlt(destination, 0, 0, bounds.Width, bounds.Height,
                    draw.hDC, bounds.Left, bounds.Top, 0x00CC0020);
            }
            finally
            {
                copy.ReleaseHdc(destination);
            }
        }

        Rectangle bitmapBounds = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bitmapBounds, ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            int[] pixels = new int[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(row, pixels, y * bitmap.Width, bitmap.Width);
            }

            int background = FindMostFrequentColor(pixels);
            int backgroundBrightness = AverageColor(background);
            if (backgroundBrightness is < 16 or > 112)
                return;

            bool changed = false;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int rowStart = y * bitmap.Width;
                for (int x = leftInset; x < bitmap.Width - rightInset; x++)
                {
                    int index = rowStart + x;
                    int corrected = CorrectDarkNeutralPixel(pixels[index],
                        backgroundBrightness);
                    if (corrected == pixels[index]) continue;

                    pixels[index] = corrected;
                    changed = true;
                }
            }

            if (!changed) return;
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(pixels, y * bitmap.Width, row, bitmap.Width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using Graphics destinationGraphics = Graphics.FromHdc(draw.hDC);
        destinationGraphics.DrawImageUnscaled(bitmap, bounds.Left, bounds.Top);
    }

    internal static int CorrectDarkNeutralPixel(int argb,
        int backgroundBrightness)
    {
        int blue = argb & 0xFF;
        int green = (argb >> 8) & 0xFF;
        int red = (argb >> 16) & 0xFF;
        int minimum = Math.Min(red, Math.Min(green, blue));
        int maximum = Math.Max(red, Math.Max(green, blue));
        int brightness = (red + green + blue) / 3;
        if (maximum - minimum > 10
            || brightness >= backgroundBrightness - 2)
            return argb;

        double coverage = Math.Clamp(
            (backgroundBrightness - brightness) / (double)backgroundBrightness,
            0d, 1d);
        int corrected = (int)Math.Round(backgroundBrightness
            + coverage * (245 - backgroundBrightness));
        return (argb & unchecked((int)0xFF000000))
            | corrected << 16 | corrected << 8 | corrected;
    }

    private static int FindMostFrequentColor(IEnumerable<int> pixels)
    {
        var counts = new Dictionary<int, int>();
        int mostFrequent = 0;
        int largestCount = 0;
        foreach (int pixel in pixels)
        {
            int color = pixel & 0x00FFFFFF;
            int count = counts.GetValueOrDefault(color) + 1;
            counts[color] = count;
            if (count <= largestCount) continue;

            mostFrequent = color;
            largestCount = count;
        }

        return mostFrequent;
    }

    private static int AverageColor(int rgb)
    {
        int sum = ((rgb >> 16) & 0xFF) + ((rgb >> 8) & 0xFF) + (rgb & 0xFF);
        return sum / 3;
    }

    private void Draw(DrawItemStruct draw, MenuItemVisual item)
    {
        Rectangle bounds = Rectangle.FromLTRB(draw.rcItem.Left, draw.rcItem.Top,
            draw.rcItem.Right, draw.rcItem.Bottom);
        DrawRow(draw.hDC, bounds, draw.itemState, item);
    }

    internal void DrawRow(IntPtr destinationDc, Rectangle bounds, uint itemState,
        MenuItemVisual item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Color? nativeSubmenuArrowColor = null;
        int iconSize = Scale(20);
        int iconLeft = bounds.Left + Scale(10);
        int iconTop = bounds.Top + (bounds.Height - iconSize) / 2;
        // GDI bitmap handles can be sign-extended on 64-bit Windows. A numeric
        // greater-than-zero check discards valid Shell icons unpredictably.
        bool hasBitmap = item.Bitmap != IntPtr.Zero
            && GetObjectBitmap(item.Bitmap, Marshal.SizeOf<NativeBitmap>(),
                out _) > 0;
        using (Graphics graphics = Graphics.FromHdc(destinationDc))
        {
            Color background = (itemState & OdsSelected) != 0
                ? ThemeManager.Hover
                : ThemeManager.Surface;
            using (var brush = new SolidBrush(background))
                graphics.FillRectangle(brush, bounds);

            if (item.IsSeparator)
            {
                int y = bounds.Top + bounds.Height / 2;
                using var separator = new Pen(ThemeManager.Border);
                graphics.DrawLine(separator, bounds.Left + Scale(10), y,
                    bounds.Right - Scale(10), y);
                return;
            }

            if (item.Glyph is { } glyph)
                NativeMenuIconSet.DrawGlyph(graphics,
                    new RectangleF(iconLeft, iconTop, iconSize, iconSize), glyph);
            else if (!hasBitmap && item.IsChecked)
                DrawCheck(graphics, new Rectangle(iconLeft, iconTop, iconSize, iconSize));

            Color textColor = item.IsEnabled
                ? ThemeManager.Text
                : ThemeManager.MutedText;
            (string text, string shortcut) = SplitLabel(item.Label);
            int textLeft = bounds.Left + Scale(44);
            int rightReserve = Scale(item.HasSubMenu ? 25 : 10);
            Rectangle textBounds = Rectangle.FromLTRB(textLeft, bounds.Top,
                bounds.Right - rightReserve, bounds.Bottom);
            Font menuFont = SystemFonts.MenuFont ?? Control.DefaultFont;
            Font font = item.IsDefault
                ? new Font(menuFont, FontStyle.Bold)
                : menuFont;
            try
            {
                TextRenderer.DrawText(graphics, text, font, textBounds, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.HidePrefix | TextFormatFlags.NoPadding);

                if (!string.IsNullOrEmpty(shortcut))
                {
                    TextRenderer.DrawText(graphics, shortcut, font, textBounds, textColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                }
            }
            finally
            {
                if (item.IsDefault) font.Dispose();
            }

            if (item.HasSubMenu)
                nativeSubmenuArrowColor = textColor;
        }

        // Complete all GDI+ drawing before AlphaBlend touches the same DC.
        // A later GDI+ flush can otherwise cover the bitmap intermittently.
        if (hasBitmap && item.Glyph is null)
            DrawBitmap(destinationDc, item.Bitmap, iconLeft, iconTop);

        // Windows draws the cascade indicator after WM_DRAWITEM returns. Do not
        // paint a second arrow; leave the menu DC using the row's foreground so
        // the single native indicator remains visible on the dark surface.
        if (nativeSubmenuArrowColor is { } arrowColor)
        {
            SetTextColor(destinationDc, ToColorRef(arrowColor));
            SetBkColor(destinationDc, ToColorRef(ThemeManager.Surface));
            SetBkMode(destinationDc, 1); // TRANSPARENT
        }
    }

    private void DrawBitmap(IntPtr destinationDc, IntPtr bitmap,
        int left, int top)
    {
        if (GetObjectBitmap(bitmap, Marshal.SizeOf<NativeBitmap>(),
                out NativeBitmap details) <= 0
            || details.Width <= 0
            || details.Height == 0)
            return;

        IntPtr sourceDc = CreateCompatibleDC(destinationDc);
        if (sourceDc == IntPtr.Zero) return;
        int width = details.Width;
        int height = Math.Abs(details.Height);
        bool hasAlpha = details.BitsPixel == 32
            && HasNonZeroAlpha(destinationDc, bitmap, width, height);
        IntPtr previous = SelectObject(sourceDc, bitmap);
        try
        {
            if (hasAlpha)
            {
                var blend = new BlendFunction
                {
                    BlendOp = AcSrcOver,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AcSrcAlpha,
                };
                AlphaBlend(destinationDc, left, top, width, height,
                    sourceDc, 0, 0, width, height, blend);
            }
            else
            {
                // Some Shell extensions provide 32-bit menu bitmaps with every
                // alpha byte set to zero. Per-pixel AlphaBlend makes those icons
                // disappear on our owner-drawn dark menu. Use the uniform corner
                // color as their transparency key when one is present.
                uint key = GetPixel(sourceDc, 0, 0);
                bool uniformCorners = key != uint.MaxValue
                    && GetPixel(sourceDc, width - 1, 0) == key
                    && GetPixel(sourceDc, 0, height - 1) == key
                    && GetPixel(sourceDc, width - 1, height - 1) == key;
                if (!uniformCorners
                    || !TransparentBlt(destinationDc, left, top, width, height,
                        sourceDc, 0, 0, width, height, key))
                    BitBlt(destinationDc, left, top, width, height,
                        sourceDc, 0, 0, 0x00CC0020);
            }
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(sourceDc, previous);
            DeleteDC(sourceDc);
        }
    }

    private static bool HasNonZeroAlpha(IntPtr deviceContext, IntPtr bitmap,
        int width, int height)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            },
        };
        byte[] pixels = new byte[checked(width * height * 4)];
        if (GetDIBits(deviceContext, bitmap, 0, (uint)height,
                pixels, ref info, 0) == 0)
            return false;

        for (int index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 0)
                return true;
        return false;
    }

    private static void DrawCheck(Graphics graphics, Rectangle bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(ThemeManager.AccentText, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLines(pen, new Point[]
        {
            new Point(bounds.Left + bounds.Width / 5, bounds.Top + bounds.Height / 2),
            new Point(bounds.Left + bounds.Width * 2 / 5, bounds.Bottom - bounds.Height / 4),
            new Point(bounds.Right - bounds.Width / 6, bounds.Top + bounds.Height / 4),
        });
    }

    private int Scale(int logicalPixels) =>
        Math.Max(1, (int)Math.Round(logicalPixels * _dpi / 96f));

    private static uint ToColorRef(Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    internal sealed record MenuItemVisual(
        string Label,
        bool IsSeparator,
        bool IsEnabled,
        bool IsChecked,
        bool IsDefault,
        bool HasSubMenu,
        IntPtr Bitmap,
        CompactMenuGlyph? Glyph = null);

    private sealed record RegisteredMenuItem(
        IntPtr Menu,
        uint Position,
        uint Id,
        nuint Data,
        MenuItemVisual Visual);

    private sealed record NativeOwnerDrawItem(
        IntPtr Menu,
        uint Position,
        uint Id,
        nuint Data);

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
    private struct MenuInfo
    {
        internal uint cbSize;
        internal uint fMask;
        internal uint dwStyle;
        internal uint cyMax;
        internal IntPtr hbrBack;
        internal uint dwContextHelpID;
        internal nuint dwMenuData;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        internal int Type;
        internal int Width;
        internal int Height;
        internal int WidthBytes;
        internal ushort Planes;
        internal ushort BitsPixel;
        internal IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        internal byte BlendOp;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemInfo(IntPtr menu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo itemInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuItemInfo(IntPtr menu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo itemInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuInfo(IntPtr menu, ref MenuInfo menuInfo);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr deviceContext, uint color);

    [DllImport("gdi32.dll")]
    private static extern uint SetBkColor(IntPtr deviceContext, uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr deviceContext, int backgroundMode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destinationDc,
        int destinationX, int destinationY, int width, int height,
        IntPtr sourceDc, int sourceX, int sourceY, uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr deviceContext, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap,
        uint startScan, uint scanLines, [Out] byte[] bits,
        ref BitmapInfo bitmapInfo, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDCOrgEx(IntPtr deviceContext,
        ref NativeMethods.POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu,
        uint item, out NativeMethods.RECT rectangle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectBitmap(IntPtr bitmap, int bufferSize,
        out NativeBitmap bitmapData);

    [DllImport("msimg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AlphaBlend(IntPtr destinationDc,
        int destinationX, int destinationY, int destinationWidth, int destinationHeight,
        IntPtr sourceDc, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        BlendFunction blendFunction);

    [DllImport("msimg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TransparentBlt(IntPtr destinationDc,
        int destinationX, int destinationY, int destinationWidth, int destinationHeight,
        IntPtr sourceDc, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        uint transparentColor);
}
