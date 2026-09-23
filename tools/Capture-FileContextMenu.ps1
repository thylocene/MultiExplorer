param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [int]$ItemIndex = 3,

    [ValidateSet('light', 'dark')]
    [string]$Theme = 'light',

    [string]$CommandHoverDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing.Common
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class FileMenuCaptureNative
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent,
        EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window,
        StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint x, uint y,
        uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte virtualKey, byte scanCode,
        uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu,
        uint item, out Rectangle rectangle);

    public static IntPtr FindLeftmostVisibleList(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        int left = int.MaxValue;
        EnumChildWindows(parent, (window, _) =>
        {
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (!className.ToString().Contains("SysListView32",
                    StringComparison.OrdinalIgnoreCase)
                || !IsWindowVisible(window)
                || !GetWindowRect(window, out Rectangle rectangle)
                || rectangle.Right - rectangle.Left < 100
                || rectangle.Bottom - rectangle.Top < 100
                || rectangle.Left >= left)
                return true;

            found = window;
            left = rectangle.Left;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static Rectangle[] FindPopupMenus(uint expectedProcessId)
    {
        var rectangles = new List<Rectangle>();
        EnumWindows((window, _) =>
        {
            var className = new StringBuilder(32);
            GetClassName(window, className, className.Capacity);
            GetWindowThreadProcessId(window, out uint processId);
            if (processId == expectedProcessId
                && className.ToString() == "#32768"
                && IsWindowVisible(window)
                && GetWindowRect(window, out Rectangle rectangle)
                && rectangle.Right > rectangle.Left
                && rectangle.Bottom > rectangle.Top)
                rectangles.Add(rectangle);
            return true;
        }, IntPtr.Zero);
        return rectangles.ToArray();
    }

    public static bool TryFindCommandRow(uint expectedProcessId,
        out Rectangle commandRow)
    {
        Rectangle locatedRow = default;
        bool found = false;
        EnumWindows((window, _) =>
        {
            var className = new StringBuilder(32);
            GetClassName(window, className, className.Capacity);
            GetWindowThreadProcessId(window, out uint processId);
            if (processId != expectedProcessId
                || className.ToString() != "#32768"
                || !IsWindowVisible(window))
                return true;

            IntPtr menu = SendMessage(window, 0x01E1, IntPtr.Zero, IntPtr.Zero);
            int count = GetMenuItemCount(menu);
            for (int position = 0; position < count; position++)
            {
                if (GetMenuItemID(menu, position) != 0xF001
                    || !GetMenuItemRect(IntPtr.Zero, menu, (uint)position,
                        out Rectangle rectangle))
                    continue;

                locatedRow = rectangle;
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        commandRow = locatedRow;
        return found;
    }
}
'@

$resolvedExecutable = [IO.Path]::GetFullPath($Executable)
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) |
    Out-Null
$process = Start-Process -FilePath $resolvedExecutable -ArgumentList "--theme=$Theme" -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and
             [DateTime]::UtcNow -lt $deadline)

    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'MultiExplorer did not create a main window.'
    }

    [FileMenuCaptureNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 4
    $list = [FileMenuCaptureNative]::FindLeftmostVisibleList(
        $process.MainWindowHandle)
    if ($list -eq [IntPtr]::Zero) {
        throw 'Could not find the left file list.'
    }

    $listRectangle = [FileMenuCaptureNative+Rectangle]::new()
    if (-not [FileMenuCaptureNative]::GetWindowRect($list, [ref]$listRectangle)) {
        throw 'Could not read the file-list position.'
    }

    $rowHeight = 32
    $headerHeight = 26
    $clientX = 120
    $clientY = $headerHeight + ($ItemIndex * $rowHeight) +
        [Math]::Floor($rowHeight / 2)
    $x = $listRectangle.Left + $clientX
    $y = $listRectangle.Top + $clientY
    [FileMenuCaptureNative]::SetCursorPos($x, $y) | Out-Null
    $coordinates = [IntPtr](($clientY -shl 16) -bor ($clientX -band 0xFFFF))
    [FileMenuCaptureNative]::PostMessage($list, 0x0204, [IntPtr]2, $coordinates) |
        Out-Null
    [FileMenuCaptureNative]::PostMessage($list, 0x0205, [IntPtr]::Zero, $coordinates) |
        Out-Null
    Start-Sleep -Seconds 2

    $popups = [FileMenuCaptureNative]::FindPopupMenus([uint32]$process.Id)
    if ($popups.Length -eq 0) {
        throw 'The file context menu did not open.'
    }

    $left = ($popups | Measure-Object Left -Minimum).Minimum
    $top = ($popups | Measure-Object Top -Minimum).Minimum
    $right = ($popups | Measure-Object Right -Maximum).Maximum
    $bottom = ($popups | Measure-Object Bottom -Maximum).Maximum
    "PopupBounds=$left,$top,$right,$bottom"
    $width = [int]$right - [int]$left
    $height = [int]$bottom - [int]$top
    if ($width -le 0 -or $height -le 0) {
        throw "The context menu returned invalid bounds: $left,$top,$right,$bottom"
    }

    if (-not [string]::IsNullOrWhiteSpace($CommandHoverDirectory)) {
        $hoverDirectory = [IO.Path]::GetFullPath($CommandHoverDirectory)
        [IO.Directory]::CreateDirectory($hoverDirectory) | Out-Null
        $commandRow = [FileMenuCaptureNative+Rectangle]::new()
        if (-not [FileMenuCaptureNative]::TryFindCommandRow(
                [uint32]$process.Id, [ref]$commandRow)) {
            throw 'Could not locate the context-menu action row.'
        }

        $commandNames = @('Cut', 'Copy', 'Rename', 'Share', 'Delete')
        for ($index = 0; $index -lt $commandNames.Count; $index++) {
            $hoverX = $commandRow.Left + [Math]::Floor(
                (($index * 2 + 1) * ($commandRow.Right - $commandRow.Left)) /
                ($commandNames.Count * 2))
            $hoverY = $commandRow.Top + [Math]::Floor(
                ($commandRow.Bottom - $commandRow.Top) / 2)
            [FileMenuCaptureNative]::SetCursorPos($hoverX, $hoverY) | Out-Null
            Start-Sleep -Milliseconds 350

            $hoverBitmap = [Drawing.Bitmap]::new($width, $height)
            try {
                $hoverGraphics = [Drawing.Graphics]::FromImage($hoverBitmap)
                try {
                    $hoverGraphics.CopyFromScreen(
                        $left, $top, 0, 0, $hoverBitmap.Size)
                }
                finally {
                    $hoverGraphics.Dispose()
                }
                $hoverPath = Join-Path $hoverDirectory (
                    "{0}-{1}.png" -f $index, $commandNames[$index])
                $hoverBitmap.Save($hoverPath,
                    [Drawing.Imaging.ImageFormat]::Png)
                $hoverPath
            }
            finally {
                $hoverBitmap.Dispose()
            }
        }
    }

    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    [FileMenuCaptureNative]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [FileMenuCaptureNative]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
    $resolvedOutput
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}
