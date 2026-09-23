using Microsoft.CSharp.RuntimeBinder;
using System.Runtime.InteropServices;

namespace MultiExplorer;

/// <summary>
/// Uses the Windows Shell command service to pin folders to Quick access and
/// files to Favorites in Home.
/// </summary>
internal static class QuickAccessService
{
    private const string QuickAccessNamespace =
        "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
    private const string IsPinnedProperty = "System.Home.IsPinned";
    private static readonly Guid QuickAccessCommandClassId =
        new("B455F46E-E4AF-4035-B0A4-CF18D2F6F28E");
    private static readonly TimeSpan PinCacheDuration = TimeSpan.FromMinutes(1);
    private static readonly object PinCacheGate = new();
    private static HashSet<string>? _cachedPinnedPaths;
    private static DateTime _pinCacheExpiresUtc;
    private static Task? _pinCacheWarmTask;

    internal static string GetCommandLabel(bool isPinned) => isPinned
        ? "Unpin from Quick access"
        : "Pin to Quick access";

    internal static string GetFavoriteCommandLabel(bool isPinned) => isPinned
        ? "Remove from Favorites"
        : "Add to Favorites";

    internal static bool IsPinned(string itemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemPath);
        string normalizedPath = NormalizePath(itemPath);
        bool refreshInBackground = false;
        bool? cachedResult = null;
        lock (PinCacheGate)
        {
            if (_cachedPinnedPaths is { } cached)
            {
                refreshInBackground = DateTime.UtcNow >= _pinCacheExpiresUtc;
                cachedResult = cached.Contains(normalizedPath);
            }
        }
        if (cachedResult is { } isPinned)
        {
            if (refreshInBackground)
                _ = WarmCacheAsync();
            return isPinned;
        }

        try
        {
            HashSet<string> loaded = LoadPinnedPaths();
            SetCachedPinnedPaths(loaded);
            return loaded.Contains(normalizedPath);
        }
        catch (Exception ex) when (IsShellAutomationException(ex))
        {
            AppLog.Debug(ex, nameof(IsPinned),
                $"Could not read the Home pin state for \"{itemPath}\".");
            return false;
        }
    }

    internal static Task WarmCacheAsync(CancellationToken cancellationToken = default)
    {
        Task warmTask;
        lock (PinCacheGate)
        {
            if (_cachedPinnedPaths is not null && DateTime.UtcNow < _pinCacheExpiresUtc)
                return Task.CompletedTask;

            _pinCacheWarmTask ??= Task.Run(WarmCacheCore);
            warmTask = _pinCacheWarmTask;
        }

        return cancellationToken.CanBeCanceled
            ? warmTask.WaitAsync(cancellationToken)
            : warmTask;
    }

    private static void WarmCacheCore()
    {
        try
        {
            SetCachedPinnedPaths(LoadPinnedPaths());
        }
        catch (Exception ex) when (IsShellAutomationException(ex))
        {
            AppLog.Debug(ex, nameof(WarmCacheAsync),
                "Could not pre-load the Home pin state.");
        }
        finally
        {
            lock (PinCacheGate)
                _pinCacheWarmTask = null;
        }
    }

    private static HashSet<string> LoadPinnedPaths()
    {
        var pinnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        object? shell = null;
        object? quickAccessFolder = null;
        object? items = null;
        try
        {
            shell = CreateShellApplication();
            dynamic shellAutomation = shell;
            quickAccessFolder = shellAutomation.NameSpace(QuickAccessNamespace);
            if (quickAccessFolder is null) return pinnedPaths;

            dynamic folder = quickAccessFolder;
            items = folder.Items();
            if (items is null) return pinnedPaths;

            dynamic folderItems = items;
            int count = Convert.ToInt32(folderItems.Count);
            for (int index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = folderItems.Item(index);
                    if (item is null) continue;

                    dynamic folderItem = item;
                    string? candidatePath = Convert.ToString(folderItem.Path);
                    object? value = folderItem.ExtendedProperty(IsPinnedProperty);
                    if (ConvertPinnedProperty(value)
                        && !string.IsNullOrWhiteSpace(candidatePath))
                        pinnedPaths.Add(NormalizePath(candidatePath));
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }

            return pinnedPaths;
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(quickAccessFolder);
            ReleaseComObject(shell);
        }
    }

    private static void SetCachedPinnedPaths(HashSet<string> paths)
    {
        lock (PinCacheGate)
        {
            _cachedPinnedPaths = paths;
            _pinCacheExpiresUtc = DateTime.UtcNow + PinCacheDuration;
        }
    }

    internal static bool TrySetPinned(string itemPath, bool pinned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemPath);

        try
        {
            string destination = Directory.Exists(itemPath)
                ? "Quick access"
                : "Favorites";
            bool dispatched = TryInvokeQuickAccessCommand(itemPath,
                out string? failureReason);
            if (!dispatched)
            {
                AppLog.Warn(null, nameof(TrySetPinned),
                    $"Windows could not {(pinned ? "pin" : "unpin")} " +
                    $"\"{Path.GetFileName(itemPath)}\" " +
                    $"{(pinned ? "to" : "from")} {destination}." +
                    (failureReason is null ? string.Empty : $" {failureReason}"));
            }
            else
            {
                UpdateCachedPinnedState(itemPath, pinned);
            }

            return dispatched;
        }
        catch (Exception ex) when (IsShellAutomationException(ex))
        {
            string destination = Directory.Exists(itemPath)
                ? "Quick access"
                : "Favorites";
            AppLog.Warn(ex, nameof(TrySetPinned),
                $"Windows could not {(pinned ? "pin" : "unpin")} " +
                $"\"{Path.GetFileName(itemPath)}\" " +
                $"{(pinned ? "to" : "from")} {destination}.");
            return false;
        }
    }

    internal static bool ConvertPinnedProperty(object? value) => value switch
    {
        bool pinned => pinned,
        byte number => number != 0,
        sbyte number => number != 0,
        short number => number != 0,
        ushort number => number != 0,
        int number => number != 0,
        uint number => number != 0,
        long number => number != 0,
        ulong number => number != 0,
        string text when bool.TryParse(text, out bool pinned) => pinned,
        string text when long.TryParse(text, out long number) => number != 0,
        _ => false,
    };

    private static bool TryInvokeQuickAccessCommand(string itemPath,
        out string? failureReason)
    {
        failureReason = null;
        IntPtr absoluteItemIdList = IntPtr.Zero;
        IntPtr shellItemArray = IntPtr.Zero;
        object? command = null;
        try
        {
            int result = NativeMethods.SHParseDisplayName(itemPath,
                IntPtr.Zero, out absoluteItemIdList, 0, out _);
            if (result < 0 || absoluteItemIdList == IntPtr.Zero)
            {
                failureReason =
                    $"Creating the Shell item ID returned 0x{result:X8}.";
                return false;
            }

            result = SHCreateShellItemArrayFromIDLists(1,
                [absoluteItemIdList], out shellItemArray);
            if (result < 0 || shellItemArray == IntPtr.Zero)
            {
                failureReason =
                    $"Creating the Shell selection returned 0x{result:X8}.";
                return false;
            }

            Type commandType = Type.GetTypeFromCLSID(
                QuickAccessCommandClassId, throwOnError: true)!;
            command = Activator.CreateInstance(commandType);
            if (command is not IObjectWithSelection selection
                || command is not IExecuteCommand executor)
            {
                AppLog.Debug(nameof(TryInvokeQuickAccessCommand),
                    "The Windows Quick access command did not expose its execution interfaces.");
                failureReason =
                    "The Windows command did not expose the required interface.";
                return false;
            }

            if (command is IInitializeCommand initialization)
            {
                result = initialization.Initialize("pintohome", IntPtr.Zero);
                if (result < 0)
                {
                    failureReason =
                        $"Initialising the Windows command returned 0x{result:X8}.";
                    return false;
                }
            }

            result = selection.SetSelection(shellItemArray);
            if (result < 0)
            {
                failureReason =
                    $"Setting the Windows command selection returned 0x{result:X8}.";
                return false;
            }

            result = executor.Execute();
            if (result < 0)
            {
                AppLog.Debug(nameof(TryInvokeQuickAccessCommand),
                    $"The Windows Quick access command returned 0x{result:X8}.");
                failureReason = $"Windows returned error 0x{result:X8}.";
            }
            return result >= 0;
        }
        finally
        {
            ReleaseComObject(command);
            if (shellItemArray != IntPtr.Zero)
                Marshal.Release(shellItemArray);
            if (absoluteItemIdList != IntPtr.Zero)
                NativeMethods.CoTaskMemFree(absoluteItemIdList);
        }
    }

    private static object CreateShellApplication()
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application",
            throwOnError: true)!;
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException(
                "Windows did not create the Shell automation service.");
    }

    private static void UpdateCachedPinnedState(string itemPath, bool pinned)
    {
        string normalizedPath = NormalizePath(itemPath);
        lock (PinCacheGate)
        {
            if (_cachedPinnedPaths is null) return;
            if (pinned)
                _cachedPinnedPaths.Add(normalizedPath);
            else
                _cachedPinnedPaths.Remove(normalizedPath);
            _pinCacheExpiresUtc = DateTime.UtcNow + PinCacheDuration;
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private static bool IsShellAutomationException(Exception exception) =>
        exception is COMException
            or RuntimeBinderException
            or InvalidCastException
            or MissingMemberException
            or InvalidOperationException
            or ArgumentException;

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    [ComImport]
    [Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSelection
    {
        [PreserveSig] int SetSelection(IntPtr items);
        [PreserveSig] int GetSelection(ref Guid interfaceId, out IntPtr value);
    }

    [ComImport]
    [Guid("85075ACF-231F-40EA-9610-D26B7B58F638")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeCommand
    {
        [PreserveSig] int Initialize(
            [MarshalAs(UnmanagedType.LPWStr)] string commandName,
            IntPtr propertyBag);
    }

    [ComImport]
    [Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExecuteCommand
    {
        [PreserveSig] int SetKeyState(uint keyState);
        [PreserveSig] int SetParameters(
            [MarshalAs(UnmanagedType.LPWStr)] string? parameters);
        [PreserveSig] int SetPosition(NativeMethods.POINT position);
        [PreserveSig] int SetShowWindow(int showCommand);
        [PreserveSig] int SetNoShowUI(
            [MarshalAs(UnmanagedType.Bool)] bool suppressUserInterface);
        [PreserveSig] int SetDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string? directory);
        [PreserveSig] int Execute();
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint itemCount,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] itemIdLists,
        out IntPtr shellItemArray);
}
