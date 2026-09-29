using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiExplorer;

internal sealed class OperationBrokerContext : ApplicationContext
{
    private readonly Dictionary<Guid, OperationWorker> _activeOperations = [];
    private readonly HashSet<Guid> _reportedFailures = [];
    private readonly OperationProgressForm _progressForm;
    private readonly IntPtr _progressWindowHandle;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly bool _exitWhenIdle;
    private bool _hasScanned;
    private bool _shutdownRequested;
    private bool _disposed;

    internal OperationBrokerContext(bool exitWhenIdle)
    {
        _exitWhenIdle = exitWhenIdle;
        _progressForm = new OperationProgressForm();
        _progressForm.CancellationRequested += OnCancellationRequested;
        _progressForm.PauseRequested += OnPauseRequested;
        _progressForm.ResumeRequested += OnResumeRequested;
        _progressForm.ShutdownRequested += OnShutdownRequested;
        // Create a hidden top-level HWND immediately. WiX CloseApplication can
        // then deliver its session-shutdown messages even while the broker is idle
        // and the progress window has never been shown.
        _progressWindowHandle = _progressForm.Handle;
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 125 };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_shutdownRequested)
        {
            ExitThread();
            return;
        }

        ScanRequests();
        RefreshProgressWindow();

        if (_exitWhenIdle
            && _hasScanned
            && _activeOperations.Count == 0
            && !HasPendingRequests())
            ExitThread();
    }

    private void ScanRequests()
    {
        _hasScanned = true;
        try
        {
            Directory.CreateDirectory(FileOperationStore.DirectoryPath);
            foreach (string requestPath in Directory.EnumerateFiles(
                         FileOperationStore.DirectoryPath, "*.request.json"))
            {
                FileOperationRequest request;
                try
                {
                    request = FileOperationStore.ReadRequest(requestPath);
                }
                catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or System.Text.Json.JsonException)
                {
                    Debug.WriteLine(
                        $"Could not read operation request '{requestPath}': {ex}");
                    continue;
                }

                if (_activeOperations.ContainsKey(request.Id))
                    continue;

                FileOperationState? previousState = FileOperationStore.TryReadState(
                    FileOperationStore.StatePath(request.Id));
                if (previousState != null)
                {
                    if (previousState.IsTerminal)
                    {
                        FileOperationStore.RemoveTransientArtifacts(request.Id);
                    }
                    else
                    {
                        TryWriteFailure(request, new InvalidOperationException(
                            "The previous operation host stopped before completing the operation."));
                    }
                    continue;
                }

                if (FileOperationStore.HasStartupAcknowledgementTimedOut(
                        request.CreatedUtc, DateTime.UtcNow))
                {
                    TryWriteFailure(request, new TimeoutException(
                        "The file-operation request expired before an operation host acknowledged it."));
                    continue;
                }

                FileOperationState queuedState = CreateQueuedState(request);
                if (!FileOperationStore.TryWriteInitialState(queuedState))
                    continue;

                var thread = new Thread(() => ExecuteOperation(
                    request, _progressWindowHandle))
                {
                    IsBackground = false,
                    Name = $"MultiExplorer.Operation.{request.Id:N}",
                };
                thread.SetApartmentState(ApartmentState.STA);
                _activeOperations.Add(request.Id, new OperationWorker(request, thread));
                thread.Start();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not scan operation requests: {ex}");
        }
    }

    private static void ExecuteOperation(
        FileOperationRequest request,
        IntPtr ownerWindow)
    {
        int oleResult = -1;
        string stage = "OleInitialize";
        try
        {
            oleResult = OleInitialize(IntPtr.Zero);
            if (oleResult < 0)
                Marshal.ThrowExceptionForHR(oleResult);

            stage = "Enter ShellFileOperation.Execute";
            FileOperationState state = ShellFileOperation.Execute(
                request,
                showNativeProgressDialog: false,
                ownerWindow);
            if (state.IsTerminal)
                FileOperationStore.RemoveTransientArtifacts(request.Id);
        }
        catch (Exception ex)
        {
            TryWriteFailure(request, ex, stage);
        }
        finally
        {
            if (oleResult >= 0)
                OleUninitialize();
        }
    }

    private void RefreshProgressWindow()
    {
        var visibleStates = new List<FileOperationState>(_activeOperations.Count);
        var failures = new List<(FileOperationRequest Request, FileOperationState State)>();
        foreach ((Guid id, OperationWorker worker) in _activeOperations.ToArray())
        {
            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            if (state is { IsTerminal: false })
                visibleStates.Add(state);
            else if (state?.Status == FileOperationStatus.Failed
                     && _reportedFailures.Add(id))
                failures.Add((worker.Request, state));
            else if (state is null && worker.Thread.IsAlive)
                visibleStates.Add(CreateQueuedState(worker.Request));

            if (!worker.Thread.IsAlive)
            {
                worker.Thread.Join();
                _activeOperations.Remove(id);
            }
        }

        OperationWindowPlacement? placement = _activeOperations.Values
            .Select(static worker => worker.Request)
            .Where(static request => request.WindowPlacement is not null)
            .OrderByDescending(static request => request.CreatedUtc)
            .Select(static request => request.WindowPlacement)
            .FirstOrDefault();
        foreach ((FileOperationRequest request, FileOperationState state) in failures)
            _progressForm.ShowFailure(request, state);
        _progressForm.UpdateOperations(visibleStates, placement);
    }

    private static FileOperationState CreateQueuedState(FileOperationRequest request)
    {
        var state = new FileOperationState
        {
            Id = request.Id,
            Kind = request.Kind,
            Status = FileOperationStatus.Queued,
            TotalItems = request.Sources.Length,
            HostProcessId = Environment.ProcessId,
            CreatedUtc = request.CreatedUtc,
            UpdatedUtc = DateTime.UtcNow,
        };
        FileOperationPresentation.ApplyRequestContext(state, request);
        return state;
    }

    private void OnCancellationRequested(object? sender, Guid operationId)
    {
        try
        {
            FileOperationStore.RequestCancellation(operationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not cancel operation {operationId:N}: {ex}");
        }
    }

    private static void OnPauseRequested(object? sender, Guid operationId)
    {
        try
        {
            FileOperationStore.RequestPause(operationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not pause operation {operationId:N}: {ex}");
        }
    }

    private static void OnResumeRequested(object? sender, Guid operationId)
    {
        try
        {
            FileOperationStore.RequestResume(operationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not resume operation {operationId:N}: {ex}");
        }
    }

    private void OnShutdownRequested(object? sender, EventArgs e)
    {
        if (_shutdownRequested) return;

        _shutdownRequested = true;
        foreach (Guid operationId in _activeOperations.Keys.ToArray())
        {
            try
            {
                FileOperationStore.RequestCancellation(operationId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(
                    $"Could not cancel operation {operationId:N} during shutdown: {ex}");
            }
        }

        _progressForm.BeginInvoke((MethodInvoker)ExitThread);
    }

    private static bool HasPendingRequests()
    {
        try
        {
            return Directory.Exists(FileOperationStore.DirectoryPath)
                && Directory.EnumerateFiles(
                    FileOperationStore.DirectoryPath, "*.request.json").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not check pending operation requests: {ex}");
            return true;
        }
    }

    private static void TryWriteFailure(
        FileOperationRequest request, Exception exception,
        string? failureStage = null)
    {
        try
        {
            var state = new FileOperationState
            {
                Id = request.Id,
                Kind = request.Kind,
                Status = FileOperationStatus.Failed,
                TotalItems = request.Sources.Length,
                Error = exception.Message,
                FailureStage = failureStage,
                FailureExceptionType = exception.GetType().FullName,
                Result = exception.HResult,
                HostProcessId = Environment.ProcessId,
                CreatedUtc = request.CreatedUtc,
                UpdatedUtc = DateTime.UtcNow,
            };
            FileOperationPresentation.ApplyRequestContext(state, request);
            FileOperationStore.WriteState(state);
            FileOperationStore.RemoveTransientArtifacts(request.Id);
        }
        catch (Exception cleanupException)
        {
            Debug.WriteLine(
                $"Could not publish operation failure state: {cleanupException}");
        }
    }

    protected override void ExitThreadCore()
    {
        if (!_disposed)
        {
            _disposed = true;
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _progressForm.CancellationRequested -= OnCancellationRequested;
            _progressForm.PauseRequested -= OnPauseRequested;
            _progressForm.ResumeRequested -= OnResumeRequested;
            _progressForm.ShutdownRequested -= OnShutdownRequested;
            _progressForm.Dispose();
        }

        base.ExitThreadCore();
    }

    private sealed record OperationWorker(
        FileOperationRequest Request, Thread Thread);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
}
