using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MultiExplorer;

/// <summary>Shared shape helper for application popup surfaces.</summary>
internal static class PopupVisuals
{
    internal const int CornerRadiusLogical = 12;
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;

    internal static void DisableNonClientShadow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int policy = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY,
                ref policy, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal static GraphicsPath CreateBottomRoundedPath(Rectangle bounds, int radius) =>
        CreateBottomRoundedPath(
            new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);

    internal static GraphicsPath CreateBottomRoundedPath(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        radius = diameter / 2;
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(bounds.Left, bounds.Top, bounds.Right, bounds.Top);
        path.AddLine(bounds.Right, bounds.Top, bounds.Right, bounds.Bottom - radius);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
            diameter, diameter, 0, 90);
        path.AddLine(bounds.Right - radius, bounds.Bottom,
            bounds.Left + radius, bounds.Bottom);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute,
        ref int value, int valueSize);
}
