namespace MultiExplorer;

/// <summary>
/// Bounds the time the UI waits for a network directory check. Windows file-system
/// calls cannot be cancelled once a network provider has entered them, so the
/// check runs on a worker thread and its result is ignored after the timeout.
/// </summary>
internal static class NetworkPathAvailability
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    internal static async Task<bool> CheckAsync(
        string path,
        CancellationToken cancellationToken = default,
        Func<string, bool>? directoryExists = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan effectiveTimeout = timeout ?? ProbeTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        directoryExists ??= Directory.Exists;
        try
        {
            Task<bool> check = Task.Run(
                () => directoryExists(path), cancellationToken);
            return await check.WaitAsync(effectiveTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            AppLog.Debug(ex, nameof(NetworkPathAvailability),
                $"Could not check network location '{path}'.");
            return false;
        }
    }
}
