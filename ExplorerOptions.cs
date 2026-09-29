using System.Diagnostics;

namespace MultiExplorer;

internal static class ExplorerOptions
{
    internal static void Open()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,Options_RunDLL 0",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(Open),
                "Could not open Windows Explorer Options.");
        }
    }
}
