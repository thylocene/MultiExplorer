using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiExplorer;

internal enum FileOperationKind
{
    Copy,
    Move,
    Delete,
    DeletePermanently,
}

internal enum FileOperationStatus
{
    Queued,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled,
    Paused,
}

internal sealed class FileOperationRequest
{
    public Guid Id { get; set; }
    public FileOperationKind Kind { get; set; }
    public string[] Sources { get; set; } = [];
    public string? Destination { get; set; }
    public OperationWindowPlacement? WindowPlacement { get; set; }
    public DateTime CreatedUtc { get; set; }
}

internal sealed class OperationWindowPlacement
{
    public int AnchorLeft { get; set; }
    public int AnchorTop { get; set; }
    public int AnchorWidth { get; set; }
    public int AnchorHeight { get; set; }
    public int WorkAreaLeft { get; set; }
    public int WorkAreaTop { get; set; }
    public int WorkAreaWidth { get; set; }
    public int WorkAreaHeight { get; set; }

    internal bool TryCalculateLocation(
        int windowWidth, int windowHeight, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (WorkAreaWidth <= 0 || WorkAreaHeight <= 0
            || windowWidth <= 0 || windowHeight <= 0)
            return false;

        long anchorLeft = AnchorWidth > 0 ? AnchorLeft : WorkAreaLeft;
        long anchorTop = AnchorHeight > 0 ? AnchorTop : WorkAreaTop;
        long anchorWidth = AnchorWidth > 0 ? AnchorWidth : WorkAreaWidth;
        long anchorHeight = AnchorHeight > 0 ? AnchorHeight : WorkAreaHeight;
        long desiredX = anchorLeft + (anchorWidth - windowWidth) / 2;
        long desiredY = anchorTop + (anchorHeight - windowHeight) / 2;
        long maximumX = (long)WorkAreaLeft
            + Math.Max(0L, (long)WorkAreaWidth - windowWidth);
        long maximumY = (long)WorkAreaTop
            + Math.Max(0L, (long)WorkAreaHeight - windowHeight);

        x = (int)Math.Clamp(desiredX, (long)WorkAreaLeft, maximumX);
        y = (int)Math.Clamp(desiredY, (long)WorkAreaTop, maximumY);
        return true;
    }
}

internal sealed class FileOperationState
{
    public Guid Id { get; set; }
    public FileOperationKind Kind { get; set; }
    public FileOperationStatus Status { get; set; }
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public ulong TotalWork { get; set; }
    public ulong WorkCompleted { get; set; }
    public ulong TotalBytes { get; set; }
    public ulong BytesCompleted { get; set; }
    public string? SourceDisplayName { get; set; }
    public string? DestinationDisplayName { get; set; }
    public string? CurrentItem { get; set; }
    public string? Error { get; set; }
    public int Result { get; set; }
    public bool Aborted { get; set; }
    public int HostProcessId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public bool IsTerminal => Status is FileOperationStatus.Completed
        or FileOperationStatus.Failed or FileOperationStatus.Cancelled;
}

internal static class FileOperationPresentation
{
    internal static void ApplyRequestContext(
        FileOperationState state, FileOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        state.SourceDisplayName = GetSourceLocationName(request.Sources);
        state.DestinationDisplayName = string.IsNullOrWhiteSpace(request.Destination)
            ? null
            : GetPathDisplayName(request.Destination);
    }

    internal static string GetSourceLocationName(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        string[] parents = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(path)))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        return parents.Length switch
        {
            0 => "selected location",
            1 => GetPathDisplayName(parents[0]),
            _ => "multiple locations",
        };
    }

    internal static string GetPathDisplayName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string trimmedPath = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}

internal static class OperationProgressMath
{
    internal static double[] SmoothSamples(
        IReadOnlyList<double> samples,
        int radius)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegative(radius);

        var smoothed = new double[samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            int firstSample = Math.Max(0, index - radius);
            int lastSample = Math.Min(samples.Count - 1, index + radius);
            double weightedTotal = 0;
            int totalWeight = 0;
            for (int sampleIndex = firstSample;
                 sampleIndex <= lastSample;
                 sampleIndex++)
            {
                int weight = radius + 1 - Math.Abs(sampleIndex - index);
                weightedTotal += samples[sampleIndex] * weight;
                totalWeight += weight;
            }

            smoothed[index] = totalWeight == 0
                ? samples[index]
                : weightedTotal / totalWeight;
        }

        return smoothed;
    }

    internal static int CalculatePercentage(FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        double ratio = state.TotalWork > 0
            ? (double)state.WorkCompleted / state.TotalWork
            : state.TotalItems > 0
                ? (double)state.CompletedItems / state.TotalItems
                : 0;
        return (int)Math.Clamp(Math.Round(ratio * 100), 0, 100);
    }

    internal static ulong EstimateCompletedBytes(
        ulong totalBytes, ulong totalWork, ulong workCompleted)
    {
        if (totalBytes == 0 || totalWork == 0 || workCompleted == 0)
            return 0;
        if (workCompleted >= totalWork)
            return totalBytes;

        return (ulong)Math.Clamp(
            Math.Round((double)totalBytes * workCompleted / totalWork),
            0,
            totalBytes);
    }

    internal static void ApplyMonotonicWorkProgress(
        FileOperationState state, ulong totalWork, ulong workCompleted)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (totalWork == 0)
            return;

        workCompleted = Math.Min(workCompleted, totalWork);
        double nextRatio = (double)workCompleted / totalWork;
        double currentRatio = state.TotalWork > 0
            ? (double)state.WorkCompleted / state.TotalWork
            : 0;
        if (state.TotalWork == 0 || nextRatio >= currentRatio)
        {
            state.TotalWork = totalWork;
            state.WorkCompleted = workCompleted;
        }

        state.BytesCompleted = Math.Max(
            state.BytesCompleted,
            EstimateCompletedBytes(state.TotalBytes, totalWork, workCompleted));
    }
}

internal static class FileOperationStore
{
    internal const string DirectoryOverrideEnvironmentVariable =
        "MULTIEXPLORER_OPERATION_DIR";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    internal static string DirectoryPath
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(
                DirectoryOverrideEnvironmentVariable);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MultiExplorer", "Operations")
                : Path.GetFullPath(overridden);
        }
    }

    internal static string RequestPath(Guid id) =>
        Path.Combine(DirectoryPath, $"{id:N}.request.json");

    internal static string StatePath(Guid id) =>
        Path.Combine(DirectoryPath, $"{id:N}.state.json");

    internal static string CancelPath(Guid id) =>
        Path.Combine(DirectoryPath, $"{id:N}.cancel");

    internal static string PausePath(Guid id) =>
        Path.Combine(DirectoryPath, $"{id:N}.pause");

    internal static string BrokerMutexName
    {
        get
        {
            string normalizedDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(DirectoryPath)).ToUpperInvariant();
            string identifier = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(normalizedDirectory)))[..24];
            return $@"Local\MultiExplorer.OperationHost.{identifier}";
        }
    }

    internal static void WriteRequest(FileOperationRequest request)
    {
        Directory.CreateDirectory(DirectoryPath);
        WriteJsonAtomically(RequestPath(request.Id), request);
    }

    internal static FileOperationRequest ReadRequest(string path) =>
        JsonSerializer.Deserialize<FileOperationRequest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("The file-operation request is empty.");

    internal static void WriteState(FileOperationState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        WriteJsonAtomically(StatePath(state.Id), state);
    }

    internal static FileOperationState? TryReadState(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<FileOperationState>(
                File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException)
        {
            return null;
        }
    }

    internal static void RequestCancellation(Guid id)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(CancelPath(id), string.Empty);
    }

    internal static bool IsCancellationRequested(Guid id) =>
        File.Exists(CancelPath(id));

    internal static void RequestPause(Guid id)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(PausePath(id), string.Empty);
    }

    internal static void RequestResume(Guid id) =>
        TryDeleteFile(PausePath(id));

    internal static bool IsPauseRequested(Guid id) =>
        File.Exists(PausePath(id));

    /// <summary>Removes the short-lived command and cancellation files for a finished operation.</summary>
    internal static void RemoveTransientArtifacts(Guid id)
    {
        TryDeleteFile(RequestPath(id));
        TryDeleteFile(CancelPath(id));
        TryDeleteFile(PausePath(id));
    }

    /// <summary>Removes a retained status record once its retention period has elapsed.</summary>
    internal static void RemoveState(Guid id) => TryDeleteFile(StatePath(id));

    internal static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Could not delete MultiExplorer operation artifact '{path}': {ex}");
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string temporaryPath = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
