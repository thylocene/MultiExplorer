using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// Borderless popup whose complete surface is presented as a 32-bit alpha
/// bitmap.  Unlike a Win32 Region, its lower corners are antialiased.
/// </summary>
internal abstract class LayeredPopupForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;

    protected LayeredPopupForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected int CornerRadius => Math.Max(8,
        PopupVisuals.CornerRadiusLogical * DeviceDpi / 96);

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupVisuals.DisableNonClientShadow(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshSurface();
    }

    protected void RefreshSurface()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            RenderSurface(graphics);
        }
        Present(bitmap);
    }

    protected abstract void RenderSurface(Graphics graphics);

    private void Present(Bitmap bitmap)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr bitmapHandle = CreateAlphaBitmap(memoryDc, bitmap);
        IntPtr previousBitmap = SelectObject(memoryDc, bitmapHandle);
        try
        {
            var destination = new NativePoint(Left, Top);
            var size = new NativeSize(Width, Height);
            var source = new NativePoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                memoryDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memoryDc, previousBitmap);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static IntPtr CreateAlphaBitmap(IntPtr memoryDc, Bitmap bitmap)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = bitmap.Width,
                Height = -bitmap.Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)(bitmap.Width * bitmap.Height * 4),
            },
        };
        IntPtr dib = CreateDIBSection(memoryDc, ref info, 0,
            out IntPtr destinationBits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || destinationBits == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            int rowBytes = bitmap.Width * 4;
            var row = new byte[rowBytes];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(destinationBits, y * rowBytes), rowBytes);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return dib;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X, Y;
        internal NativePoint(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        internal int Width, Height;
        internal NativeSize(int width, int height) { Width = width; Height = height; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        internal byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hDc, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst,
        ref NativePoint destination, ref NativeSize size, IntPtr hdcSrc,
        ref NativePoint source, int colorKey, ref BlendFunction blend, uint flags);
}
