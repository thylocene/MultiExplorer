namespace MultiExplorer;

internal enum FolderDifferenceKind
{
    OnlyLeft,
    OnlyRight,
    LeftNewer,
    RightNewer,
    DifferentSize,
    TypeConflict,
    ReparsePoint,
}

internal enum FolderSyncAction
{
    Skip,
    CopyLeftToRight,
    CopyRightToLeft,
    DeleteLeft,
    DeleteRight,
}

internal enum FileTimestampOrder
{
    Similar,
    LeftNewer,
    RightNewer,
}

internal sealed record FolderItemSnapshot(
    string RelativePath,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed record FolderDifference(
    string RelativePath,
    FolderDifferenceKind Kind,
    FolderItemSnapshot? Left,
    FolderItemSnapshot? Right)
{
    internal FolderSyncAction SuggestedAction => Kind switch
    {
        FolderDifferenceKind.OnlyLeft or FolderDifferenceKind.LeftNewer =>
            FolderSyncAction.CopyLeftToRight,
        FolderDifferenceKind.OnlyRight or FolderDifferenceKind.RightNewer =>
            FolderSyncAction.CopyRightToLeft,
        FolderDifferenceKind.DifferentSize => SuggestDifferentSizeAction(),
        _ => FolderSyncAction.Skip,
    };

    private FolderSyncAction SuggestDifferentSizeAction()
    {
        if (Left is not { IsDirectory: false, IsReparsePoint: false } left
            || Right is not { IsDirectory: false, IsReparsePoint: false } right)
            return FolderSyncAction.Skip;

        return FolderComparisonService.CompareModifiedTimes(left, right) switch
        {
            FileTimestampOrder.LeftNewer => FolderSyncAction.CopyLeftToRight,
            FileTimestampOrder.RightNewer => FolderSyncAction.CopyRightToLeft,
            _ => FolderSyncAction.Skip,
        };
    }

    internal bool Allows(FolderSyncAction action) => action switch
    {
        FolderSyncAction.Skip => true,
        FolderSyncAction.CopyLeftToRight => Left is not null
            && Kind is not (FolderDifferenceKind.TypeConflict
                or FolderDifferenceKind.ReparsePoint),
        FolderSyncAction.CopyRightToLeft => Right is not null
            && Kind is not (FolderDifferenceKind.TypeConflict
                or FolderDifferenceKind.ReparsePoint),
        FolderSyncAction.DeleteLeft => Left is not null
            && Kind is not (FolderDifferenceKind.TypeConflict
                or FolderDifferenceKind.ReparsePoint),
        FolderSyncAction.DeleteRight => Right is not null
            && Kind is not (FolderDifferenceKind.TypeConflict
                or FolderDifferenceKind.ReparsePoint),
        _ => false,
    };
}

internal sealed record FolderComparisonResult(
    string LeftRoot,
    string RightRoot,
    IReadOnlyList<FolderDifference> Differences,
    IReadOnlyDictionary<string, FolderItemSnapshot> LeftItems,
    IReadOnlyDictionary<string, FolderItemSnapshot> RightItems);

internal sealed record FolderSyncSelection(
    FolderDifference Difference,
    FolderSyncAction Action);

internal sealed record FolderSyncPlanItem(
    FolderSyncAction Action,
    string SourcePath,
    string? DestinationDirectory,
    string TargetPath);

internal static class FolderComparisonService
{
    private const int MaximumEntriesPerSide = 50000;
    private const int MaximumDifferences = 10000;
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    internal static FileTimestampOrder CompareModifiedTimes(
        FolderItemSnapshot left, FolderItemSnapshot right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        TimeSpan difference = left.LastWriteTimeUtc - right.LastWriteTimeUtc;
        if (difference > TimestampTolerance) return FileTimestampOrder.LeftNewer;
        if (difference < -TimestampTolerance) return FileTimestampOrder.RightNewer;
        return FileTimestampOrder.Similar;
    }

    internal static async Task<FolderComparisonResult> CompareAsync(
        string leftRoot, string rightRoot, CancellationToken cancellationToken = default)
    {
        (string left, string right) = NormalizeRoots(leftRoot, rightRoot);
        return await Task.Run(() => CompareCore(left, right, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<(FolderItemSnapshot Left, FolderItemSnapshot Right)>
        GetMatchingFiles(FolderComparisonResult comparison, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        var differences = comparison.Differences
            .Select(static difference => difference.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return comparison.LeftItems.Values
            .Where(item => !item.IsDirectory && !item.IsReparsePoint
                && !differences.Contains(item.RelativePath)
                && comparison.RightItems.TryGetValue(item.RelativePath,
                    out FolderItemSnapshot? right)
                && right is { IsDirectory: false, IsReparsePoint: false })
            .OrderBy(static item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase)
            .Take(maximumCount)
            .Select(item => (item, comparison.RightItems[item.RelativePath]))
            .ToArray();
    }

    internal static IReadOnlyList<FolderSyncPlanItem> BuildPlan(
        FolderComparisonResult comparison,
        IEnumerable<FolderSyncSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(selections);

        var plan = new List<FolderSyncPlanItem>();
        foreach (FolderSyncSelection selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (selection.Action == FolderSyncAction.Skip) continue;
            if (!comparison.Differences.Contains(selection.Difference)
                || !selection.Difference.Allows(selection.Action))
                throw new ArgumentException("The comparison does not allow a selected action.",
                    nameof(selections));

            bool sourceIsLeft = selection.Action is FolderSyncAction.CopyLeftToRight
                or FolderSyncAction.DeleteLeft;
            string sourceRoot = sourceIsLeft ? comparison.LeftRoot : comparison.RightRoot;
            string otherRoot = sourceIsLeft ? comparison.RightRoot : comparison.LeftRoot;
            string sourcePath = CombineUnderRoot(sourceRoot,
                selection.Difference.RelativePath);
            string targetPath = selection.Action is FolderSyncAction.DeleteLeft
                or FolderSyncAction.DeleteRight
                ? sourcePath
                : CombineUnderRoot(otherRoot, selection.Difference.RelativePath);
            string? destinationDirectory = selection.Action is FolderSyncAction.CopyLeftToRight
                or FolderSyncAction.CopyRightToLeft
                ? Path.GetDirectoryName(targetPath)
                : null;
            plan.Add(new FolderSyncPlanItem(selection.Action, sourcePath,
                destinationDirectory, targetPath));
        }
        return plan;
    }

    internal static async Task ValidatePlanAsync(
        FolderComparisonResult comparison,
        IReadOnlyList<FolderSyncSelection> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(selections);
        await Task.Run(() =>
        {
            foreach (FolderSyncSelection selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selection.Action == FolderSyncAction.Skip) continue;
                FolderDifference difference = selection.Difference;
                ValidateAncestorDirectories(comparison.LeftRoot,
                    difference.RelativePath);
                ValidateAncestorDirectories(comparison.RightRoot,
                    difference.RelativePath);
                string leftPath = CombineUnderRoot(comparison.LeftRoot,
                    difference.RelativePath);
                string rightPath = CombineUnderRoot(comparison.RightRoot,
                    difference.RelativePath);
                if (!SnapshotsMatch(difference.Left, ReadSnapshot(leftPath,
                        difference.RelativePath))
                    || !SnapshotsMatch(difference.Right, ReadSnapshot(rightPath,
                        difference.RelativePath)))
                    throw new InvalidOperationException(
                        $"'{difference.RelativePath}' changed since the comparison. Compare again before applying changes.");
                if (difference.Left?.IsDirectory == true)
                    ValidateDirectoryContents(leftPath, difference.RelativePath,
                        comparison.LeftItems, cancellationToken);
                if (difference.Right?.IsDirectory == true)
                    ValidateDirectoryContents(rightPath, difference.RelativePath,
                        comparison.RightItems, cancellationToken);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static FolderComparisonResult CompareCore(
        string leftRoot, string rightRoot, CancellationToken cancellationToken)
    {
        EnsureDirectoryWithoutReparsePoint(leftRoot);
        EnsureDirectoryWithoutReparsePoint(rightRoot);
        Dictionary<string, FolderItemSnapshot> left = Scan(leftRoot, cancellationToken);
        Dictionary<string, FolderItemSnapshot> right = Scan(rightRoot, cancellationToken);
        var differences = new List<FolderDifference>();
        var hiddenSubtrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string relativePath in left.Keys.Concat(right.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasHiddenAncestor(relativePath, hiddenSubtrees))
                continue;

            left.TryGetValue(relativePath, out FolderItemSnapshot? leftItem);
            right.TryGetValue(relativePath, out FolderItemSnapshot? rightItem);
            FolderDifferenceKind? kind = Classify(leftItem, rightItem);
            if (kind is null) continue;

            differences.Add(new FolderDifference(relativePath, kind.Value,
                leftItem, rightItem));
            if (differences.Count > MaximumDifferences)
                throw new IOException(
                    $"More than {MaximumDifferences:N0} differences were found. Compare a smaller folder.");
            if (leftItem?.IsDirectory == true || rightItem?.IsDirectory == true)
                hiddenSubtrees.Add(relativePath);
        }

        return new FolderComparisonResult(leftRoot, rightRoot, differences,
            left, right);
    }

    private static bool HasHiddenAncestor(
        string relativePath, HashSet<string> hiddenSubtrees)
    {
        for (string? parent = Path.GetDirectoryName(relativePath);
             !string.IsNullOrEmpty(parent);
             parent = Path.GetDirectoryName(parent))
        {
            if (hiddenSubtrees.Contains(parent)) return true;
        }
        return false;
    }

    private static FolderDifferenceKind? Classify(
        FolderItemSnapshot? left, FolderItemSnapshot? right)
    {
        if (left?.IsReparsePoint == true || right?.IsReparsePoint == true)
            return FolderDifferenceKind.ReparsePoint;
        if (left is null) return FolderDifferenceKind.OnlyRight;
        if (right is null) return FolderDifferenceKind.OnlyLeft;
        if (left.IsDirectory != right.IsDirectory)
            return FolderDifferenceKind.TypeConflict;
        if (left.IsDirectory) return null;
        if (left.Length != right.Length)
            return FolderDifferenceKind.DifferentSize;
        return CompareModifiedTimes(left, right) switch
        {
            FileTimestampOrder.LeftNewer => FolderDifferenceKind.LeftNewer,
            FileTimestampOrder.RightNewer => FolderDifferenceKind.RightNewer,
            _ => null,
        };
    }

    private static Dictionary<string, FolderItemSnapshot> Scan(
        string root, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, FolderItemSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.GetRelativePath(root, path);
                FolderItemSnapshot snapshot = ReadSnapshot(path, relativePath)
                    ?? throw new IOException($"'{path}' disappeared during the comparison.");
                items.Add(relativePath, snapshot);
                if (items.Count > MaximumEntriesPerSide)
                    throw new IOException(
                        $"'{root}' contains more than {MaximumEntriesPerSide:N0} items. Compare a smaller folder.");
                if (snapshot.IsDirectory && !snapshot.IsReparsePoint)
                    pending.Push(path);
            }
        }
        return items;
    }

    private static FolderItemSnapshot? ReadSnapshot(string path, string relativePath)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            bool isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
            return new FolderItemSnapshot(relativePath, isDirectory,
                isReparsePoint,
                isDirectory || isReparsePoint ? 0 : new FileInfo(path).Length,
                isDirectory || isReparsePoint
                    ? DateTime.MinValue : File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                   or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool SnapshotsMatch(
        FolderItemSnapshot? expected, FolderItemSnapshot? current) =>
        expected is null ? current is null
            : current is not null
              && expected.IsDirectory == current.IsDirectory
              && expected.IsReparsePoint == current.IsReparsePoint
              && (expected.IsDirectory || expected.Length == current.Length
                  && expected.LastWriteTimeUtc == current.LastWriteTimeUtc);

    private static void ValidateDirectoryContents(
        string directory, string relativePath,
        IReadOnlyDictionary<string, FolderItemSnapshot> originalItems,
        CancellationToken cancellationToken)
    {
        Dictionary<string, FolderItemSnapshot> currentItems = Scan(directory,
            cancellationToken);
        string prefix = relativePath + Path.DirectorySeparatorChar;
        int expectedCount = 0;
        foreach ((string path, FolderItemSnapshot expected) in originalItems)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            expectedCount++;
            string childPath = path[prefix.Length..];
            if (!currentItems.TryGetValue(childPath, out FolderItemSnapshot? current)
                || !SnapshotsMatch(expected, current))
                throw new InvalidOperationException(
                    $"'{relativePath}' changed since the comparison. Compare again before applying changes.");
        }
        if (expectedCount != currentItems.Count)
            throw new InvalidOperationException(
                $"'{relativePath}' changed since the comparison. Compare again before applying changes.");
    }

    private static void ValidateAncestorDirectories(string root, string relativePath)
    {
        EnsureDirectoryWithoutReparsePoint(root);
        string? parent = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(parent)) return;
        string current = root;
        foreach (string part in parent.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            EnsureDirectoryWithoutReparsePoint(current);
        }
    }

    private static void EnsureDirectoryWithoutReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(
                $"'{path}' must be a normal folder to compare or synchronize it.");
    }

    private static (string Left, string Right) NormalizeRoots(
        string leftRoot, string rightRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightRoot);
        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(leftRoot));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rightRoot));
        if (IsSameOrChild(left, right) || IsSameOrChild(right, left))
            throw new ArgumentException(
                "Choose separate folders that are not inside one another.");
        return (left, right);
    }

    private static bool IsSameOrChild(string candidate, string parent) =>
        candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string CombineUnderRoot(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsSameOrChild(path, root)
            || path.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{relativePath}' is outside the compared folder.");
        return path;
    }
}
