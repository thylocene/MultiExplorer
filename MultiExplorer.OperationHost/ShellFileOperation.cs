using System.Runtime.InteropServices;

namespace MultiExplorer;

public static class ShellFileOperation
{
    private const int EAbort = unchecked((int)0x80004004);
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmMkdir = 0x0200;
    private const uint FofxRecycleOnDelete = 0x00080000;

    internal static FileOperationState Execute(
        FileOperationRequest request, bool showNativeProgressDialog = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var windowPromoter = new OperationWindowPromoter(
            request.WindowPlacement);

        var state = new FileOperationState
        {
            Id = request.Id,
            Kind = request.Kind,
            Status = FileOperationStatus.Running,
            TotalItems = request.Sources.Length,
            HostProcessId = Environment.ProcessId,
            CreatedUtc = request.CreatedUtc,
            UpdatedUtc = DateTime.UtcNow,
        };
        FileOperationPresentation.ApplyRequestContext(state, request);
        FileOperationStore.WriteState(state);

        var control = new OperationControlProbe(request.Id);
        IFileOperation? operation = null;
        FileOperationProgressSink? sink = null;
        uint cookie = 0;
        var shellItems = new List<IShellItem>();
        try
        {
            SourceMetrics metrics = CalculateSourceMetrics(request.Sources, control);
            state.TotalItems = metrics.ItemCount > 0
                ? metrics.ItemCount
                : request.Sources.Length;
            state.TotalBytes = metrics.TotalBytes;
            state.UpdatedUtc = DateTime.UtcNow;
            FileOperationStore.WriteState(state);

            if (control.IsCancellationRequested(force: true))
                throw new OperationCanceledException();

            var operationType = Type.GetTypeFromCLSID(
                new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))
                ?? throw new InvalidOperationException("IFileOperation is unavailable.");
            operation = (IFileOperation)Activator.CreateInstance(operationType)!;
            sink = new FileOperationProgressSink(state, control);
            ThrowIfFailed(operation.Advise(sink, out cookie));

            uint flags = FofNoConfirmMkdir;
            if (!showNativeProgressDialog)
                flags |= FofSilent;
            if (request.Kind == FileOperationKind.DeletePermanently)
                flags |= FofNoConfirmation;
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
                if (sink.CheckControlState() == EAbort)
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

            if (sink.CheckControlState() == EAbort)
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
            if (state.Status == FileOperationStatus.Completed && state.TotalWork > 0)
                state.WorkCompleted = state.TotalWork;
            if (state.Status == FileOperationStatus.Completed && state.TotalBytes > 0)
                state.BytesCompleted = state.TotalBytes;
            if (state.Status == FileOperationStatus.Completed)
                state.CompletedItems = state.TotalItems;
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

    private static SourceMetrics CalculateSourceMetrics(
        IEnumerable<string> sourcePaths, OperationControlProbe control)
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
            if (control.IsCancellationRequested(force: true))
                break;

            try
            {
                if (File.Exists(sourcePath))
                {
                    itemCount = AddItem(itemCount);
                    totalBytes = AddFileLength(totalBytes, sourcePath);
                    continue;
                }

                if (!Directory.Exists(sourcePath))
                    continue;
                itemCount = AddItem(itemCount);
                var directory = new DirectoryInfo(sourcePath);
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos(
                             "*", enumerationOptions))
                {
                    if (control.IsCancellationRequested())
                        return new SourceMetrics(itemCount, totalBytes);
                    itemCount = AddItem(itemCount);
                    if (entry is FileInfo file)
                        totalBytes = AddFileLength(totalBytes, file);
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                // Totals are estimates only. The Shell remains responsible for
                // reporting an actionable operation error if an item is unreadable.
                System.Diagnostics.Debug.WriteLine(
                    $"Could not measure '{sourcePath}' for progress: {ex}");
            }
        }

        return new SourceMetrics(itemCount, totalBytes);
    }

    private static ulong AddFileLength(ulong totalBytes, string filePath)
        => AddFileLength(totalBytes, new FileInfo(filePath));

    private static ulong AddFileLength(ulong totalBytes, FileInfo file)
    {
        long length = file.Length;
        ulong positiveLength = length > 0 ? (ulong)length : 0;
        return ulong.MaxValue - totalBytes < positiveLength
            ? ulong.MaxValue
            : totalBytes + positiveLength;
    }

    private static int AddItem(int itemCount) =>
        itemCount == int.MaxValue ? int.MaxValue : itemCount + 1;

    private readonly record struct SourceMetrics(int ItemCount, ulong TotalBytes);

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
        private readonly OperationControlProbe _control;
        private long _nextPublishAt;
        private bool _publishedPaused;

        private const int StatePublishIntervalMilliseconds = 150;

        internal FileOperationProgressSink(
            FileOperationState state,
            OperationControlProbe control)
        {
            _state = state;
            _control = control;
        }

        private int Before(IShellItem item)
        {
            int controlResult = CheckControlState();
            if (controlResult < 0)
                return controlResult;

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
            if (result >= 0)
            {
                if (_state.TotalItems <= 0 || _state.CompletedItems < _state.TotalItems)
                    _state.CompletedItems++;
            }
            else
            {
                _state.Result = result;
                _state.Error = Marshal.GetExceptionForHR(result)?.Message
                    ?? $"The Shell operation failed with HRESULT 0x{result:X8}.";
            }
            Save(force: result < 0);
            return CheckControlState();
        }

        internal int CheckControlState()
        {
            if (_control.IsCancellationRequested())
            {
                _state.Status = FileOperationStatus.Cancelling;
                Save(force: true);
                return EAbort;
            }

            while (_control.IsPauseRequested(force: true))
            {
                if (!_publishedPaused)
                {
                    _publishedPaused = true;
                    _state.Status = FileOperationStatus.Paused;
                    Save(force: true);
                }

                if (_control.IsCancellationRequested(force: true))
                {
                    _state.Status = FileOperationStatus.Cancelling;
                    Save(force: true);
                    return EAbort;
                }

                // IFileOperation callbacks are synchronous. Holding the callback
                // is what prevents the Shell operation from advancing while paused.
                Thread.Sleep(OperationControlProbe.CheckIntervalMilliseconds);
            }

            if (_publishedPaused)
            {
                _publishedPaused = false;
                _state.Status = FileOperationStatus.Running;
                Save(force: true);
            }

            return _control.IsCancellationRequested() ? EAbort : 0;
        }

        private void Save(bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now < _nextPublishAt) return;
            _nextPublishAt = now + StatePublishIntervalMilliseconds;
            _state.UpdatedUtc = DateTime.UtcNow;
            FileOperationStore.WriteState(_state);
        }

        public int StartOperations() { Save(force: true); return CheckControlState(); }
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
        public int UpdateProgress(uint totalWork, uint workSoFar)
        {
            OperationProgressMath.ApplyMonotonicWorkProgress(
                _state, totalWork, workSoFar);
            Save();
            return CheckControlState();
        }
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    internal sealed class OperationControlProbe(Guid operationId)
    {
        internal const int CheckIntervalMilliseconds = 75;
        private long _nextCancellationCheckAt;
        private long _nextPauseCheckAt;
        private bool _cancellationRequested;
        private bool _pauseRequested;

        internal bool IsCancellationRequested(bool force = false)
        {
            if (_cancellationRequested) return true;
            long now = Environment.TickCount64;
            if (!force && now < _nextCancellationCheckAt) return false;

            _nextCancellationCheckAt = now + CheckIntervalMilliseconds;
            _cancellationRequested = FileOperationStore.IsCancellationRequested(operationId);
            return _cancellationRequested;
        }

        internal bool IsPauseRequested(bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now < _nextPauseCheckAt)
                return _pauseRequested;

            _nextPauseCheckAt = now + CheckIntervalMilliseconds;
            _pauseRequested = FileOperationStore.IsPauseRequested(operationId);
            return _pauseRequested;
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
