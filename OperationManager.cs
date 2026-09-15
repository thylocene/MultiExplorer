using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace MultiExplorer;

internal sealed class OperationManager : IDisposable
{
    private static readonly TimeSpan OperationArtifactRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan ArtifactCleanupInterval = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, FileOperationState> _states = new();
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly SynchronizationContext? _uiContext;
    private DateTime _nextArtifactCleanupUtc;
    private bool _disposed;

    internal static OperationManager? Current { get; private set; }

    internal event EventHandler? OperationsChanged;
    internal event EventHandler? OperationsBecameIdle;

    internal OperationManager()
    {
        if (Current != null)
            throw new InvalidOperationException("Only one operation manager may be active.");

        Current = this;
        _uiContext = SynchronizationContext.Current;
        DiscoverExistingOperations();
        _nextArtifactCleanupUtc = DateTime.UtcNow + ArtifactCleanupInterval;
        _pollTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _pollTimer.Tick += (_, _) => RefreshStates();
        _pollTimer.Start();
    }

    internal int ActiveCount
    {
        get
        {
            lock (_gate) return _states.Values.Count(state => !state.IsTerminal);
        }
    }

    internal IReadOnlyList<FileOperationState> ActiveOperations
    {
        get
        {
            lock (_gate)
                return _states.Values.Where(state => !state.IsTerminal)
                    .Select(Clone).ToArray();
        }
    }

    internal Guid? Start(
        FileOperationKind kind, IEnumerable<string> sources, string? destination = null)
    {
        if (_disposed) return null;

        string[] paths = sources.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return null;
        if (kind is FileOperationKind.Copy or FileOperationKind.Move
            && string.IsNullOrWhiteSpace(destination))
            return null;

        var request = new FileOperationRequest
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Sources = paths,
            Destination = destination,
            CreatedUtc = DateTime.UtcNow,
        };
        var state = new FileOperationState
        {
            Id = request.Id,
            Kind = request.Kind,
            Status = FileOperationStatus.Queued,
            TotalItems = paths.Length,
            UpdatedUtc = DateTime.UtcNow,
        };

        bool requestWritten = false;
        try
        {
            FileOperationStore.WriteRequest(request);
            requestWritten = true;
            string hostPath = Path.Combine(AppContext.BaseDirectory,
                "MultiExplorer.OperationHost.exe");
            if (!File.Exists(hostPath))
                throw new FileNotFoundException("The MultiExplorer operation host is missing.", hostPath);

            lock (_gate) _states[state.Id] = state;
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                ArgumentList = { "--request", FileOperationStore.RequestPath(request.Id) },
            });
            if (process == null)
                throw new InvalidOperationException("Windows did not start the operation host.");
            state.HostProcessId = process.Id;
            process.Dispose();
            RaiseChanged();
            return request.Id;
        }
        catch (Exception ex)
        {
            lock (_gate) _states.Remove(state.Id);
            if (requestWritten)
                FileOperationStore.RemoveTransientArtifacts(request.Id);
            AppLog.Warn(ex, nameof(OperationManager), "Could not start the file operation.");
            RaiseChanged();
            return null;
        }
    }

    internal void CancelAll()
    {
        foreach (FileOperationState state in ActiveOperations)
        {
            try
            {
                FileOperationStore.RequestCancellation(state.Id);
                lock (_gate)
                {
                    if (_states.TryGetValue(state.Id, out FileOperationState? current))
                        current.Status = FileOperationStatus.Cancelling;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, nameof(CancelAll),
                    $"Could not request cancellation for operation {state.Id:N}.");
            }
        }
        RaiseChanged();
    }

    internal string DescribeActiveOperations()
    {
        IReadOnlyList<FileOperationState> active = ActiveOperations;
        if (active.Count == 0) return "No file operations are active.";

        var text = new StringBuilder();
        foreach (FileOperationState state in active)
        {
            string action = state.Kind switch
            {
                FileOperationKind.Copy => "Copying",
                FileOperationKind.Move => "Moving",
                _ => "Deleting",
            };
            text.AppendLine($"{action}: {state.CompletedItems} of {state.TotalItems} item(s)" +
                (string.IsNullOrEmpty(state.CurrentItem)
                    ? string.Empty
                    : $" — {Path.GetFileName(state.CurrentItem)}"));
        }
        return text.ToString().TrimEnd();
    }

    private void DiscoverExistingOperations()
    {
        try
        {
            CleanupExpiredOperationArtifacts();
            if (!Directory.Exists(FileOperationStore.DirectoryPath)) return;
            foreach (string path in Directory.EnumerateFiles(
                         FileOperationStore.DirectoryPath, "*.state.json"))
            {
                FileOperationState? state = FileOperationStore.TryReadState(path);
                if (state != null && !state.IsTerminal
                    && IsOperationHostRunning(state.HostProcessId))
                    _states[state.Id] = state;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(DiscoverExistingOperations));
        }
    }

    /// <summary>
    /// Keeps completed status files briefly for diagnostics, while removing the
    /// request and cancellation files as soon as a job is terminal. The same pass
    /// converts jobs abandoned by a terminated helper process into retained failures.
    /// </summary>
    internal static void CleanupExpiredOperationArtifacts()
    {
        try
        {
            string directory = FileOperationStore.DirectoryPath;
            if (!Directory.Exists(directory)) return;

            DateTime cutoffUtc = DateTime.UtcNow - OperationArtifactRetention;
            foreach (string path in Directory.EnumerateFiles(directory, "*.state.json"))
            {
                FileOperationState? state = FileOperationStore.TryReadState(path);
                if (state == null)
                {
                    DeleteIfExpired(path, cutoffUtc);
                    continue;
                }

                if (state.IsTerminal)
                {
                    FileOperationStore.RemoveTransientArtifacts(state.Id);
                    if (IsExpired(path, cutoffUtc))
                        FileOperationStore.RemoveState(state.Id);
                    continue;
                }

                if (!IsOperationHostRunning(state.HostProcessId))
                    MarkInterruptedOperationAsFailed(state);
            }

            CleanupOrphanedRequests(directory, cutoffUtc);
            CleanupExpiredFiles(directory, "*.cancel", cutoffUtc);
            CleanupExpiredFiles(directory, "*.tmp", cutoffUtc);
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(CleanupExpiredOperationArtifacts));
        }
    }

    private static void CleanupOrphanedRequests(string directory, DateTime cutoffUtc)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.request.json"))
        {
            if (!IsExpired(path, cutoffUtc)) continue;

            try
            {
                FileOperationRequest request = FileOperationStore.ReadRequest(path);
                if (!File.Exists(FileOperationStore.StatePath(request.Id)))
                {
                    FileOperationStore.WriteState(new FileOperationState
                    {
                        Id = request.Id,
                        Kind = request.Kind,
                        Status = FileOperationStatus.Failed,
                        TotalItems = request.Sources.Length,
                        Error = "The application exited before the operation host started.",
                        Result = unchecked((int)0x80004005),
                        UpdatedUtc = DateTime.UtcNow,
                    });
                    FileOperationStore.RemoveTransientArtifacts(request.Id);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug(ex, nameof(CleanupOrphanedRequests),
                    $"Could not reconcile operation request '{path}'.");
            }
        }
    }

    private static void MarkInterruptedOperationAsFailed(FileOperationState state)
    {
        state.Status = FileOperationStatus.Failed;
        state.Error = "The operation host exited before reporting completion.";
        state.Result = unchecked((int)0x80004005);
        state.UpdatedUtc = DateTime.UtcNow;
        FileOperationStore.WriteState(state);
        FileOperationStore.RemoveTransientArtifacts(state.Id);
    }

    private static void CleanupExpiredFiles(string directory, string searchPattern, DateTime cutoffUtc)
    {
        foreach (string path in Directory.EnumerateFiles(directory, searchPattern))
            DeleteIfExpired(path, cutoffUtc);
    }

    private static void DeleteIfExpired(string path, DateTime cutoffUtc)
    {
        if (IsExpired(path, cutoffUtc))
            FileOperationStore.TryDeleteFile(path);
    }

    private static bool IsExpired(string path, DateTime cutoffUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) <= cutoffUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug(ex, nameof(IsExpired),
                $"Could not read the last-write time for operation artifact '{path}'.");
            return false;
        }
    }

    private void RefreshStates()
    {
        if (_disposed) return;
        if (DateTime.UtcNow >= _nextArtifactCleanupUtc)
        {
            CleanupExpiredOperationArtifacts();
            _nextArtifactCleanupUtc = DateTime.UtcNow + ArtifactCleanupInterval;
        }

        bool wasActive;
        bool changed = false;
        var failures = new List<FileOperationState>();
        lock (_gate) wasActive = _states.Values.Any(state => !state.IsTerminal);

        Guid[] ids;
        lock (_gate) ids = _states.Keys.ToArray();
        foreach (Guid id in ids)
        {
            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            if (state == null)
            {
                lock (_gate)
                {
                    if (_states.TryGetValue(id, out FileOperationState? current))
                        state = Clone(current);
                }
                if (state == null || state.HostProcessId <= 0
                    || DateTime.UtcNow - state.UpdatedUtc <= TimeSpan.FromSeconds(3)
                    || IsOperationHostRunning(state.HostProcessId))
                    continue;

                state.Status = FileOperationStatus.Failed;
                state.Error = "The operation host exited before starting the operation.";
                state.Result = unchecked((int)0x80004005);
                state.UpdatedUtc = DateTime.UtcNow;
            }

            if (!state.IsTerminal && state.HostProcessId > 0
                && DateTime.UtcNow - state.UpdatedUtc > TimeSpan.FromSeconds(3)
                && !IsOperationHostRunning(state.HostProcessId))
            {
                state.Status = FileOperationStatus.Failed;
                state.Error = "The operation host exited before reporting completion.";
                state.Result = unchecked((int)0x80004005);
                state.UpdatedUtc = DateTime.UtcNow;
                try { FileOperationStore.WriteState(state); }
                catch (IOException ex)
                {
                    AppLog.Debug(ex, nameof(RefreshStates),
                        $"Could not persist the failed state for operation {state.Id:N}.");
                }
            }
            lock (_gate)
            {
                if (!_states.TryGetValue(id, out FileOperationState? previous)
                    || previous.Status != state.Status
                    || previous.CompletedItems != state.CompletedItems
                    || previous.CurrentItem != state.CurrentItem)
                {
                    if (state.Status == FileOperationStatus.Failed
                        && previous?.Status != FileOperationStatus.Failed)
                        failures.Add(Clone(state));
                    _states[id] = state;
                    changed = true;
                }
            }

            if (state.IsTerminal)
            {
                FileOperationStore.RemoveTransientArtifacts(id);
                lock (_gate) _states.Remove(id);
            }
        }

        foreach (FileOperationState failure in failures)
            AppLog.Warn(null, nameof(OperationManager),
                $"A {failure.Kind.ToString().ToLowerInvariant()} operation failed: " +
                (failure.Error ?? $"Windows returned 0x{failure.Result:X8}."));

        bool isActive;
        lock (_gate) isActive = _states.Values.Any(state => !state.IsTerminal);
        if (changed) RaiseChanged();
        if (wasActive && !isActive)
            OperationsBecameIdle?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseChanged()
    {
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => OperationsChanged?.Invoke(this, EventArgs.Empty), null);
            return;
        }
        OperationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsOperationHostRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static FileOperationState Clone(FileOperationState state) => new()
    {
        Id = state.Id,
        Kind = state.Kind,
        Status = state.Status,
        TotalItems = state.TotalItems,
        CompletedItems = state.CompletedItems,
        CurrentItem = state.CurrentItem,
        Error = state.Error,
        Result = state.Result,
        Aborted = state.Aborted,
        HostProcessId = state.HostProcessId,
        UpdatedUtc = state.UpdatedUtc,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
