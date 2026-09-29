using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MultiExplorer;

internal readonly record struct ExplorerLaunchResult(bool WindowFound, bool Placed);

/// <summary>Opens a separate File Explorer window on the screen where a tab was dropped.</summary>
internal static class ExplorerWindowLauncher
{
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(8);

    internal static async Task<ExplorerLaunchResult> OpenAtAsync(
        string path, Point dropPosition, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await LaunchGate.WaitAsync(cancellationToken);
        try
        {
            HashSet<IntPtr> existingWindows = GetExplorerWindows();
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                ArgumentList = { "/n,", path },
            });

            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < WindowTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr window = GetExplorerWindows()
                    .FirstOrDefault(handle =>
                        !existingWindows.Contains(handle)
                        && NativeMethods.IsWindowVisible(handle)
                        && NativeMethods.GetWindowRect(handle, out NativeMethods.RECT bounds)
                        && bounds.Right > bounds.Left
                        && bounds.Bottom > bounds.Top);

                if (window != IntPtr.Zero)
                    return new ExplorerLaunchResult(true,
                        PlaceWindow(window, dropPosition));

                await Task.Delay(100, cancellationToken);
            }

            return new ExplorerLaunchResult(false, false);
        }
        finally
        {
            LaunchGate.Release();
        }
    }

    private static HashSet<IntPtr> GetExplorerWindows()
    {
        HashSet<IntPtr> windows = [];
        NativeMethods.EnumWindows((window, _) =>
        {
            var className = new StringBuilder(64);
            if (NativeMethods.GetClassName(window, className, className.Capacity) > 0
                && className.ToString() is "CabinetWClass" or "ExploreWClass")
                windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool PlaceWindow(IntPtr window, Point dropPosition)
    {
        bool wasMaximized = NativeMethods.IsZoomed(window);
        if (wasMaximized)
            NativeMethods.ShowWindow(window, NativeMethods.SW_RESTORE);

        if (!NativeMethods.GetWindowRect(window, out NativeMethods.RECT current))
            return false;

        Rectangle workingArea = Screen.FromPoint(dropPosition).WorkingArea;
        Rectangle destination = CalculateWindowBounds(workingArea,
            new Size(current.Right - current.Left, current.Bottom - current.Top));
        if (!NativeMethods.MoveWindow(window, destination.X, destination.Y,
                destination.Width, destination.Height, repaint: true))
            return false;

        NativeMethods.ShowWindow(window,
            wasMaximized ? NativeMethods.SW_MAXIMIZE : NativeMethods.SW_SHOW);
        return NativeMethods.SetForegroundWindow(window)
            || NativeMethods.GetForegroundWindow() == window;
    }

    internal static Rectangle CalculateWindowBounds(Rectangle workingArea, Size windowSize)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));

        int width = Math.Min(Math.Max(1, windowSize.Width), workingArea.Width);
        int height = Math.Min(Math.Max(1, windowSize.Height), workingArea.Height);
        return new Rectangle(
            workingArea.Left + (workingArea.Width - width) / 2,
            workingArea.Top + (workingArea.Height - height) / 2,
            width, height);
    }
}
