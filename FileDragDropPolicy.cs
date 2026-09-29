using System;
using System.Collections.Generic;
using System.IO;

namespace MultiExplorer;

internal static class FileDragDropPolicy
{
    internal static bool ShouldMoveByDefault(
        IReadOnlyList<string> sourcePaths, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (sourcePaths.Count == 0) return false;

        // Network paths should behave like local-to-local drags: move unless
        // the user holds Ctrl to request a copy. This also covers mapped drives.
        if (ExplorerHost.IsNetworkPath(destinationPath))
            return true;

        string? destinationRoot = Path.GetPathRoot(destinationPath);
        bool sameVolume = destinationRoot is not null;
        var inspectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in sourcePaths)
        {
            string? sourceRoot = Path.GetPathRoot(sourcePath);
            sameVolume &= string.Equals(sourceRoot, destinationRoot,
                StringComparison.OrdinalIgnoreCase);

            // Avoid querying the drive type again for every selected item on
            // the same drive while OLE calls DragOver repeatedly.
            if (sourceRoot is not null
                && inspectedRoots.Add(sourceRoot)
                && ExplorerHost.IsNetworkPath(sourceRoot))
                return true;
        }

        return sameVolume;
    }
}
