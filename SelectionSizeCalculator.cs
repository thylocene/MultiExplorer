using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MultiExplorer;

internal readonly record struct SelectedSizeResult(
    long Bytes,
    bool IsComplete,
    int SelectedFileCount,
    int SelectedFolderCount);

internal static class SelectionSizeCalculator
{
    private const long Kilobyte = 1_000;
    private const long Megabyte = 1_000_000;
    private const long Gigabyte = 1_000_000_000;
    private const long Terabyte = 1_000_000_000_000;
    private const long Petabyte = 1_000_000_000_000_000;
    private const long Exabyte = 1_000_000_000_000_000_000;

    internal static SelectedSizeResult Calculate(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        long totalBytes = 0;
        bool isComplete = true;
        int selectedFileCount = 0;
        int selectedFolderCount = 0;
        var pendingDirectories = new Stack<DirectoryInfo>();
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) || ExplorerHost.IsNetworkPath(path))
            {
                isComplete = false;
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (IsRecoverableFileSystemException(ex))
            {
                isComplete = false;
                continue;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                selectedFileCount++;
                AddFileLength(new FileInfo(path), ref totalBytes, ref isComplete);
                continue;
            }

            selectedFolderCount++;
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                isComplete = false;
                continue;
            }

            pendingDirectories.Push(new DirectoryInfo(path));
        }

        while (pendingDirectories.TryPop(out DirectoryInfo? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos(
                             "*", enumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileAttributes attributes;
                    try
                    {
                        attributes = item.Attributes;
                    }
                    catch (Exception ex) when (IsRecoverableFileSystemException(ex))
                    {
                        isComplete = false;
                        continue;
                    }

                    if (!attributes.HasFlag(FileAttributes.Directory))
                    {
                        AddFileLength((FileInfo)item, ref totalBytes, ref isComplete);
                    }
                    else if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Junctions and directory links can leave the selected tree,
                        // point to a network share, or form a cycle.
                        isComplete = false;
                    }
                    else
                    {
                        pendingDirectories.Push((DirectoryInfo)item);
                    }
                }
            }
            catch (Exception ex) when (IsRecoverableFileSystemException(ex))
            {
                isComplete = false;
            }
        }

        return new SelectedSizeResult(
            totalBytes,
            isComplete,
            selectedFileCount,
            selectedFolderCount);
    }

    internal static string FormatSelection(SelectedSizeResult result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(result.Bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(result.SelectedFileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(result.SelectedFolderCount);

        string selectedItems = (result.SelectedFileCount, result.SelectedFolderCount) switch
        {
            (0, 0) => "0 files",
            (var files, 0) => FormatCount(files, "file", "files"),
            (0, var folders) => FormatCount(folders, "folder", "folders"),
            (var files, var folders) =>
                $"{FormatCount(files, "file", "files")}, " +
                FormatCount(folders, "folder", "folders"),
        };
        string size = FormatBytes(result.Bytes);
        return result.IsComplete
            ? $"Selected: {selectedItems} ({size})"
            : $"Selected: {selectedItems} (at least {size})";
    }

    internal static string FormatBytes(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        return bytes switch
        {
            < Kilobyte => $"{bytes} B",
            < Megabyte => $"{bytes / (double)Kilobyte:F2} KB",
            < Gigabyte => $"{bytes / (double)Megabyte:F2} MB",
            < Terabyte => $"{bytes / (double)Gigabyte:F2} GB",
            < Petabyte => $"{bytes / (double)Terabyte:F2} TB",
            < Exabyte => $"{bytes / (double)Petabyte:F2} PB",
            _ => $"{bytes / (double)Exabyte:F2} EB",
        };
    }

    private static void AddFileLength(
        FileInfo file,
        ref long totalBytes,
        ref bool isComplete)
    {
        try
        {
            long length = file.Length;
            if (length > long.MaxValue - totalBytes)
            {
                totalBytes = long.MaxValue;
                isComplete = false;
                return;
            }

            totalBytes += length;
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            isComplete = false;
        }
    }

    private static string FormatCount(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    private static bool IsRecoverableFileSystemException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException;
}
