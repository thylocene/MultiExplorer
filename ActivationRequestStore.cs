using System.Text;

namespace MultiExplorer;

/// <summary>
/// Passes a folder from a newly activated Start tile to the already-running
/// MultiExplorer instance before the second process exits.
/// </summary>
internal static class ActivationRequestStore
{
    private static readonly string RequestDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiExplorer");
    private static readonly string RequestPath = Path.Combine(
        RequestDirectory, "pending-folder-activation.txt");

    internal static bool TryWriteFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) return false;

        string temporaryPath = Path.Combine(RequestDirectory,
            $"pending-folder-activation-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(RequestDirectory);
            File.WriteAllText(temporaryPath, fullPath, Encoding.UTF8);
            File.Move(temporaryPath, RequestPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            AppLog.Warn(ex, nameof(TryWriteFolder),
                "Could not pass the requested folder to the running window.");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug(ex, nameof(TryWriteFolder),
                    "Could not remove the temporary activation request.");
            }
        }
    }

    internal static string? TryTakeFolder()
    {
        try
        {
            if (!File.Exists(RequestPath)) return null;
            string path = File.ReadAllText(RequestPath, Encoding.UTF8);
            File.Delete(RequestPath);
            string fullPath = Path.GetFullPath(path);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            AppLog.Warn(ex, nameof(TryTakeFolder),
                "Could not read the pending Start tile request.");
            return null;
        }
    }
}
