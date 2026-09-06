using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// A strip panel docked at the bottom of a PanelView that shows the name, type,
/// date modified, and size of the currently selected file/folder.
/// Updated on each PollPath tick via ShowItem().
/// </summary>
internal sealed class DetailsPanel : Panel
{
    private string? _filePath;

    public DetailsPanel()
    {
        Height      = 120;
        BackColor   = SystemColors.Window;
        BorderStyle = BorderStyle.None;
        Padding     = new Padding(8, 6, 8, 6);
    }

    public void ShowItem(string? filePath)
    {
        if (_filePath == filePath) return;
        _filePath = filePath;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        // Top separator line
        using var pen = new Pen(SystemColors.ControlLight);
        g.DrawLine(pen, 0, 0, Width, 0);

        if (string.IsNullOrEmpty(_filePath)
            || (!File.Exists(_filePath) && !Directory.Exists(_filePath)))
        {
            DrawPlaceholder(g);
            return;
        }

        bool isDir = Directory.Exists(_filePath);

        // Shell icon
        var icon = GetShellIcon(_filePath, 32);
        if (icon != null)
        {
            g.DrawImage(icon, Padding.Left, Padding.Top + 4, 32, 32);
            icon.Dispose();
        }

        int textX = Padding.Left + 40;
        int y     = Padding.Top;

        // Name (bold)
        string name = Path.GetFileName(_filePath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = _filePath;
        using var boldFont = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold);
        using var textBrush = new SolidBrush(SystemColors.WindowText);
        g.DrawString(name, boldFont, textBrush, textX, y);
        y += (int)boldFont.GetHeight(g) + 2;

        using var grayBrush = new SolidBrush(SystemColors.GrayText);

        // Type
        string type = isDir ? "File folder" : GetFileType(_filePath);
        g.DrawString(type, Font, grayBrush, textX, y);
        y += (int)Font.GetHeight(g) + 1;

        if (!isDir)
        {
            try
            {
                long bytes = new FileInfo(_filePath).Length;
                g.DrawString(FormatSize(bytes), Font, grayBrush, textX, y);
                y += (int)Font.GetHeight(g) + 1;

                var modified = File.GetLastWriteTime(_filePath);
                g.DrawString($"Modified: {modified:g}", Font, grayBrush, textX, y);
            }
            catch (Exception ex) { AppLog.Debug(ex, nameof(OnPaint)); }
        }
        else
        {
            try
            {
                var modified = Directory.GetLastWriteTime(_filePath);
                g.DrawString($"Modified: {modified:g}", Font, grayBrush, textX, y);
            }
            catch (Exception ex) { AppLog.Debug(ex, nameof(OnPaint)); }
        }
    }

    private void DrawPlaceholder(Graphics g)
    {
        using var brush = new SolidBrush(SystemColors.GrayText);
        var sf = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString("No item selected", Font, brush, ClientRectangle, sf);
    }

    private static Bitmap? GetShellIcon(string path, int size)
    {
        try
        {
            var shfi = new NativeMethods.SHFILEINFOW();
            const uint SHGFI_ICON      = 0x100;
            const uint SHGFI_SMALLICON = 0x001;
            uint flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : 0u);
            NativeMethods.SHGetFileInfoW(path, 0, ref shfi,
                (uint)Marshal.SizeOf(shfi), flags);
            if (shfi.hIcon == IntPtr.Zero) return null;
            var bmp = Icon.FromHandle(shfi.hIcon).ToBitmap();
            NativeMethods.DestroyIcon(shfi.hIcon);
            return bmp;
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(GetShellIcon)); return null; }
    }

    private static string GetFileType(string path)
    {
        try
        {
            var shfi = new NativeMethods.SHFILEINFOW();
            const uint SHGFI_TYPENAME = 0x400;
            NativeMethods.SHGetFileInfoW(path, 0, ref shfi,
                (uint)Marshal.SizeOf(shfi), SHGFI_TYPENAME);
            return string.IsNullOrEmpty(shfi.szTypeName) ? "File" : shfi.szTypeName;
        }
        catch (Exception ex) { AppLog.Debug(ex, nameof(GetFileType)); return "File"; }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)             return $"{bytes} bytes";
        if (bytes < 1024 * 1024)      return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
