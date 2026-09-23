using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace MultiExplorer;

internal readonly record struct StartTileIcons(Uri Square44, Uri Square150);

/// <summary>
/// Creates Start-tile artwork from the file or folder's real Shell icon. The
/// two native-size images keep the icon sharp at the sizes used by Start.
/// </summary>
internal static class StartTileIconService
{
    private const uint DibRgbColors = 0;
    private const uint SiigbfIconOnly = 0x00000004;
    private const uint SiigbfScaleUp = 0x00000100;
    private const string IconFolderName = "StartTileIcons";

    internal static async Task<StartTileIcons?> PrepareAsync(string path,
        string tileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);

        try
        {
            string iconFolderPath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path, IconFolderName);
            Directory.CreateDirectory(iconFolderPath);

            string square44Name = $"{tileId}-44.png";
            string square150Name = $"{tileId}-150.png";
            await SaveShellIconAsync(path, 44,
                Path.Combine(iconFolderPath, square44Name),
                cancellationToken);
            await SaveShellIconAsync(path, 150,
                Path.Combine(iconFolderPath, square150Name),
                cancellationToken);

            return new StartTileIcons(
                new Uri($"ms-appdata:///local/{IconFolderName}/{square44Name}"),
                new Uri($"ms-appdata:///local/{IconFolderName}/{square150Name}"));
        }
        catch (Exception ex) when (
            ex is COMException or
            ExternalException or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            AppLog.Debug(ex, nameof(PrepareAsync),
                $"Could not create Start artwork for '{path}'.");
            return null;
        }
    }

    private static async Task SaveShellIconAsync(string path, int size,
        string destinationPath, CancellationToken cancellationToken)
    {
        using Bitmap bitmap = GetShellIcon(path, size)
            ?? throw new InvalidOperationException(
                $"Windows did not return a {size}-pixel Shell icon.");
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        await File.WriteAllBytesAsync(destinationPath, stream.ToArray(),
            cancellationToken);
    }

    private static Bitmap? GetShellIcon(string path, int size)
    {
        Guid imageFactoryId = typeof(IShellItemImageFactory).GUID;
        int result = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero,
            ref imageFactoryId, out IntPtr factoryPointer);
        if (result < 0 || factoryPointer == IntPtr.Zero) return null;

        IShellItemImageFactory? factory = null;
        IntPtr nativeBitmap = IntPtr.Zero;
        try
        {
            factory = (IShellItemImageFactory)
                Marshal.GetObjectForIUnknown(factoryPointer);
            result = factory.GetImage(new ShellSize(size, size),
                SiigbfIconOnly | SiigbfScaleUp, out nativeBitmap);
            if (result < 0 || nativeBitmap == IntPtr.Zero) return null;

            return CopyBitmap(nativeBitmap);
        }
        finally
        {
            if (nativeBitmap != IntPtr.Zero) DeleteObject(nativeBitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
            Marshal.Release(factoryPointer);
        }
    }

    private static Bitmap? CopyBitmap(IntPtr sourceBitmap)
    {
        if (GetObjectBitmap(sourceBitmap, Marshal.SizeOf<NativeBitmap>(),
                out NativeBitmap source) <= 0
            || source.Width <= 0 || source.Height == 0)
            return null;

        int width = source.Width;
        int height = Math.Abs(source.Height);
        int sourceStride = width * 4;
        var pixels = new byte[sourceStride * height];
        var bitmapInfo = new BitmapInfo
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

        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            if (screenDc == IntPtr.Zero
                || GetDIBits(screenDc, sourceBitmap, 0, (uint)height, pixels,
                    ref bitmapInfo, DibRgbColors) == 0)
                return null;
        }
        finally
        {
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }

        EnsureVisibleAlpha(pixels);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(pixels, row * sourceStride,
                    IntPtr.Add(data.Scan0, row * data.Stride), sourceStride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static void EnsureVisibleAlpha(Span<byte> pixels)
    {
        bool hasVisiblePixel = false;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] == 0) continue;
            hasVisiblePixel = true;
            break;
        }

        if (hasVisiblePixel) return;

        for (int index = 0; index + 3 < pixels.Length; index += 4)
        {
            if (pixels[index] != 0 || pixels[index + 1] != 0
                || pixels[index + 2] != 0)
                pixels[index + 3] = byte.MaxValue;
        }
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
    private readonly record struct ShellSize(int Width, int Height);

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

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectBitmap(IntPtr handle, int size,
        out NativeBitmap value);

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
