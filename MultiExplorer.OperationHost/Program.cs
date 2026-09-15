using System.Runtime.InteropServices;

namespace MultiExplorer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? requestPath = args.Length == 2 && args[0] == "--request"
            ? args[1]
            : null;
        if (string.IsNullOrWhiteSpace(requestPath)) return 2;

        int oleResult = OleInitialize(IntPtr.Zero);
        try
        {
            FileOperationRequest request = FileOperationStore.ReadRequest(requestPath);
            FileOperationState state = ShellFileOperation.Execute(request);
            if (state.IsTerminal)
                FileOperationStore.RemoveTransientArtifacts(request.Id);
            return state.Status == FileOperationStatus.Completed ? 0
                : state.Status == FileOperationStatus.Cancelled ? 3 : 1;
        }
        catch (Exception ex)
        {
            TryWriteFailure(requestPath, ex);
            return 1;
        }
        finally
        {
            if (oleResult >= 0) OleUninitialize();
        }
    }

    private static void TryWriteFailure(string requestPath, Exception exception)
    {
        try
        {
            FileOperationRequest request = FileOperationStore.ReadRequest(requestPath);
            FileOperationStore.WriteState(new FileOperationState
            {
                Id = request.Id,
                Kind = request.Kind,
                Status = FileOperationStatus.Failed,
                TotalItems = request.Sources.Length,
                Error = exception.Message,
                Result = exception.HResult,
                HostProcessId = Environment.ProcessId,
                UpdatedUtc = DateTime.UtcNow,
            });
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
