using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiExplorer;

/// <summary>
/// Places newly-created Shell operation windows at the top of the non-modal
/// window stack. The temporary topmost state makes the progress window visible
/// when it first appears, then it is returned to ordinary z-order so it cannot
/// remain above unrelated applications.
/// </summary>
internal sealed class OperationWindowPromoter : IDisposable
{
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNotTopMost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint PositionFlags = SwpNoSize | SwpNoMove
        | SwpNoActivate | SwpShowWindow;
    private static readonly TimeSpan TopMostDuration = TimeSpan.FromMilliseconds(900);

    private readonly object _gate = new();
    private readonly HashSet<IntPtr> _handledWindows = [];
    private readonly Dictionary<IntPtr, DateTime> _promotedWindows = [];
    private readonly System.Threading.Timer _timer;
    private readonly OperationWindowPlacement? _placement;
    private readonly uint _operationThreadId;
    private bool _disposed;
    private int _callbackActive;

    internal OperationWindowPromoter(OperationWindowPlacement? placement)
    {
        _placement = placement;
        _operationThreadId = GetCurrentThreadId();
        _timer = new System.Threading.Timer(
            static state => ((OperationWindowPromoter)state!).Poll(),
            this, TimeSpan.Zero, TimeSpan.FromMilliseconds(75));
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _callbackActive, 1) != 0) return;

        try
        {
            lock (_gate)
            {
                if (_disposed) return;

                DateTime now = DateTime.UtcNow;
                foreach (IntPtr window in _promotedWindows
                             .Where(pair => pair.Value <= now)
                             .Select(static pair => pair.Key)
                             .ToArray())
                {
                    ReturnToNormalZOrder(window);
                    _promotedWindows.Remove(window);
                }

                EnumWindows((window, _) =>
                {
                    uint windowThreadId = GetWindowThreadProcessId(window, out uint processId);
                    if (processId != (uint)Environment.ProcessId
                        || windowThreadId != _operationThreadId
                        || !IsWindowVisible(window)
                        || !_handledWindows.Add(window))
                        return true;

                    if (PromoteAndPosition(window))
                    {
                        _promotedWindows[window] = now + TopMostDuration;
                    }

                    return true;
                }, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not promote an operation window: {ex}");
        }
        finally
        {
            Volatile.Write(ref _callbackActive, 0);
        }
    }

    private bool PromoteAndPosition(IntPtr window)
    {
        uint flags = PositionFlags;
        int x = 0;
        int y = 0;
        if (_placement is not null
            && GetWindowRect(window, out WindowRectangle bounds)
            && _placement.TryCalculateLocation(
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                out x,
                out y))
        {
            flags &= ~SwpNoMove;
        }

        // SWP_NOACTIVATE keeps the Shell window non-modal. Its short topmost
        // promotion remains solely a visibility aid and is removed after 900 ms.
        return SetWindowPos(window, HwndTopMost, x, y, 0, 0, flags);
    }

    private static void ReturnToNormalZOrder(IntPtr window)
    {
        if (window != IntPtr.Zero && IsWindow(window))
            SetWindowPos(window, HwndNotTopMost, 0, 0, 0, 0, PositionFlags);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            _timer.Dispose();
            foreach (IntPtr window in _promotedWindows.Keys)
                ReturnToNormalZOrder(window);
            _promotedWindows.Clear();
            _handledWindows.Clear();
        }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window,
        out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window,
        out WindowRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window,
        IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
