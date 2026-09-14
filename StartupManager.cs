using System;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>Maintains the current user's Windows sign-in launch registration.</summary>
internal static class StartupManager
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "MultiExplorer";

    internal static string BuildRunCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" --startup";
    }

    internal static bool TrySetEnabled(bool enabled, out Exception? error)
    {
        try
        {
            if (enabled)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                    ?? throw new InvalidOperationException(
                        "The Windows startup registry key could not be opened.");
                key.SetValue(ValueName, BuildRunCommand(Application.ExecutablePath),
                    RegistryValueKind.String);
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }
}
