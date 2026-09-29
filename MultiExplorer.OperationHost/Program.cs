using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiExplorer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--broker", StringComparer.OrdinalIgnoreCase))
        {
            bool exitWhenIdle = args.Contains(
                "--exit-when-idle", StringComparer.OrdinalIgnoreCase);
            return RunBroker(exitWhenIdle);
        }

        string? requestPath = args.Length == 2 && args[0] == "--request"
            ? args[1]
            : null;
        if (string.IsNullOrWhiteSpace(requestPath)) return 2;

        int oleResult = -1;
        string stage = "OleInitialize";
        try
        {
            oleResult = OleInitialize(IntPtr.Zero);
            if (oleResult < 0)
                Marshal.ThrowExceptionForHR(oleResult);

            stage = "Read operation request";
            FileOperationRequest request = FileOperationStore.ReadRequest(requestPath);
            if (FileOperationStore.HasStartupAcknowledgementTimedOut(
                    request.CreatedUtc, DateTime.UtcNow))
                throw new TimeoutException(
                    "The file-operation request expired before an operation host acknowledged it.");
            stage = "Enter ShellFileOperation.Execute";
            FileOperationState state = ShellFileOperation.Execute(request);
            if (state.IsTerminal)
                FileOperationStore.RemoveTransientArtifacts(request.Id);
            return state.Status == FileOperationStatus.Completed ? 0
                : state.Status == FileOperationStatus.Cancelled ? 3 : 1;
        }
        catch (Exception ex)
        {
            TryWriteFailure(requestPath, ex, stage);
            return 1;
        }
        finally
        {
            if (oleResult >= 0) OleUninitialize();
        }
    }

    private static int RunBroker(bool exitWhenIdle)
    {
        using var mutex = new Mutex(initiallyOwned: false,
            FileOperationStore.BrokerMutexName);
        bool ownsMutex;
        try
        {
            ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
            return 0;

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var context = new OperationBrokerContext(exitWhenIdle);
            Application.Run(context);
            return 0;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void TryWriteFailure(
        string requestPath, Exception exception, string failureStage)
    {
        try
        {
            FileOperationRequest request = FileOperationStore.ReadRequest(requestPath);
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
            System.Diagnostics.Debug.WriteLine(
                $"Could not publish operation failure state: {cleanupException}");
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
}
