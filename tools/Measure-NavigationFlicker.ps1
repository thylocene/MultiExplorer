param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing.Common
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NavigationFlickerNative
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent,
        EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window,
        StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPropW")]
    public static extern IntPtr GetProp(IntPtr window, string propertyName);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    public static IntPtr FindDescendant(IntPtr parent, string desiredClass)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (window, _) =>
        {
            var className = new StringBuilder(64);
            GetClassName(window, className, className.Capacity);
            if (!className.ToString().Equals(desiredClass,
                    StringComparison.OrdinalIgnoreCase))
                return true;

            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Measure-WindowBrightness {
    param(
        [NavigationFlickerNative+Rectangle]$Rectangle,
        [string]$SavePath = ''
    )

    $width = $Rectangle.Right - $Rectangle.Left
    $height = $Rectangle.Bottom - $Rectangle.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($Rectangle.Left, $Rectangle.Top, 0, 0,
                [Drawing.Size]::new($width, $height))
        }
        finally {
            $graphics.Dispose()
        }

        [long]$brightness = 0
        [int]$samples = 0
        for ($y = 8; $y -lt $height; $y += 16) {
            for ($x = 8; $x -lt $width; $x += 16) {
                $pixel = $bitmap.GetPixel($x, $y)
                $brightness += [int](($pixel.R + $pixel.G + $pixel.B) / 3)
                $samples++
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($SavePath)) {
            $bitmap.Save($SavePath, [Drawing.Imaging.ImageFormat]::Png)
        }
        return $brightness / [Math]::Max(1, $samples)
    }
    finally {
        $bitmap.Dispose()
    }
}

$resolvedExecutable = [IO.Path]::GetFullPath($Executable)
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$process = Start-Process -FilePath $resolvedExecutable -ArgumentList '--theme=dark' -PassThru
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

    [NavigationFlickerNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 4

    $tree = [NavigationFlickerNative]::FindDescendant(
        $process.MainWindowHandle, 'SysTreeView32')
    if ($tree -eq [IntPtr]::Zero) {
        throw 'Could not locate the active navigation tree.'
    }

    $rectangle = [NavigationFlickerNative+Rectangle]::new()
    if (-not [NavigationFlickerNative]::GetWindowRect($tree, [ref]$rectangle)) {
        throw 'Could not read the navigation-tree rectangle.'
    }

    $markerName = 'MultiExplorer.NativeTheme.4FD929EE-85DD-44C8-BC73-3A6864450CC8'
    $markerBefore = [NavigationFlickerNative]::GetProp($tree, $markerName).ToInt64()
    $baselinePath = Join-Path $resolvedOutput 'baseline.png'
    $baseline = Measure-WindowBrightness $rectangle $baselinePath

    $tvmGetNextItem = 0x110A
    $tvmSelectItem = 0x110B
    $tvgnNext = 1
    $tvgnPrevious = 2
    $tvgnCaret = 9
    $selected = [NavigationFlickerNative]::SendMessage(
        $tree, $tvmGetNextItem, [IntPtr]$tvgnCaret, [IntPtr]::Zero)
    $target = [NavigationFlickerNative]::SendMessage(
        $tree, $tvmGetNextItem, [IntPtr]$tvgnNext, $selected)
    if ($target -eq [IntPtr]::Zero) {
        $target = [NavigationFlickerNative]::SendMessage(
            $tree, $tvmGetNextItem, [IntPtr]$tvgnPrevious, $selected)
    }
    if ($target -eq [IntPtr]::Zero) {
        throw 'The selected navigation item has no neighboring item to select.'
    }

    [NavigationFlickerNative]::SendMessage(
        $tree, $tvmSelectItem, [IntPtr]$tvgnCaret, $target) | Out-Null

    [double]$maximum = 0
    [int]$maximumFrame = -1
    $samples = [Collections.Generic.List[string]]::new()
    for ($frame = 0; $frame -lt 60; $frame++) {
        $value = Measure-WindowBrightness $rectangle
        $samples.Add("$frame,$value")
        if ($value -gt $maximum) {
            $maximum = $value
            $maximumFrame = $frame
            Measure-WindowBrightness $rectangle (
                Join-Path $resolvedOutput 'brightest.png') | Out-Null
        }
        Start-Sleep -Milliseconds 25
    }

    $markerAfter = [NavigationFlickerNative]::GetProp($tree, $markerName).ToInt64()
    @(
        "BaselineBrightness=$baseline"
        "MaximumBrightness=$maximum"
        "BrightnessIncrease=$($maximum - $baseline)"
        "MaximumFrame=$maximumFrame"
        "ThemeMarkerBefore=$markerBefore"
        "ThemeMarkerAfter=$markerAfter"
    ) | Set-Content -LiteralPath (Join-Path $resolvedOutput 'summary.txt')
    @('Frame,AverageBrightness') + $samples |
        Set-Content -LiteralPath (Join-Path $resolvedOutput 'samples.csv')
    Get-Content -LiteralPath (Join-Path $resolvedOutput 'summary.txt')
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}
