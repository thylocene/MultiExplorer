using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MultiExplorer;

/// <summary>
/// Right-side panel that hosts a Windows shell IPreviewHandler for the selected file.
/// The preview handler CLSID is looked up from the HKCR per-extension shellex key.
/// </summary>
internal sealed class PreviewPane : Panel
{
    private NativeMethods.IPreviewHandler? _handler;
    private string?                        _currentPath;

    private const string HandlerShellexKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private readonly Font _placeholderFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Color ColPlaceholder = Color.FromArgb(0x80, 0x80, 0x80);

    public PreviewPane()
    {
        Width       = 300;
        BackColor   = SystemColors.Window;
        BorderStyle = BorderStyle.None;
    }

    /// <summary>Previews the given file path, or clears the preview if path is null/empty.</summary>
    public void Preview(string? filePath)
    {
        if (!IsHandleCreated) return;
        if (filePath == _currentPath) return;
        _currentPath = filePath;

        ReleaseHandler();

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            Invalidate();
            return;
        }

        var clsid = FindPreviewHandlerClsid(filePath);
        if (clsid == null) { Invalidate(); return; }

        try
        {
            var type = Type.GetTypeFromCLSID(clsid.Value);
            if (type == null) { Invalidate(); return; }

            var handler = Activator.CreateInstance(type) as NativeMethods.IPreviewHandler;
            if (handler == null) { Invalidate(); return; }

            bool inited = false;

            // IInitializeWithFile is the most common init path
            if (!inited && handler is NativeMethods.IInitializeWithFile iwf)
                inited = iwf.Initialize(filePath, 0 /*STGM_READ*/) >= 0;

            // IInitializeWithItem as fallback
            if (!inited && handler is NativeMethods.IInitializeWithItem iwi)
            {
                var siId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                if (NativeMethods.SHCreateItemFromParsingName(
                        filePath, IntPtr.Zero, ref siId, out IntPtr siPtr) >= 0
                    && siPtr != IntPtr.Zero)
                {
                    try
                    {
                        var si = (NativeMethods.IShellItem)Marshal.GetObjectForIUnknown(siPtr);
                        inited = iwi.Initialize(si, 0) >= 0;
                    }
                    finally { Marshal.Release(siPtr); }
                }
            }

            if (!inited) { Invalidate(); return; }

            var rc = ClientRect();
            handler.SetWindow(Handle, ref rc);
            handler.SetRect(ref rc);
            handler.DoPreview();
            _handler = handler;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(Preview), Path.GetFileName(filePath ?? ""));
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_handler != null)
        {
            var rc = ClientRect();
            _handler.SetRect(ref rc);
        }
        else
        {
            Invalidate();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseHandler();
        _placeholderFont.Dispose();
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_handler != null) return; // handler owns the child window content

        // Left border separator
        using var pen = new Pen(SystemColors.ControlLight);
        e.Graphics.DrawLine(pen, 0, 0, 0, Height);

        string msg = string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath)
            ? "No preview available"
            : "Loading preview…";

        // TextRenderer uses GDI/ClearType — sharper and heavier than DrawString's GDI+.
        TextRenderer.DrawText(
            e.Graphics, msg, _placeholderFont, ClientRectangle, ColPlaceholder,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private NativeMethods.RECT ClientRect()
        => new NativeMethods.RECT(0, 0, Width, Height);

    private void ReleaseHandler()
    {
        if (_handler == null) return;
        try { _handler.Unload(); }   catch { }
        try { Marshal.ReleaseComObject(_handler); } catch { }
        _handler = null;
    }

    private static Guid? FindPreviewHandlerClsid(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return null;

        // HKCR\.<ext>\shellex\{handler-category}
        using var k1 = Registry.ClassesRoot.OpenSubKey($@"{ext}\shellex\{HandlerShellexKey}");
        if (k1?.GetValue(null) is string s1 && Guid.TryParse(s1, out var g1)) return g1;

        // Resolve ProgID and try again
        using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
        if (extKey?.GetValue(null) is string progId && !string.IsNullOrEmpty(progId))
        {
            using var k2 = Registry.ClassesRoot.OpenSubKey(
                $@"{progId}\shellex\{HandlerShellexKey}");
            if (k2?.GetValue(null) is string s2 && Guid.TryParse(s2, out var g2)) return g2;
        }

        return null;
    }
}
