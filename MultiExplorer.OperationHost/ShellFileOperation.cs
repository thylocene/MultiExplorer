using System.Runtime.InteropServices;

namespace MultiExplorer;

public static class ShellFileOperation
{
    private const int EAbort = unchecked((int)0x80004004);
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmMkdir = 0x0200;
    private const uint FofxRecycleOnDelete = 0x00080000;

    internal static FileOperationState Execute(FileOperationRequest request)
    {
        var state = new FileOperationState
        {
            Id = request.Id,
            Kind = request.Kind,
            Status = FileOperationStatus.Running,
            TotalItems = request.Sources.Length,
            HostProcessId = Environment.ProcessId,
            UpdatedUtc = DateTime.UtcNow,
        };
        FileOperationStore.WriteState(state);

        IFileOperation? operation = null;
        FileOperationProgressSink? sink = null;
        uint cookie = 0;
        var shellItems = new List<IShellItem>();
        try
        {
            var operationType = Type.GetTypeFromCLSID(
                new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))
                ?? throw new InvalidOperationException("IFileOperation is unavailable.");
            operation = (IFileOperation)Activator.CreateInstance(operationType)!;
            var cancellation = new CancellationProbe(request.Id);
            sink = new FileOperationProgressSink(state, cancellation);
            ThrowIfFailed(operation.Advise(sink, out cookie));

            uint flags = FofNoConfirmMkdir;
            if (request.Kind != FileOperationKind.DeletePermanently)
                flags |= FofAllowUndo;
            if (request.Kind == FileOperationKind.Delete)
                flags |= FofxRecycleOnDelete;
            ThrowIfFailed(operation.SetOperationFlags(flags));

            IShellItem? destination = null;
            if (request.Kind is FileOperationKind.Copy or FileOperationKind.Move)
            {
                if (string.IsNullOrWhiteSpace(request.Destination))
                    throw new InvalidDataException("A destination is required for copy and move operations.");
                destination = CreateShellItem(request.Destination);
                shellItems.Add(destination);
            }

            foreach (string sourcePath in request.Sources)
            {
                if (cancellation.IsRequested())
                    throw new OperationCanceledException();

                IShellItem source = CreateShellItem(sourcePath);
                shellItems.Add(source);
                int result = request.Kind switch
                {
                    FileOperationKind.Copy => operation.CopyItem(source, destination!, null, null),
                    FileOperationKind.Move => operation.MoveItem(source, destination!, null, null),
                    FileOperationKind.Delete or FileOperationKind.DeletePermanently =>
                        operation.DeleteItem(source, null),
                    _ => throw new InvalidOperationException("Unsupported file operation."),
                };
                ThrowIfFailed(result);
            }

            if (cancellation.IsRequested(force: true))
                throw new OperationCanceledException();

            int performResult = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            state.Result = performResult;
            state.Aborted = aborted || performResult == EAbort;
            state.Status = state.Aborted
                ? FileOperationStatus.Cancelled
                : performResult < 0 || state.Error != null
                    ? FileOperationStatus.Failed
                    : FileOperationStatus.Completed;
            if (performResult < 0 && !state.Aborted)
                state.Error = Marshal.GetExceptionForHR(performResult)?.Message;
        }
        catch (OperationCanceledException)
        {
            state.Result = EAbort;
            state.Aborted = true;
            state.Status = FileOperationStatus.Cancelled;
        }
        catch (Exception ex)
        {
            state.Result = ex.HResult;
            state.Status = ex.HResult == EAbort
                ? FileOperationStatus.Cancelled
                : FileOperationStatus.Failed;
            state.Aborted = state.Status == FileOperationStatus.Cancelled;
            state.Error = ex.Message;
        }
        finally
        {
            if (operation != null && cookie != 0)
            {
                try { operation.Unadvise(cookie); }
                catch { }
            }
            foreach (IShellItem item in shellItems)
            {
                try { Marshal.FinalReleaseComObject(item); }
                catch { }
            }
            if (operation != null)
            {
                try { Marshal.FinalReleaseComObject(operation); }
                catch { }
            }

            state.UpdatedUtc = DateTime.UtcNow;
            FileOperationStore.WriteState(state);
        }

        return state;
    }

    private static IShellItem CreateShellItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        int result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item);
        ThrowIfFailed(result);
        return item;
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class FileOperationProgressSink : IFileOperationProgressSink
    {
        private readonly FileOperationState _state;
        private readonly CancellationProbe _cancellation;
        private long _nextPublishAt;

        private const int StatePublishIntervalMilliseconds = 150;

        internal FileOperationProgressSink(
            FileOperationState state,
            CancellationProbe cancellation)
        {
            _state = state;
            _cancellation = cancellation;
        }

        private int Before(IShellItem item)
        {
            if (_cancellation.IsRequested())
            {
                _state.Status = FileOperationStatus.Cancelling;
                Save(force: true);
                return EAbort;
            }

            // Shell name resolution can itself be relatively expensive. Only do
            // it when the next externally visible state snapshot is due.
            if (Environment.TickCount64 >= _nextPublishAt)
            {
                item.GetDisplayName(0x80058000, out string name);
                _state.CurrentItem = name;
                Save(force: true);
            }
            return 0;
        }

        private int After(int result)
        {
            if (result >= 0) _state.CompletedItems++;
            else
            {
                _state.Result = result;
                _state.Error = Marshal.GetExceptionForHR(result)?.Message
                    ?? $"The Shell operation failed with HRESULT 0x{result:X8}.";
            }
            Save(force: result < 0);
            return _cancellation.IsRequested() ? EAbort : 0;
        }

        private void Save(bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now < _nextPublishAt) return;
            _nextPublishAt = now + StatePublishIntervalMilliseconds;
            _state.UpdatedUtc = DateTime.UtcNow;
            FileOperationStore.WriteState(_state);
        }

        public int StartOperations() { Save(force: true); return 0; }
        public int FinishOperations(int result) { _state.Result = result; Save(force: true); return 0; }
        public int PreRenameItem(uint flags, IShellItem item, string newName) => Before(item);
        public int PostRenameItem(uint flags, IShellItem item, string newName, int result, IShellItem? newItem) => After(result);
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) => Before(item);
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? newItem) => After(result);
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) => Before(item);
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? newItem) => After(result);
        public int PreDeleteItem(uint flags, IShellItem item) => Before(item);
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem) => After(result);
        public int PreNewItem(uint flags, IShellItem destination, string newName) => Before(destination);
        public int PostNewItem(uint flags, IShellItem destination, string newName, string? templateName, uint attributes, int result, IShellItem? newItem) => After(result);
        public int UpdateProgress(uint totalWork, uint workSoFar) =>
            _cancellation.IsRequested() ? EAbort : 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    internal sealed class CancellationProbe(Guid operationId)
    {
        private const int CheckIntervalMilliseconds = 75;
        private long _nextCheckAt;
        private bool _requested;

        internal bool IsRequested(bool force = false)
        {
            if (_requested) return true;
            long now = Environment.TickCount64;
            if (!force && now < _nextCheckAt) return false;

            _nextCheckAt = now + CheckIntervalMilliseconds;
            _requested = FileOperationStore.IsCancellationRequested(operationId);
            return _requested;
        }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint attributes, int result, IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint totalWork, uint workSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}
