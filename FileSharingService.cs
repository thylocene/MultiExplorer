using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace MultiExplorer;

internal enum EmailShareStatus
{
    Completed,
    Cancelled,
    Unavailable,
    Failed,
}

internal readonly record struct EmailShareResult(
    EmailShareStatus Status, int ErrorCode = 0);

/// <summary>
/// Provides the application-owned sharing actions used by <see cref="ShareDialog"/>.
/// </summary>
internal sealed class FileSharingService
{
    private const int MapiSuccess = 0;
    private const int MapiUserAbort = 1;
    private const int MapiNotSupported = 26;
    private const int MapiLogonUi = 0x00000001;
    private const int MapiDialog = 0x00000008;

    internal static bool CanShare(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Count > 0 && paths.All(File.Exists);
    }

    internal static string[] GetShareableFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async Task<bool> CopyFilesAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] files = GetShareableFiles(paths);
        if (files.Length == 0) return false;

        var dropList = new StringCollection();
        dropList.AddRange(files);
        var data = new DataObject();
        data.SetFileDropList(dropList);
        return await SetClipboardDataAsync(data, cancellationToken);
    }

    internal async Task<bool> CopyPathsAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] files = GetShareableFiles(paths);
        if (files.Length == 0) return false;

        var data = new DataObject();
        data.SetText(string.Join(Environment.NewLine, files), TextDataFormat.UnicodeText);
        return await SetClipboardDataAsync(data, cancellationToken);
    }

    internal async Task<EmailShareResult> ComposeEmailAsync(IntPtr ownerWindow,
        IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (ownerWindow == IntPtr.Zero)
            throw new ArgumentException("An email window owner is required.",
                nameof(ownerWindow));

        string[] files = GetShareableFiles(paths);
        if (files.Length == 0)
            return new EmailShareResult(EmailShareStatus.Failed);

        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<EmailShareResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(SendEmail(ownerWindow, files));
            }
            catch (DllNotFoundException)
            {
                completion.TrySetResult(new EmailShareResult(
                    EmailShareStatus.Unavailable));
            }
            catch (EntryPointNotFoundException)
            {
                completion.TrySetResult(new EmailShareResult(
                    EmailShareStatus.Unavailable));
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, nameof(ComposeEmailAsync),
                    "Could not open the default email application.");
                completion.TrySetResult(new EmailShareResult(
                    EmailShareStatus.Failed));
            }
        })
        {
            IsBackground = true,
            Name = "MultiExplorer email sharing",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return await completion.Task.WaitAsync(cancellationToken);
    }

    internal static EmailShareResult MapEmailResult(int result) => result switch
    {
        MapiSuccess => new EmailShareResult(EmailShareStatus.Completed),
        MapiUserAbort => new EmailShareResult(EmailShareStatus.Cancelled, result),
        MapiNotSupported => new EmailShareResult(EmailShareStatus.Unavailable, result),
        _ => new EmailShareResult(EmailShareStatus.Failed, result),
    };

    private static async Task<bool> SetClipboardDataAsync(DataObject data,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        const int maximumAttempts = 4;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                ClipboardCutState.Clear();
                return true;
            }
            catch (ExternalException ex) when (attempt < maximumAttempts)
            {
                AppLog.Debug(ex, nameof(SetClipboardDataAsync));
                await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
            }
            catch (Exception ex) when (ex is ExternalException
                                       or ThreadStateException)
            {
                AppLog.Warn(ex, nameof(SetClipboardDataAsync),
                    "Could not copy the selected files to the clipboard.");
                return false;
            }
        }

        return false;
    }

    private static EmailShareResult SendEmail(IntPtr ownerWindow,
        IReadOnlyList<string> files)
    {
        int descriptorSize = Marshal.SizeOf<MapiFileDescription>();
        IntPtr descriptors = Marshal.AllocHGlobal(descriptorSize * files.Count);
        int initializedDescriptors = 0;
        try
        {
            for (int index = 0; index < files.Count; index++)
            {
                var descriptor = new MapiFileDescription
                {
                    Position = -1,
                    PathName = files[index],
                    FileName = Path.GetFileName(files[index]),
                };
                Marshal.StructureToPtr(descriptor,
                    IntPtr.Add(descriptors, index * descriptorSize),
                    fDeleteOld: false);
                initializedDescriptors++;
            }

            var message = new MapiMessage
            {
                Subject = files.Count == 1
                    ? Path.GetFileName(files[0])
                    : $"{files.Count} files from MultiExplorer",
                FileCount = files.Count,
                Files = descriptors,
            };
            int result = MapiSendMail(IntPtr.Zero, ownerWindow, ref message,
                MapiLogonUi | MapiDialog, 0);
            return MapEmailResult(result);
        }
        finally
        {
            for (int index = 0; index < initializedDescriptors; index++)
            {
                Marshal.DestroyStructure<MapiFileDescription>(
                    IntPtr.Add(descriptors, index * descriptorSize));
            }
            Marshal.FreeHGlobal(descriptors);
        }
    }

    [DllImport("MAPI32.DLL", CharSet = CharSet.Ansi,
        EntryPoint = "MAPISendMail")]
    private static extern int MapiSendMail(IntPtr session, IntPtr ownerWindow,
        ref MapiMessage message, int flags, int reserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MapiMessage
    {
        internal int Reserved;
        [MarshalAs(UnmanagedType.LPStr)] internal string? Subject;
        [MarshalAs(UnmanagedType.LPStr)] internal string? NoteText;
        [MarshalAs(UnmanagedType.LPStr)] internal string? MessageType;
        [MarshalAs(UnmanagedType.LPStr)] internal string? DateReceived;
        [MarshalAs(UnmanagedType.LPStr)] internal string? ConversationId;
        internal int Flags;
        internal IntPtr Originator;
        internal int RecipientCount;
        internal IntPtr Recipients;
        internal int FileCount;
        internal IntPtr Files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MapiFileDescription
    {
        internal int Reserved;
        internal int Flags;
        internal int Position;
        [MarshalAs(UnmanagedType.LPStr)] internal string? PathName;
        [MarshalAs(UnmanagedType.LPStr)] internal string? FileName;
        internal IntPtr FileType;
    }
}
