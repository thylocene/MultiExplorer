namespace MultiExplorer;

/// <summary>
/// Tracks files placed on the clipboard with the Move drop effect so every
/// visible pane can render the same translucent cut-item cue as File Explorer.
/// </summary>
internal static class ClipboardCutState
{
    private static readonly object SyncRoot = new();
    private static HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    internal static event EventHandler? Changed;

    internal static bool Contains(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (SyncRoot)
            return _paths.Contains(path);
    }

    internal static void SetPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var nextPaths = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool changed;
        lock (SyncRoot)
        {
            changed = !_paths.SetEquals(nextPaths);
            if (changed) _paths = nextPaths;
        }

        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void Clear() => SetPaths([]);
}
