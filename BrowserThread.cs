using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// Owns a dedicated STA thread that hosts a single native child window and pumps
/// its own message loop. The embedded <c>IExplorerBrowser</c> is created against
/// this window (see <see cref="ExplorerHost"/>).
///
/// Why this exists
/// ---------------
/// A native shell copy / move / delete initiated inside the hosted view runs a
/// nested modal message loop on the thread that owns the view's window. When the
/// browser was hosted directly on the application's UI thread, that nested loop
/// suspended the whole application until the operation finished or was cancelled.
///
/// By giving the browser its own thread and pump, the shell's nested loop blocks
/// only this thread; the application's UI thread keeps running and stays
/// responsive. All COM interaction with the browser is marshalled onto this
/// thread via <see cref="Invoke"/> so every call executes in the apartment that
/// owns the object.
/// </summary>
internal sealed class BrowserThread : IDisposable
{
    // A single shared window class for every browser-thread host window.
    private static readonly object _classLock = new();
    private static bool _classRegistered;
    private static readonly ConcurrentDictionary<IntPtr, BrowserThread> _hosts = new();
    private const string ClassName = "MultiExplorerBrowserHost";

    // Keep the WndProc delegate alive for the process lifetime so the thunk that
    // the class registration points at is never collected.
    private static NativeMethods.WndProcDelegate? _sharedWndProc;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ConcurrentQueue<Action> _work = new();

    /// <summary>
    /// Optional hook invoked on the browser thread for every message retrieved by
    /// the pump, before TranslateMessage/DispatchMessage. Return true to mark the
    /// message handled and skip default dispatch. This is how <see cref="ExplorerHost"/>
    /// intercepts shell keyboard/mouse input now that the shell windows live on this
    /// thread rather than the application UI thread.
    /// </summary>
    public Func<NativeMethods.MSG, bool>? MessageHook { get; set; }

    private IntPtr _hwnd;
    private uint   _threadId;
    private volatile bool _disposed;
    private Exception? _startupError;

    /// <summary>The native child window handle owned by the browser thread.</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>The Win32 id of the STA thread that owns the host window.</summary>
    public uint ThreadId => _threadId;

    /// <summary>
    /// Starts the thread and creates the host window as a child of
    /// <paramref name="parent"/> with the given initial client size. Blocks until
    /// the window exists (or startup fails).
    /// </summary>
    public BrowserThread(IntPtr parent, int width, int height)
    {
        EnsureClassRegistered();

        _thread = new Thread(() => ThreadMain(parent, width, height))
        {
            IsBackground = true,
            Name         = "MultiExplorer.BrowserThread",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _ready.Wait();
        if (_startupError != null)
            throw new InvalidOperationException(
                "Failed to start the browser host thread.", _startupError);

        // Creating a cross-thread child while the UI thread waits for _ready can
        // deadlock: CreateWindow sends synchronous parent notifications. Create
        // the window unparented on its STA first, then attach it after that STA's
        // message pump is available.
        int childStyle = (int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE
            | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, childStyle);
        NativeMethods.SetParent(_hwnd, parent);
        NativeMethods.MoveWindow(_hwnd, 0, 0, Math.Max(1, width), Math.Max(1, height), true);
    }

    private static void EnsureClassRegistered()
    {
        lock (_classLock)
        {
            if (_classRegistered) return;

            _sharedWndProc = StaticWndProc;
            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_sharedWndProc),
                hInstance     = NativeMethods.GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            if (NativeMethods.RegisterClassW(ref wc) == 0)
                throw new InvalidOperationException(
                    "RegisterClassW failed for the browser host window class.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            _classRegistered = true;
        }
    }

    private void ThreadMain(IntPtr parent, int width, int height)
    {
        try
        {
            // The browser thread must be an OLE-initialised STA: the shell view
            // creates windows and uses drag-drop, both of which require it.
            NativeMethods.OleInitialize(IntPtr.Zero);

            _threadId = NativeMethods.GetCurrentThreadId();

            _hwnd = NativeMethods.CreateWindowExW(
                0,
                ClassName,
                null,
                NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0, 0, Math.Max(1, width), Math.Max(1, height),
                IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            _hosts[_hwnd] = this;
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        // Classic pump. WM_APP_INVOKE wakes us to drain the work queue; WM_QUIT
        // (posted by Dispose via PostQuitMessage) ends the loop.
        while (NativeMethods.GetMessageW(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == NativeMethods.WM_APP_INVOKE)
            {
                DrainWork();
                continue;
            }

            var hook = MessageHook;
            if (hook != null)
            {
                try { if (hook(msg)) continue; }
                catch (Exception ex) { AppLog.Debug(ex, nameof(BrowserThread), "Message hook threw."); }
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        // Drain any final queued work before tearing down the apartment.
        DrainWork();

        if (_hwnd != IntPtr.Zero)
        {
            _hosts.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        NativeMethods.OleUninitialize();
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out Action? action))
        {
            try { action(); }
            catch (Exception ex) { AppLog.Debug(ex, nameof(BrowserThread), "Queued browser-thread work threw."); }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> synchronously on the browser thread and
    /// returns once it has completed. Exceptions are propagated to the caller.
    /// If called from the browser thread itself, runs inline to avoid deadlock.
    /// </summary>
    public void Invoke(Action action)
    {
        if (_disposed) return;

        if (NativeMethods.GetCurrentThreadId() == _threadId)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? captured = null;
        _work.Enqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        });
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        done.Wait();

        if (captured != null)
            throw new System.Reflection.TargetInvocationException(captured);
    }

    /// <summary>Runs <paramref name="func"/> synchronously on the browser thread and returns its result.</summary>
    public T Invoke<T>(Func<T> func)
    {
        T result = default!;
        Invoke(() => { result = func(); });
        return result;
    }

    /// <summary>
    /// Queues work on the browser thread without waiting for it.  This is used
    /// for layout updates: if the shell is inside a modal drag/drop operation,
    /// resizing the WinForms surface must not make the UI thread wait for it.
    /// </summary>
    public void Post(Action action)
    {
        if (_disposed) return;

        _work.Enqueue(action);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Ask the pump to exit, then wait for the thread to unwind (window
        // destruction + OleUninitialize happen on the thread it belongs to).
        // Posting WM_QUIT to the thread queue makes GetMessage return 0.
        if (_threadId != 0)
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
            AppLog.Debug(null, nameof(BrowserThread), "Browser thread did not exit within the timeout.");

        _ready.Dispose();
    }

    // ── Shared WndProc ──────────────────────────────────────────────────────────
    // The host window is a plain child container; the browser paints over it and
    // handles input itself. We only need default processing here.
    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // OLE drag/drop replaces the outer pump with a nested modal loop. That
        // loop still dispatches messages to this HWND, so drain queued work here
        // as well; otherwise a synchronous query from the UI thread would wait
        // until the entire file operation completed.
        if (msg == NativeMethods.WM_APP_INVOKE && _hosts.TryGetValue(hWnd, out BrowserThread? host))
        {
            host.DrainWork();
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
