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
}

internal sealed class FileOperationRequest
{
    public Guid Id { get; set; }
    public FileOperationKind Kind { get; set; }
    public string[] Sources { get; set; } = [];
    public string? Destination { get; set; }
    public DateTime CreatedUtc { get; set; }
}

internal sealed class FileOperationState
{
    public Guid Id { get; set; }
    public FileOperationKind Kind { get; set; }
    public FileOperationStatus Status { get; set; }
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public string? CurrentItem { get; set; }
    public string? Error { get; set; }
    public int Result { get; set; }
    public bool Aborted { get; set; }
    public int HostProcessId { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public bool IsTerminal => Status is FileOperationStatus.Completed
        or FileOperationStatus.Failed or FileOperationStatus.Cancelled;
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

    /// <summary>Removes the short-lived command and cancellation files for a finished operation.</summary>
    internal static void RemoveTransientArtifacts(Guid id)
    {
        TryDeleteFile(RequestPath(id));
        TryDeleteFile(CancelPath(id));
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
