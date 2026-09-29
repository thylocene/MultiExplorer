using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MultiExplorer;

internal enum CompactMenuGlyph
{
    Open,
    DesktopBackground,
    RotateRight,
    RotateLeft,
    GiveAccess,
    RestorePrevious,
    IncludeLibrary,
    PinStart,
    CopyPath,
    CreateShortcut,
    Properties,
    ShowMore,
    CopyToOtherPane,
    MoveToOtherPane,
    Favorite,
}

/// <summary>
/// Owns alpha-enabled native menu bitmaps. Each glyph is vector-rendered at the
/// target monitor's DPI instead of scaling a fixed-size raster resource.
/// </summary>
internal sealed class NativeMenuIconSet(int dpi) : IDisposable
{
    private const int LogicalIconSize = 20;
    private const int LogicalTextGap = 12;
    private const int GlyphRenderScale = 4;
    private const uint MiimBitmap = 0x0080;
    private const uint DibRgbColors = 0;
    private const uint SiigbfIconOnly = 0x00000004;
    private const uint SiigbfScaleUp = 0x00000100;
    private readonly int _dpi = Math.Max(96, dpi);
    private readonly List<IntPtr> _bitmapHandles = [];
    private bool _disposed;

    internal static int GetIconPixelSize(int dpi) =>
        Math.Max(LogicalIconSize,
            (int)Math.Round(LogicalIconSize * Math.Max(96, dpi) / 96f));

    internal static int GetBitmapPixelWidth(int dpi) =>
        Math.Max(LogicalIconSize + LogicalTextGap,
            (int)Math.Round((LogicalIconSize + LogicalTextGap)
                            * Math.Max(96, dpi) / 96f));

    internal bool Apply(IntPtr menu, uint commandId, CompactMenuGlyph glyph)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        return ApplyBitmap(menu, commandId, byPosition: false,
            CreateGlyphBitmap(glyph));
    }

    internal bool ApplyAtPosition(IntPtr menu, uint position,
        CompactMenuGlyph glyph)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        return ApplyBitmap(menu, position, byPosition: true,
            CreateGlyphBitmap(glyph));
    }

    internal bool ApplyFileTypeIcon(IntPtr menu, uint commandId, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IntPtr bitmap = CreateFileTypeBitmap(path);
        return ApplyBitmap(menu, commandId, byPosition: false, bitmap);
    }

    private bool ApplyBitmap(IntPtr menu, uint item, bool byPosition,
        IntPtr bitmap)
    {
        var info = new MenuItemInfo
        {
            cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
            fMask = MiimBitmap,
            hbmpItem = bitmap,
        };
        if (!SetMenuItemInfo(menu, item, byPosition, ref info))
        {
            DeleteObject(bitmap);
            return false;
        }

        _bitmapHandles.Add(bitmap);
        return true;
    }

    private IntPtr CreateFileTypeBitmap(string path)
    {
        IntPtr shellBitmap = GetShellFileTypeBitmap(path);
        if (shellBitmap == IntPtr.Zero)
            return CreateGlyphBitmap(CompactMenuGlyph.Open);

        try
        {
            int iconSize = GetIconPixelSize(_dpi);
            if (GetObjectBitmap(shellBitmap, Marshal.SizeOf<NativeBitmap>(),
                    out NativeBitmap source) <= 0
                || source.Width <= 0 || source.Height == 0)
                return CreateGlyphBitmap(CompactMenuGlyph.Open);

            int sourceWidth = source.Width;
            int sourceHeight = Math.Abs(source.Height);
            int sourceStride = sourceWidth * 4;
            var sourcePixels = new byte[sourceStride * sourceHeight];
            var sourceInfo = CreateBitmapInfo(sourceWidth, sourceHeight);
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                if (screenDc == IntPtr.Zero
                    || GetDIBits(screenDc, shellBitmap, 0, (uint)sourceHeight,
                        sourcePixels, ref sourceInfo, DibRgbColors) == 0)
                    return CreateGlyphBitmap(CompactMenuGlyph.Open);
            }
            finally
            {
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }

            int bitmapWidth = GetBitmapPixelWidth(_dpi);
            IntPtr target = CreateBitmapCanvas(bitmapWidth, iconSize,
                out IntPtr targetBits);
            int copyWidth = Math.Min(iconSize, sourceWidth);
            int copyHeight = Math.Min(iconSize, sourceHeight);
            int sourceX = Math.Max(0, (sourceWidth - copyWidth) / 2);
            int sourceY = Math.Max(0, (sourceHeight - copyHeight) / 2);
            int targetX = Math.Max(0, (iconSize - copyWidth) / 2);
            int targetY = Math.Max(0, (iconSize - copyHeight) / 2);
            int targetStride = bitmapWidth * 4;
            int rowBytes = copyWidth * 4;
            var row = new byte[rowBytes];
            for (int y = 0; y < copyHeight; y++)
            {
                Buffer.BlockCopy(sourcePixels,
                    ((sourceY + y) * sourceStride) + (sourceX * 4),
                    row, 0, rowBytes);
                Marshal.Copy(row, 0,
                    IntPtr.Add(targetBits,
                        ((targetY + y) * targetStride) + (targetX * 4)),
                    rowBytes);
            }

            return target;
        }
        finally
        {
            DeleteObject(shellBitmap);
        }
    }

    private IntPtr GetShellFileTypeBitmap(string path)
    {
        Guid imageFactoryId = typeof(IShellItemImageFactory).GUID;
        int result = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero,
            ref imageFactoryId, out IntPtr factoryPointer);
        if (result < 0 || factoryPointer == IntPtr.Zero) return IntPtr.Zero;

        IShellItemImageFactory? factory = null;
        try
        {
            factory = (IShellItemImageFactory)
                Marshal.GetObjectForIUnknown(factoryPointer);
            int iconSize = GetIconPixelSize(_dpi);
            var requestedSize = new ShellSize { Width = iconSize, Height = iconSize };
            result = factory.GetImage(requestedSize,
                SiigbfIconOnly | SiigbfScaleUp, out IntPtr bitmap);
            if (result >= 0) return bitmap;

            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            return IntPtr.Zero;
        }
        finally
        {
            if (factory is not null) Marshal.ReleaseComObject(factory);
            Marshal.Release(factoryPointer);
        }
    }

    private IntPtr CreateGlyphBitmap(CompactMenuGlyph glyph)
    {
        int iconSize = GetIconPixelSize(_dpi);
        int bitmapWidth = GetBitmapPixelWidth(_dpi);
        IntPtr bitmapHandle = CreateBitmapCanvas(bitmapWidth, iconSize,
            out IntPtr bits);

        try
        {
            using var bitmap = new Bitmap(bitmapWidth, iconSize, bitmapWidth * 4,
                PixelFormat.Format32bppPArgb, bits);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            DrawGlyph(graphics, new RectangleF(0, 0, iconSize, iconSize), glyph);
            return bitmapHandle;
        }
        catch
        {
            DeleteObject(bitmapHandle);
            throw;
        }
    }

    private static IntPtr CreateBitmapCanvas(int width, int height,
        out IntPtr bits)
    {
        var bitmapInfo = CreateBitmapInfo(width, height);
        IntPtr bitmapHandle = CreateDIBSection(IntPtr.Zero, ref bitmapInfo,
            DibRgbColors, out bits, IntPtr.Zero, 0);
        if (bitmapHandle == IntPtr.Zero || bits == IntPtr.Zero)
            throw new InvalidOperationException("Could not create a native menu icon bitmap.");
        return bitmapHandle;
    }

    private static BitmapInfo CreateBitmapInfo(int width, int height) => new()
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

    internal static void DrawGlyph(Graphics graphics, RectangleF bounds,
        CompactMenuGlyph glyph)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        // Render above the destination resolution, then downsample. GDI+'s
        // single-pass anti-aliasing leaves visible steps on 20-pixel stars and
        // diagonal strokes. This also covers dark menus drawn straight to an
        // HDC, whose Graphics starts with smoothing disabled.
        int renderWidth = Math.Max(1,
            (int)Math.Ceiling(bounds.Width * GlyphRenderScale));
        int renderHeight = Math.Max(1,
            (int)Math.Ceiling(bounds.Height * GlyphRenderScale));
        using var source = new Bitmap(renderWidth, renderHeight,
            PixelFormat.Format32bppPArgb);
        using (Graphics sourceGraphics = Graphics.FromImage(source))
        {
            sourceGraphics.Clear(Color.Transparent);
            sourceGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            sourceGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawGlyphCore(sourceGraphics,
                new RectangleF(0, 0, renderWidth, renderHeight), glyph);
        }

        GraphicsState state = graphics.Save();
        try
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, bounds,
                new RectangleF(0, 0, renderWidth, renderHeight),
                GraphicsUnit.Pixel);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawGlyphCore(Graphics graphics, RectangleF bounds,
        CompactMenuGlyph glyph)
    {
        float scale = bounds.Width / 20f;
        // Use Explorer's blue accent for the Favorites outline; other document
        // and tool outlines use neutral ink in both themes.
        Color color = glyph == CompactMenuGlyph.Favorite
            ? ThemeManager.AccentText
            : ThemeManager.Text;
        // One logical pixel at 100% DPI. Scale that thin outline with the
        // monitor so it remains readable without becoming heavy.
        using var pen = new Pen(color, scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        switch (glyph)
        {
            case CompactMenuGlyph.Open:
                DrawOpen(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.DesktopBackground:
                DrawDesktopBackground(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.RotateRight:
                DrawRotate(graphics, pen, bounds, clockwise: true);
                break;
            case CompactMenuGlyph.RotateLeft:
                DrawRotate(graphics, pen, bounds, clockwise: false);
                break;
            case CompactMenuGlyph.GiveAccess:
                DrawGiveAccess(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.RestorePrevious:
                DrawRestorePrevious(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.IncludeLibrary:
                DrawIncludeLibrary(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.PinStart:
                DrawPinStart(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.Favorite:
                DrawFavorite(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.CopyPath:
                DrawCopyPath(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.CopyToOtherPane:
                DrawOppositePaneTransfer(graphics, pen, bounds, copy: true);
                break;
            case CompactMenuGlyph.MoveToOtherPane:
                DrawOppositePaneTransfer(graphics, pen, bounds, copy: false);
                break;
            case CompactMenuGlyph.CreateShortcut:
                DrawCreateShortcut(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.Properties:
                DrawProperties(graphics, pen, bounds);
                break;
            case CompactMenuGlyph.ShowMore:
                DrawShowMore(graphics, pen, bounds);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(glyph), glyph, null);
        }
    }

    private static void DrawOpen(Graphics graphics, Pen pen, RectangleF bounds)
    {
        using var path = new GraphicsPath();
        path.AddLines([
            P(bounds, 2, 6), P(bounds, 7, 6), P(bounds, 9, 8),
            P(bounds, 18, 8), P(bounds, 16, 17), P(bounds, 2, 17),
        ]);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
        graphics.DrawLine(pen, P(bounds, 2, 6), P(bounds, 2, 4));
        graphics.DrawLine(pen, P(bounds, 2, 4), P(bounds, 8, 4));
        graphics.DrawLine(pen, P(bounds, 8, 4), P(bounds, 10, 6));
    }

    private static void DrawDesktopBackground(Graphics graphics, Pen pen,
        RectangleF bounds)
    {
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 2, 3, 16, 13)));
        graphics.DrawEllipse(pen, R(bounds, 12.5f, 5, 2.5f, 2.5f));
        graphics.DrawLines(pen, [
            P(bounds, 4, 14), P(bounds, 8, 9), P(bounds, 11, 12),
            P(bounds, 13, 10), P(bounds, 17, 14),
        ]);
        graphics.DrawLine(pen, P(bounds, 7, 18), P(bounds, 13, 18));
    }

    private static void DrawRotate(Graphics graphics, Pen pen, RectangleF bounds,
        bool clockwise)
    {
        RectangleF arc = R(bounds, 3, 3, 14, 14);
        if (clockwise)
        {
            graphics.DrawArc(pen, arc, 210, 255);
            graphics.DrawLines(pen, [
                P(bounds, 15, 3), P(bounds, 18, 6), P(bounds, 14, 7),
            ]);
        }
        else
        {
            graphics.DrawArc(pen, arc, 75, 255);
            graphics.DrawLines(pen, [
                P(bounds, 5, 3), P(bounds, 2, 6), P(bounds, 6, 7),
            ]);
        }
    }

    private static void DrawCopyPath(Graphics graphics, Pen pen, RectangleF bounds)
    {
        // Explorer represents a path as text inside a thin address field.
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 1.5f, 3, 17, 14)));
        using var accentPen = new Pen(ThemeManager.AccentText, pen.Width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(accentPen, P(bounds, 4, 6.5f),
            P(bounds, 6.5f, 13.5f));
        using var dot = new SolidBrush(ThemeManager.AccentText);
        foreach (float x in new[] { 9f, 12f, 15f })
            graphics.FillEllipse(dot, R(bounds, x, 11.7f, 0.9f, 0.9f));
    }

    private static void DrawOppositePaneTransfer(Graphics graphics, Pen pen,
        RectangleF bounds, bool copy)
    {
        // The document outlines distinguish copy from move, while the arrow
        // points toward the opposite pane. All coordinates scale from a 20-unit
        // vector canvas to the menu's current monitor DPI.
        if (copy)
        {
            graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 2, 4, 8, 11)));
            graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 5, 2, 8, 11)));
            graphics.DrawLine(pen, P(bounds, 11, 16), P(bounds, 18, 16));
            graphics.DrawLines(pen,
                [P(bounds, 15, 13), P(bounds, 18, 16), P(bounds, 15, 19)]);
        }
        else
        {
            graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 2, 3, 9, 14)));
            using var accentPen = new Pen(ThemeManager.AccentText, pen.Width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            graphics.DrawLine(accentPen, P(bounds, 7, 10), P(bounds, 18, 10));
            graphics.DrawLines(accentPen,
                [P(bounds, 14, 6), P(bounds, 18, 10), P(bounds, 14, 14)]);
        }
    }

    private static void DrawGiveAccess(Graphics graphics, Pen pen, RectangleF bounds)
    {
        graphics.DrawEllipse(pen, R(bounds, 3, 3, 5, 5));
        graphics.DrawArc(pen, R(bounds, 1, 9, 9, 8), 190, 160);
        graphics.DrawEllipse(pen, R(bounds, 12, 5, 4, 4));
        graphics.DrawArc(pen, R(bounds, 10, 10, 8, 6), 195, 150);
        graphics.DrawLine(pen, P(bounds, 8, 7), P(bounds, 14, 13));
        graphics.DrawLines(pen,
            [P(bounds, 11, 13), P(bounds, 14, 13), P(bounds, 14, 10)]);
    }

    private static void DrawRestorePrevious(Graphics graphics, Pen pen,
        RectangleF bounds)
    {
        graphics.DrawArc(pen, R(bounds, 3, 3, 14, 14), 35, 300);
        graphics.DrawLines(pen,
            [P(bounds, 3, 4), P(bounds, 3, 9), P(bounds, 8, 8)]);
        graphics.DrawLine(pen, P(bounds, 10, 6), P(bounds, 10, 11));
        graphics.DrawLine(pen, P(bounds, 10, 11), P(bounds, 14, 13));
    }

    private static void DrawIncludeLibrary(Graphics graphics, Pen pen,
        RectangleF bounds)
    {
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 2, 4, 4, 13)));
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 7, 2, 4, 15)));
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 12, 5, 5, 12)));
        graphics.DrawLine(pen, P(bounds, 3, 7), P(bounds, 5, 7));
        graphics.DrawLine(pen, P(bounds, 8, 5), P(bounds, 10, 5));
        graphics.DrawLine(pen, P(bounds, 13, 8), P(bounds, 16, 8));
    }

    private static void DrawPinStart(Graphics graphics, Pen pen, RectangleF bounds)
    {
        using var pinHead = new GraphicsPath();
        pinHead.AddLines([
            P(bounds, 6, 3),
            P(bounds, 14, 3),
            P(bounds, 13, 8),
            P(bounds, 15, 11),
            P(bounds, 5, 11),
            P(bounds, 7, 8),
        ]);
        pinHead.CloseFigure();

        graphics.DrawPath(pen, pinHead);
        graphics.DrawLine(pen, P(bounds, 10, 11), P(bounds, 10, 18));
    }

    private static void DrawFavorite(Graphics graphics, Pen pen, RectangleF bounds)
    {
        using var star = new GraphicsPath();
        PointF[] points = Enumerable.Range(0, 10)
            .Select(index =>
            {
                double angle = -Math.PI / 2 + index * Math.PI / 5;
                float radius = index % 2 == 0 ? 8.5f : 3.8f;
                return P(bounds, 10 + radius * (float)Math.Cos(angle),
                    10 + radius * (float)Math.Sin(angle));
            })
            .ToArray();
        star.AddPolygon(points);
        graphics.DrawPath(pen, star);
    }

    private static void DrawCreateShortcut(Graphics graphics, Pen pen,
        RectangleF bounds)
    {
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 2, 5, 11, 12)));
        graphics.DrawLine(pen, P(bounds, 8, 11), P(bounds, 18, 2));
        graphics.DrawLines(pen,
            [P(bounds, 12, 2), P(bounds, 18, 2), P(bounds, 18, 8)]);
    }

    private static void DrawProperties(Graphics graphics, Pen pen, RectangleF bounds)
    {
        using var spanner = new GraphicsPath(FillMode.Alternate);
        spanner.StartFigure();
        spanner.AddLines([
            P(bounds, 17.7f, 2.4f), P(bounds, 14.9f, 5.2f),
            P(bounds, 15.3f, 6.7f), P(bounds, 16.8f, 7.1f),
            P(bounds, 18.8f, 5.1f),
        ]);
        spanner.AddBezier(P(bounds, 18.8f, 5.1f),
            P(bounds, 19.2f, 8.3f), P(bounds, 16.4f, 10.8f),
            P(bounds, 13.2f, 10.0f));
        spanner.AddLine(P(bounds, 13.2f, 10.0f), P(bounds, 6.8f, 17.3f));
        spanner.AddBezier(P(bounds, 6.8f, 17.3f),
            P(bounds, 5.5f, 18.8f), P(bounds, 3.4f, 18.7f),
            P(bounds, 2.5f, 17.4f));
        spanner.AddBezier(P(bounds, 2.5f, 17.4f),
            P(bounds, 1.6f, 16.3f), P(bounds, 2.0f, 14.9f),
            P(bounds, 3.0f, 14.0f));
        spanner.AddLine(P(bounds, 3.0f, 14.0f), P(bounds, 9.9f, 7.5f));
        spanner.AddBezier(P(bounds, 9.9f, 7.5f),
            P(bounds, 9.3f, 4.5f), P(bounds, 11.8f, 1.7f),
            P(bounds, 14.7f, 1.6f));
        spanner.AddBezier(P(bounds, 14.7f, 1.6f),
            P(bounds, 15.9f, 1.5f), P(bounds, 17.1f, 1.9f),
            P(bounds, 17.7f, 2.4f));
        spanner.CloseFigure();
        spanner.AddEllipse(R(bounds, 3.9f, 15.2f, 2.0f, 2.0f));

        graphics.DrawPath(pen, spanner);
    }

    private static void DrawShowMore(Graphics graphics, Pen pen, RectangleF bounds)
    {
        graphics.DrawRectangle(pen, Rectangle.Round(R(bounds, 3, 3, 14, 14)));
        using var brush = new SolidBrush(pen.Color);
        foreach (float x in new[] { 7f, 10f, 13f })
            graphics.FillEllipse(brush, R(bounds, x - 0.8f, 9.2f, 1.6f, 1.6f));
    }

    private static PointF P(RectangleF bounds, float x, float y) => new(
        bounds.Left + x * bounds.Width / 20f,
        bounds.Top + y * bounds.Height / 20f);

    private static RectangleF R(RectangleF bounds, float x, float y,
        float width, float height) => new(
        bounds.Left + x * bounds.Width / 20f,
        bounds.Top + y * bounds.Height / 20f,
        width * bounds.Width / 20f,
        height * bounds.Height / 20f);

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        foreach (IntPtr bitmap in _bitmapHandles)
            DeleteObject(bitmap);
        _bitmapHandles.Clear();
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(ShellSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellSize
    {
        internal int Width;
        internal int Height;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuItemInfo(IntPtr menu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo itemInfo);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr deviceContext,
        ref BitmapInfo bitmapInfo, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectBitmap(IntPtr bitmap, int bufferSize,
        out NativeBitmap bitmapData);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap,
        uint startScan, uint scanLines, [Out] byte[] bits,
        ref BitmapInfo bitmapInfo, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
