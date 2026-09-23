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
    private int                            _contentInsetLeft;
    private readonly Panel                 _handlerHost;

    private const string HandlerShellexKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private readonly Font _placeholderFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
    public PreviewPane()
    {
        // Keep native preview-handler windows inside their own managed host. The
        // resize grip occupies the separate inset to its left, so a handler can
        // never cover the grip when SetRect runs after a drag.
        _handlerHost = new Panel
        {
            BackColor = ThemeManager.Window,
            TabStop = false,
        };
        _handlerHost.Paint += OnHandlerHostPaint;
        Controls.Add(_handlerHost);

        Width       = AppSettings.DefaultPreviewPaneWidth;
        BackColor   = ThemeManager.Window;
        BorderStyle = BorderStyle.None;
    }

    internal int ContentWidth => Math.Max(0, ClientSize.Width - _contentInsetLeft);

    internal int ContentInsetLeft => _contentInsetLeft;

    internal void SetContentInsetLeft(int inset)
    {
        int constrainedInset = Math.Clamp(inset, 0, ClientSize.Width);
        if (_contentInsetLeft == constrainedInset)
            return;

        _contentInsetLeft = constrainedInset;
        LayoutHandlerHost();
        if (_handler is not null)
        {
            var rc = HandlerRect();
            _handler.SetRect(ref rc);
        }
        BringManagedOverlaysToFront();
        _handlerHost.Invalidate();
    }

    /// <summary>Previews the given file path, or clears the preview if path is null/empty.</summary>
    public void Preview(string? filePath)
    {
        if (!IsHandleCreated) return;

        // Only skip when we already have a LIVE preview for this exact path. If a
        // previous attempt for the same path failed (no handler installed), fall
        // through and retry — otherwise a single transient failure (e.g. the pane
        // had no size yet, or the handler was momentarily unavailable) would leave
        // "Loading preview…" on screen permanently.
        if (filePath == _currentPath && _handler != null) return;

        _currentPath = filePath;

        ReleaseHandler();

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _handlerHost.Invalidate();
            return;
        }

        var clsid = FindPreviewHandlerClsid(filePath);
        if (clsid == null) { _handlerHost.Invalidate(); return; }

        try
        {
            var type = Type.GetTypeFromCLSID(clsid.Value);
            if (type == null) { _handlerHost.Invalidate(); return; }

            object instance = Activator.CreateInstance(type)!;
            if (instance is not NativeMethods.IPreviewHandler handler)
            {
                if (instance is not null) Marshal.ReleaseComObject(instance);
                _handlerHost.Invalidate();
                return;
            }

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

            if (!inited)
            {
                Marshal.ReleaseComObject(handler);
                _handlerHost.Invalidate();
                return;
            }

            _handlerHost.CreateControl();
            var rc = HandlerRect();
            handler.SetWindow(_handlerHost.Handle, ref rc);
            handler.SetRect(ref rc);
            handler.DoPreview();
            _handler = handler;
            BringManagedOverlaysToFront();
            ThemeManager.ApplyNativeWindow(Handle);
            _handlerHost.Invalidate();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(Preview), Path.GetFileName(filePath ?? ""));
            _handlerHost.Invalidate();
        }
    }

    internal void ApplyTheme()
    {
        BackColor = ThemeManager.Window;
        ForeColor = ThemeManager.Text;
        _handlerHost.BackColor = ThemeManager.Window;
        _handlerHost.ForeColor = ThemeManager.Text;
        BringManagedOverlaysToFront();
        if (IsHandleCreated)
            ThemeManager.ApplyNativeWindow(Handle);
        _handlerHost.Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutHandlerHost();
        if (_handler != null)
        {
            var rc = HandlerRect();
            _handler.SetRect(ref rc);
        }
        else
        {
            _handlerHost.Invalidate();
        }
        BringManagedOverlaysToFront();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // Release only the native preview handler here. The placeholder font is a
        // long-lived resource: WinForms recreates control handles on DPI changes
        // and reparenting, after which the host's Paint event still needs it.
        // Font disposal belongs in Dispose, which runs once at end of life.
        ReleaseHandler();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseHandler();
            _placeholderFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnHandlerHostPaint(object? sender, PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_handler != null) return; // handler owns the child window content

        string msg = string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath)
            ? "No preview available"
            : "Loading preview…";

        // TextRenderer uses GDI/ClearType — sharper and heavier than DrawString's GDI+.
        TextRenderer.DrawText(
            e.Graphics, msg, _placeholderFont, _handlerHost.ClientRectangle,
            ThemeManager.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private void LayoutHandlerHost()
    {
        _handlerHost.Bounds = new Rectangle(
            _contentInsetLeft,
            0,
            ContentWidth,
            ClientSize.Height);
    }

    private NativeMethods.RECT HandlerRect() => new(
        0,
        0,
        _handlerHost.ClientSize.Width,
        _handlerHost.ClientSize.Height);

    private void BringManagedOverlaysToFront()
    {
        foreach (Control child in Controls)
        {
            if (!ReferenceEquals(child, _handlerHost))
                child.BringToFront();
        }
    }

    private void ReleaseHandler()
    {
        if (_handler == null) return;
        try { _handler.Unload(); }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ReleaseHandler),
                "The preview handler failed during unload.");
        }
        try { Marshal.ReleaseComObject(_handler); }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(ReleaseHandler),
                "Could not release the preview-handler COM object.");
        }
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
