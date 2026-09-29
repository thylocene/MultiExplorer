namespace MultiExplorer;

internal readonly record struct FileOperationSourceMetrics(
    int ItemCount,
    ulong TotalBytes,
    bool IsComplete);

internal static class FileOperationMetrics
{
    internal static FileOperationSourceMetrics Calculate(
        FileOperationRequest request,
        Func<bool> cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(cancellationRequested);

        FileOperationSourceMetrics fallback = CreateFallback(request.Sources);
        if (request.Sources.Length == 0)
            return new FileOperationSourceMetrics(0, 0, IsComplete: true);

        return ShouldRecursivelyMeasure(request)
            ? CalculateRecursively(
                request.Sources,
                cancellationRequested,
                fallback)
            : fallback;
    }

    internal static bool ShouldRecursivelyMeasure(FileOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Sources.Length == 0)
            return false;

        return request.Kind switch
        {
            FileOperationKind.Copy =>
                !string.IsNullOrWhiteSpace(request.Destination),
            FileOperationKind.Move =>
                !string.IsNullOrWhiteSpace(request.Destination)
                && !AreAllSourcesOnDestinationVolume(
                    request.Sources, request.Destination),
            _ => false,
        };
    }

    internal static bool AreAllSourcesOnDestinationVolume(
        IReadOnlyCollection<string> sourcePaths,
        string? destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0 || string.IsNullOrWhiteSpace(destinationPath))
            return false;

        string? destinationRoot = GetNormalizedRoot(destinationPath);
        return destinationRoot is not null
            && sourcePaths.All(path => string.Equals(
                GetNormalizedRoot(path),
                destinationRoot,
                StringComparison.OrdinalIgnoreCase));
    }

    private static FileOperationSourceMetrics CalculateRecursively(
        IEnumerable<string> sourcePaths,
        Func<bool> cancellationRequested,
        FileOperationSourceMetrics fallback)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        int itemCount = 0;
        ulong totalBytes = 0;

        foreach (string sourcePath in sourcePaths)
        {
            if (cancellationRequested())
                return fallback;

            try
            {
                var file = new FileInfo(sourcePath);
                if (file.Exists)
                {
                    itemCount = AddItem(itemCount);
                    totalBytes = AddFileLength(totalBytes, file);
                    continue;
                }

                var directory = new DirectoryInfo(sourcePath);
                if (!directory.Exists)
                    return fallback;

                itemCount = AddItem(itemCount);
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos(
                             "*", enumerationOptions))
                {
                    if (cancellationRequested())
                        return fallback;
                    itemCount = AddItem(itemCount);
                    if (entry is FileInfo childFile)
                        totalBytes = AddFileLength(totalBytes, childFile);
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                return fallback;
            }
        }

        return new FileOperationSourceMetrics(
            itemCount, totalBytes, IsComplete: true);
    }

    private static FileOperationSourceMetrics CreateFallback(
        IReadOnlyCollection<string> sourcePaths) =>
        new(sourcePaths.Count, 0, IsComplete: false);

    private static int AddItem(int itemCount) =>
        itemCount == int.MaxValue ? int.MaxValue : itemCount + 1;

    private static ulong AddFileLength(ulong totalBytes, FileInfo file)
    {
        long length = file.Length;
        ulong positiveLength = length > 0 ? (ulong)length : 0;
        return ulong.MaxValue - totalBytes < positiveLength
            ? ulong.MaxValue
            : totalBytes + positiveLength;
    }

    private static string? GetNormalizedRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return null;

            root = root.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            if (root.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                root = @"\\" + root[8..];
            else if (root.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                root = root[4..];
            return root.TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;
}
