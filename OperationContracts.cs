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

internal static class OperationWindowPromotionPolicy
{
    internal static bool RequiresPromotion(bool wasPromoted, bool isTopMost) =>
        !wasPromoted || !isTopMost;

    internal static bool ShouldReassertProgressWindow(
        bool visible,
        bool hasVisibleOwnedPopup) =>
        visible && !hasVisibleOwnedPopup;
}

internal sealed class FileOperationState
{
    public Guid Id { get; set; }
    public FileOperationKind Kind { get; set; }
    public FileOperationStatus Status { get; set; }
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public int ProcessedItems { get; set; }
    public bool ItemCountIsComplete { get; set; }
    public ulong TotalWork { get; set; }
    public ulong WorkCompleted { get; set; }
    public ulong TotalBytes { get; set; }
    public ulong BytesCompleted { get; set; }
    public string? SourceDisplayName { get; set; }
    public string? DestinationDisplayName { get; set; }
    public string? CurrentItem { get; set; }
    public string? Error { get; set; }
    public string? FailureStage { get; set; }
    public string? FailureExceptionType { get; set; }
    public string? Warning { get; set; }
    public int StateWriteRetries { get; set; }
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

    internal static string CreateCleanupWarningMessage(FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string action = state.Kind switch
        {
            FileOperationKind.Copy => "copy",
            FileOperationKind.Move => "move",
            FileOperationKind.Delete => "delete",
            FileOperationKind.DeletePermanently => "permanent delete",
            _ => "file",
        };
        return $"A Windows cleanup issue occurred after the {action} operation. "
            + "See app.log for details.";
    }
}

internal static class FileOperationResultClassifier
{
    private const int EAbort = unchecked((int)0x80004004);
    private const int ErrorCancelled = unchecked((int)0x800704C7);
    private const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    internal static bool IsCancellation(int result) => result is EAbort
        or ErrorCancelled or CopyEngineUserCancelled;
}

internal static class ShellFileOperationOptions
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmMkdir = 0x0200;
    private const uint FofxRecycleOnDelete = 0x00080000;

    internal static uint CreateFlags(
        FileOperationKind kind,
        bool showNativeProgressDialog)
    {
        uint flags = FofNoConfirmMkdir;
        if (!showNativeProgressDialog)
            flags |= FofSilent;
        if (kind == FileOperationKind.DeletePermanently)
            flags |= FofNoConfirmation;
        if (kind != FileOperationKind.DeletePermanently)
            flags |= FofAllowUndo;
        if (kind == FileOperationKind.Delete)
            flags |= FofxRecycleOnDelete;
        return flags;
    }
}

internal static class FileOperationFailurePresentation
{
    internal static string CreateMessage(
        FileOperationState state,
        FileOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        string action = state.Kind switch
        {
            FileOperationKind.Copy => "copying",
            FileOperationKind.Move => "moving",
            FileOperationKind.Delete => "deleting",
            FileOperationKind.DeletePermanently => "permanently deleting",
            _ => "processing",
        };
        var message = new StringBuilder()
            .Append("MultiExplorer could not finish ")
            .Append(action)
            .AppendLine(" the selected items.")
            .AppendLine();

        if (request.Sources.Length == 1)
            message.Append("Source: ").AppendLine(request.Sources[0]);
        else
            message.Append("Source: ")
                .Append(request.Sources.Length)
                .AppendLine(" selected items");

        if (!string.IsNullOrWhiteSpace(request.Destination))
            message.Append("Destination: ").AppendLine(request.Destination);
        if (!string.IsNullOrWhiteSpace(state.CurrentItem))
            message.Append("Item: ").AppendLine(state.CurrentItem);

        message.AppendLine()
            .Append("Problem: ")
            .AppendLine(string.IsNullOrWhiteSpace(state.Error)
                ? "Windows did not provide additional error details."
                : state.Error.Trim());
        if (!string.IsNullOrWhiteSpace(state.FailureStage))
            message.Append("Stage: ").AppendLine(state.FailureStage);
        if (!string.IsNullOrWhiteSpace(state.FailureExceptionType))
            message.Append("Exception: ").AppendLine(state.FailureExceptionType);
        if (state.Result != 0)
            message.Append("Windows error: 0x")
                .AppendLine(unchecked((uint)state.Result).ToString("X8"));

        if (IsNetworkPath(request.Destination)
            || request.Sources.Any(IsNetworkPath))
        {
            message.AppendLine()
                .AppendLine("Check that the network location is connected and available, "
                    + "and that you still have permission to access it. Then retry the operation.");
        }

        return message.ToString().TrimEnd();
    }

    internal static string CreateLogMessage(FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var message = new StringBuilder()
            .Append("A ")
            .Append(state.Kind.ToString().ToLowerInvariant())
            .Append(" operation failed");
        if (!string.IsNullOrWhiteSpace(state.CurrentItem))
            message.Append(" while processing \"")
                .Append(state.CurrentItem.Trim())
                .Append('"');

        message.Append(": ")
            .Append(string.IsNullOrWhiteSpace(state.Error)
                ? "Windows did not provide additional error details."
                : state.Error.Trim());
        if (!string.IsNullOrWhiteSpace(state.FailureStage))
            message.Append(" Stage: ").Append(state.FailureStage).Append('.');
        if (!string.IsNullOrWhiteSpace(state.FailureExceptionType))
            message.Append(" Exception: ").Append(state.FailureExceptionType).Append('.');
        if (state.Result != 0)
            message.Append(" Windows error: 0x")
                .Append(unchecked((uint)state.Result).ToString("X8"))
                .Append('.');

        return message.ToString();
    }

    internal static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root)
                && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal static class OperationProgressMath
{
    internal static int GetCompletedItemsForRate(FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Kind is FileOperationKind.Delete or FileOperationKind.DeletePermanently
            ? state.ProcessedItems
            : state.CompletedItems;
    }

    internal static int? GetRemainingItemCount(FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status == FileOperationStatus.Completed)
            return 0;
        if (!state.ItemCountIsComplete)
            return null;

        return Math.Max(0, state.TotalItems - state.CompletedItems);
    }

    internal static ulong? EstimateRemainingPermanentDeleteItems(
        FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Kind != FileOperationKind.DeletePermanently
            || state.ItemCountIsComplete
            || state.Status is not (FileOperationStatus.Running
                or FileOperationStatus.Paused or FileOperationStatus.Cancelling)
            || state.TotalWork <= state.WorkCompleted)
            return null;

        // Shell work is close to one unit per child during permanent deletion.
        // It is an estimate, so the UI must not present it as an exact count.
        return state.TotalWork - state.WorkCompleted;
    }

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
        if (state.Status == FileOperationStatus.Completed)
            return 100;

        double ratio = state.TotalWork > 0
            ? (double)state.WorkCompleted / state.TotalWork
            : state.ItemCountIsComplete && state.TotalItems > 0
                ? (double)state.CompletedItems / state.TotalItems
                : 0;
        return (int)Math.Clamp(Math.Round(ratio * 100), 0, 99);
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

internal sealed class OperationRateAverage
{
    private readonly long _windowMilliseconds;
    private readonly List<RateSample> _samples = [];
    private bool _hasProgress;

    internal OperationRateAverage(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _windowMilliseconds = checked((long)Math.Ceiling(window.TotalMilliseconds));
    }

    internal double? AddSample(long timestampMilliseconds, double cumulativeWork)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(cumulativeWork);

        if (_samples.Count > 0)
        {
            RateSample previous = _samples[^1];
            if (timestampMilliseconds < previous.TimestampMilliseconds
                || cumulativeWork < previous.CumulativeWork)
            {
                Reset();
            }
            else if (timestampMilliseconds == previous.TimestampMilliseconds)
            {
                _hasProgress |= cumulativeWork > previous.CumulativeWork;
                _samples[^1] = new RateSample(
                    timestampMilliseconds, cumulativeWork);
                return CalculateRate();
            }

            if (_samples.Count > 0)
                _hasProgress |= cumulativeWork > previous.CumulativeWork;
        }

        _samples.Add(new RateSample(timestampMilliseconds, cumulativeWork));
        long cutoff = timestampMilliseconds - _windowMilliseconds;
        while (_samples.Count > 2
               && _samples[0].TimestampMilliseconds < cutoff)
            _samples.RemoveAt(0);

        return CalculateRate();
    }

    internal void Reset()
    {
        _samples.Clear();
        _hasProgress = false;
    }

    private double? CalculateRate()
    {
        if (!_hasProgress || _samples.Count < 2)
            return null;

        RateSample first = _samples[0];
        RateSample last = _samples[^1];
        long elapsedMilliseconds = last.TimestampMilliseconds
            - first.TimestampMilliseconds;
        if (elapsedMilliseconds <= 0)
            return null;

        return Math.Max(0, last.CumulativeWork - first.CumulativeWork)
            * 1000 / elapsedMilliseconds;
    }

    private readonly record struct RateSample(
        long TimestampMilliseconds,
        double CumulativeWork);
}

internal static class FileOperationStore
{
    private const int StateWriteAttempts = 10;
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int AccessDenied = unchecked((int)0x80070005);

    internal const string DirectoryOverrideEnvironmentVariable =
        "MULTIEXPLORER_OPERATION_DIR";
    internal static readonly TimeSpan StartupAcknowledgementTimeout =
        TimeSpan.FromSeconds(30);
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
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(DirectoryPath);
        string path = StatePath(state.Id);
        for (int attempt = 0; attempt < StateWriteAttempts; attempt++)
        {
            try
            {
                WriteJsonAtomically(path, state);
                return;
            }
            catch (Exception ex) when (attempt < StateWriteAttempts - 1
                                       && IsTransientFileAccessFailure(ex))
            {
                state.StateWriteRetries++;
                // This store is synchronous because it is also called from
                // Shell COM progress callbacks. Keep the retry bounded.
                Thread.Sleep(Math.Min(200, 25 * (attempt + 1)));
            }
        }
    }

    internal static bool TryWriteInitialState(FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(DirectoryPath);
        string statePath = StatePath(state.Id);
        string temporaryPath = statePath
            + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, JsonOptions));
            try
            {
                File.Move(temporaryPath, statePath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(statePath))
            {
                return false;
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    internal static bool HasStartupAcknowledgementTimedOut(
        DateTime createdUtc,
        DateTime utcNow) =>
        createdUtc == default
        || utcNow - createdUtc >= StartupAcknowledgementTimeout;

    internal static FileOperationState? TryReadState(string path)
    {
        try
        {
            // State updates atomically replace this file. Allow the writer to
            // rename it while a reader still holds the previous snapshot open.
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<FileOperationState>(
                stream, JsonOptions);
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
        // A unique name avoids two threads in the same process writing the
        // same temporary file while publishing different state snapshots.
        string temporaryPath = path
            + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath,
                JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool IsTransientFileAccessFailure(Exception exception) =>
        exception is UnauthorizedAccessException
        || exception is IOException
            && exception.HResult is SharingViolation or LockViolation or AccessDenied;
}
