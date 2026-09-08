using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MultiExplorer;

public enum LogSeverity { Debug, Warn, Error }

public sealed class LogEntry
{
    public LogSeverity Severity  { get; }
    public string      Context   { get; }
    public string      Message   { get; }
    public Exception?  Exception { get; }
    public DateTime    Timestamp { get; }

    internal LogEntry(LogSeverity severity, string context, string message, Exception? ex)
    {
        Severity  = severity;
        Context   = context;
        Message   = string.IsNullOrEmpty(message) && ex != null ? ex.Message : message;
        Exception = ex;
        Timestamp = DateTime.Now;
    }

    /// <summary>One-line summary suitable for a status bar.</summary>
    public string ShortMessage =>
        string.IsNullOrEmpty(Message) ? Context : $"{Context} — {Message}";

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Severity,-5}] {Context}");
        if (!string.IsNullOrEmpty(Message)) sb.Append($": {Message}");
        if (Exception != null)
            sb.Append($"\n  {Exception.GetType().Name}: {Exception.Message}");
        if (Exception?.InnerException != null)
            sb.Append($"\n  Inner: {Exception.InnerException.Message}");
        return sb.ToString();
    }
}

/// <summary>
/// Lightweight application logger.
/// Debug entries go only to the VS Output window and the log file.
/// Warn and Error entries additionally fire <see cref="MessageLogged"/> so the
/// status bar (subscribed in MainForm) can show them to the user.
/// </summary>
public static class AppLog
{
    private static readonly string _logDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MultiExplorer");

    private static readonly string _logPath = Path.Combine(_logDir, "app.log");

    /// <summary>
    /// Raised for Warn/Error entries only.
    /// Fired on whichever thread called the log method — subscribers must
    /// marshal to the UI thread themselves if needed.
    /// </summary>
    public static event Action<LogEntry>? MessageLogged;

    public static string LogFilePath => _logPath;

    // ── Logging API ───────────────────────────────────────────────────────────

    /// <summary>Expected / benign failure — log file and debug output only, never status bar.</summary>
    public static void Debug(Exception? ex, string context, string message = "")
        => Log(LogSeverity.Debug, context, message, ex);

    /// <inheritdoc cref="Debug(Exception?,string,string)"/>
    public static void Debug(string context, string message = "")
        => Log(LogSeverity.Debug, context, message, null);

    /// <summary>Unexpected but recoverable failure — shown in the status bar.</summary>
    public static void Warn(Exception? ex, string context, string message = "")
        => Log(LogSeverity.Warn, context, message, ex);

    /// <summary>Significant failure that impacts functionality — shown persistently in the status bar.</summary>
    public static void Error(Exception? ex, string context, string message = "")
        => Log(LogSeverity.Error, context, message, ex);

    /// <summary>Opens the log file in the system default text editor.</summary>
    public static void OpenLogFile()
    {
        try
        {
            if (File.Exists(_logPath))
                Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn(ex, nameof(OpenLogFile),
                "Could not open the application log.");
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static void Log(LogSeverity severity, string context, string message, Exception? ex)
    {
        var entry = new LogEntry(severity, context, message, ex);

        // Always write to the VS Output window
        System.Diagnostics.Debug.WriteLine(entry.ToString());

        // Append to the rolling log file — best-effort; file I/O must never propagate
        try
        {
            Directory.CreateDirectory(_logDir);

            // Cap the file at ~512 KB by dropping the first half once exceeded
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 512 * 1024)
            {
                var lines = File.ReadAllLines(_logPath);
                File.WriteAllLines(_logPath, lines[(lines.Length / 2)..]);
            }

            File.AppendAllText(_logPath, entry + Environment.NewLine);
        }
        catch (Exception ioEx)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AppLog file write failed: {ioEx}");
        }

        // Notify UI subscribers for Warn and above
        if (severity >= LogSeverity.Warn)
            MessageLogged?.Invoke(entry);
    }
}
