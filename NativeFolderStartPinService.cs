using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MultiExplorer;

/// <summary>
/// Creates a native Win32 shortcut pin for a folder. Windows 11 renders these
/// pins like File Explorer's folder pins, without the compulsory grey
/// background applied to secondary tiles.
/// </summary>
internal static class NativeFolderStartPinService
{
    private const string UnifiedTileIdentifierClass =
        "WindowsInternal.Shell.UnifiedTile.UnifiedTileIdentifier";
    private const string UserPinHelperClass =
        "WindowsInternal.Shell.UnifiedTile.Private.UnifiedTileUserPinHelper";
    private const string TileIdClass = "WindowsUdk.UI.StartScreen.TileId";
    private const string ExtensionFactoryClass =
        "WindowsUdk.ApplicationModel.AppExtensions.ExtensionFactory";
    private const string StartManagerExtensionName =
        "com.microsoft.windows.app.startmenu";
    private const string StartManagerRuntimeClass =
        "WindowsUdk.UI.StartScreen.StartScreenManagerExtension";
    private const string StartManagerWindowClass = "STATIC";
    private const string StartManagerWindowName =
        "StartScreenManagerWindowService";
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const int CoreQueryWindowServiceOrdinal = 7;
    private const ushort VtLpwStr = 31;
    private const uint ShcneCreate = 0x00000002;
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcnfPathW = 0x0005;
    private const int AsyncStatusStarted = 0;
    private const int AsyncStatusCompleted = 1;
    private const int AsyncStatusCanceled = 2;
    private const int AsyncStatusError = 3;
    private const int AsyncCompletionPollMilliseconds = 50;
    private const int AsyncCompletionPollLimit = 200;

    private static readonly Guid Win32IdentifierFactoryId =
        new("0E7735BE-A965-44A6-A75F-54B8BCD67BEC");
    private static readonly Guid UserPinHelperId =
        new("0083831C-82D6-4E8F-BCC2-A8AC2691BE49");
    private static readonly Guid TileIdStaticsId =
        new("A04AFCD6-91EC-52D7-AB03-75F8CC65A086");
    private static readonly Guid ExtensionFactoryId =
        new("836DA1ED-5BE8-5365-8452-6AF327AA427B");
    private static readonly Guid StartManagerId =
        new("4C550F3F-F924-5F93-BEF5-7B65B013CC8E");
    private static readonly Guid StartManagerServiceId =
        new("4D91DAFC-4687-406D-AFCA-FDD69E51CC79");
    private static readonly Guid AsyncInfoId =
        new("00000036-0000-0000-C000-000000000046");
    private static readonly NativeMethods.PROPERTYKEY AppUserModelIdKey = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    internal static bool IsPinned(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath)) return false;

        string fullPath = Path.GetFullPath(folderPath);
        string shortcutPath = GetShortcutPath(fullPath);
        return File.Exists(shortcutPath);
    }

    internal static async Task<bool> TrySetPinnedAsync(string folderPath,
        bool pin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        string fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath)) return false;

        string shortcutPath = GetShortcutPath(fullPath);
        try
        {
            StartPinRequest request = await Task.Run(() =>
                CreatePinRequest(fullPath, shortcutPath, pin),
                cancellationToken);
            if (!request.Started || request.Operation == IntPtr.Zero)
                return false;

            try
            {
                bool changed = await WaitForResultAsync(request.Operation,
                    cancellationToken);
                if (changed && !pin)
                {
                    await Task.Run(() => DeleteShortcut(shortcutPath),
                        cancellationToken);
                }
                return changed;
            }
            finally
            {
                Marshal.Release(request.Operation);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or
                                   IOException or InvalidOperationException or
                                   MarshalDirectiveException or
                                   UnauthorizedAccessException)
        {
            AppLog.Debug(ex, nameof(TrySetPinnedAsync),
                "Native folder pinning is unavailable.");
            return false;
        }
    }

    private static StartPinRequest CreatePinRequest(string folderPath,
        string shortcutPath, bool pin)
    {
        if (pin) CreateOrUpdateShortcut(folderPath, shortcutPath);
        if (!File.Exists(shortcutPath)) return default;

        IntPtr asyncOperation = IntPtr.Zero;
        bool started = TryWithIdentifier(folderPath, identifier =>
        {
            if (pin && !TryCreateUserPinnedTile(identifier)) return false;
            return TryCreateTileId(identifier, tileId =>
            {
                return TryUseStartManager(manager =>
                {
                    IntPtr vtable = Marshal.ReadIntPtr(manager);
                    var request = GetDelegate<RequestTileChangeDelegate>(
                        vtable, pin ? 7 : 8);
                    int result = request(manager, tileId,
                        out asyncOperation);
                    LogFailure(nameof(CreatePinRequest), result);
                    return result >= 0 && asyncOperation != IntPtr.Zero;
                });
            });
        });

        if (!started && asyncOperation != IntPtr.Zero)
        {
            Marshal.Release(asyncOperation);
            asyncOperation = IntPtr.Zero;
        }

        return new StartPinRequest(started, asyncOperation);
    }

    private static async Task<bool> WaitForResultAsync(IntPtr operation,
        CancellationToken cancellationToken)
    {
        IntPtr asyncInfo = IntPtr.Zero;
        Guid interfaceId = AsyncInfoId;
        int queryResult = Marshal.QueryInterface(operation, ref interfaceId,
            out asyncInfo);
        LogFailure(nameof(WaitForResultAsync), queryResult);
        if (queryResult < 0 || asyncInfo == IntPtr.Zero) return false;

        try
        {
            IntPtr infoVtable = Marshal.ReadIntPtr(asyncInfo);
            var getStatus = GetDelegate<GetAsyncStatusDelegate>(infoVtable, 7);
            var getError = GetDelegate<GetAsyncErrorDelegate>(infoVtable, 8);

            for (int attempt = 0; attempt < AsyncCompletionPollLimit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int result = getStatus(asyncInfo, out int status);
                LogFailure(nameof(WaitForResultAsync), result);
                if (result < 0) return false;

                switch (status)
                {
                    case AsyncStatusCompleted:
                    {
                        IntPtr operationVtable = Marshal.ReadIntPtr(operation);
                        var getResults = GetDelegate<GetBooleanResultDelegate>(
                            operationVtable, 8);
                        result = getResults(operation, out byte changed);
                        LogFailure(nameof(WaitForResultAsync), result);
                        return result >= 0 && changed != 0;
                    }
                    case AsyncStatusCanceled:
                        return false;
                    case AsyncStatusError:
                        result = getError(asyncInfo, out int errorCode);
                        if (result >= 0) LogFailure(nameof(WaitForResultAsync),
                            errorCode);
                        return false;
                    case AsyncStatusStarted:
                        await Task.Delay(AsyncCompletionPollMilliseconds,
                            cancellationToken);
                        break;
                    default:
                        AppLog.Debug(nameof(WaitForResultAsync),
                            $"Windows returned unknown async status {status}.");
                        return false;
                }
            }

            AppLog.Debug(nameof(WaitForResultAsync),
                "Windows did not finish the Start pin request within 10 seconds.");
            return false;
        }
        finally
        {
            Marshal.Release(asyncInfo);
        }
    }

    private static void CreateOrUpdateShortcut(string folderPath,
        string shortcutPath)
    {
        string? shortcutDirectory = Path.GetDirectoryName(shortcutPath);
        if (string.IsNullOrWhiteSpace(shortcutDirectory))
            throw new InvalidOperationException(
                "The folder-pin shortcut directory is invalid.");
        Directory.CreateDirectory(shortcutDirectory);

        Type shellLinkType = Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"),
            throwOnError: true)!;
        object shellLinkObject = Activator.CreateInstance(shellLinkType)
            ?? throw new InvalidOperationException(
                "Windows could not create the folder-pin shortcut.");
        try
        {
            var shellLink = (NativeMethods.IShellLinkW)shellLinkObject;
            ThrowOnFailure(shellLink.SetPath(Environment.ProcessPath
                ?? Application.ExecutablePath), "set the shortcut target");
            ThrowOnFailure(shellLink.SetArguments(
                StartPinService.GetActivationArguments(folderPath)),
                "set the shortcut arguments");
            ThrowOnFailure(shellLink.SetDescription(GetDisplayName(folderPath)),
                "set the shortcut description");
            ThrowOnFailure(shellLink.SetWorkingDirectory(folderPath),
                "set the shortcut working directory");
            ThrowOnFailure(shellLink.SetIconLocation(
                Path.Combine(Environment.SystemDirectory, "shell32.dll"), 3),
                "set the shortcut icon");

            var propertyStore = (NativeMethods.IPropertyStore)shellLinkObject;
            NativeMethods.PROPERTYKEY key = AppUserModelIdKey;
            var value = new NativeMethods.PROPVARIANT
            {
                vt = VtLpwStr,
                pointerValue = Marshal.StringToCoTaskMemUni(GetAppId(folderPath)),
            };
            try
            {
                ThrowOnFailure(propertyStore.SetValue(ref key, ref value),
                    "set the shortcut application identifier");
                ThrowOnFailure(propertyStore.Commit(),
                    "save the shortcut application identifier");
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.pointerValue);
            }

            var persistFile = (IPersistFile)shellLinkObject;
            persistFile.Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLinkObject);
        }

        SHChangeNotify(File.Exists(shortcutPath) ? ShcneUpdateItem : ShcneCreate,
            ShcnfPathW, shortcutPath, null);
    }

    private static void DeleteShortcut(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return;
        File.Delete(shortcutPath);
        SHChangeNotify(ShcneUpdateItem, ShcnfPathW, shortcutPath, null);

        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static bool TryCreateUserPinnedTile(IntPtr identifier) =>
        TryGetActivationFactory(UserPinHelperClass, UserPinHelperId, factory =>
        {
            IntPtr vtable = Marshal.ReadIntPtr(factory);
            var create = GetDelegate<CreateUserPinnedTileDelegate>(vtable, 6);
            int result = create(factory, identifier);
            LogFailure(nameof(TryCreateUserPinnedTile), result);
            return result >= 0;
        });

    private static bool TryCreateTileId(IntPtr identifier,
        Func<IntPtr, bool> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        IntPtr unifiedId = IntPtr.Zero;
        try
        {
            IntPtr identifierVtable = Marshal.ReadIntPtr(identifier);
            var getUnifiedId = GetDelegate<GetStringDelegate>(
                identifierVtable, 7);
            int result = getUnifiedId(identifier, out unifiedId);
            LogFailure(nameof(TryCreateTileId), result);
            if (result < 0 || unifiedId == IntPtr.Zero) return false;

            return TryGetActivationFactory(TileIdClass, TileIdStaticsId,
                factory =>
                {
                    IntPtr tileId = IntPtr.Zero;
                    try
                    {
                        IntPtr factoryVtable = Marshal.ReadIntPtr(factory);
                        var create = GetDelegate<CreateTileIdDelegate>(
                            factoryVtable, 9);
                        int createResult = create(factory, unifiedId,
                            out tileId);
                        LogFailure(nameof(TryCreateTileId), createResult);
                        return createResult >= 0 && tileId != IntPtr.Zero
                            && operation(tileId);
                    }
                    finally
                    {
                        if (tileId != IntPtr.Zero) Marshal.Release(tileId);
                    }
                });
        }
        finally
        {
            if (unifiedId != IntPtr.Zero) WindowsDeleteString(unifiedId);
        }
    }

    private static bool TryWithIdentifier(string folderPath,
        Func<IntPtr, bool> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return TryGetActivationFactory(UnifiedTileIdentifierClass,
            Win32IdentifierFactoryId, factory =>
            {
                IntPtr appId = IntPtr.Zero;
                IntPtr identifier = IntPtr.Zero;
                try
                {
                    int result = CreateWindowsString(GetAppId(folderPath),
                        out appId);
                    if (result < 0) return false;

                    IntPtr vtable = Marshal.ReadIntPtr(factory);
                    var create = GetDelegate<CreateIdentifierDelegate>(
                        vtable, 6);
                    result = create(factory, appId, out identifier);
                    LogFailure(nameof(TryWithIdentifier), result);
                    return result >= 0 && identifier != IntPtr.Zero
                        && operation(identifier);
                }
                finally
                {
                    if (identifier != IntPtr.Zero) Marshal.Release(identifier);
                    if (appId != IntPtr.Zero) WindowsDeleteString(appId);
                }
            });
    }

    private static bool TryUseStartManager(Func<IntPtr, bool> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (TryUseStartManagerExtension(operation)) return true;

        IntPtr module = IntPtr.Zero;
        IntPtr manager = IntPtr.Zero;
        try
        {
            IntPtr window = FindWindow(StartManagerWindowClass,
                StartManagerWindowName);
            if (window == IntPtr.Zero) return false;

            module = LoadLibraryEx("twinapi.appcore.dll", IntPtr.Zero,
                LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero) return false;
            IntPtr entryPoint = GetProcAddress(module,
                (IntPtr)CoreQueryWindowServiceOrdinal);
            if (entryPoint == IntPtr.Zero) return false;

            var query = Marshal.GetDelegateForFunctionPointer<
                CoreQueryWindowServiceDelegate>(entryPoint);
            Guid serviceId = StartManagerServiceId;
            Guid interfaceId = StartManagerId;
            int result = query(window, ref serviceId, ref interfaceId,
                out manager);
            LogFailure(nameof(TryUseStartManager), result);
            return result >= 0 && manager != IntPtr.Zero
                && operation(manager);
        }
        finally
        {
            if (manager != IntPtr.Zero) Marshal.Release(manager);
            if (module != IntPtr.Zero) FreeLibrary(module);
        }
    }

    private static bool TryUseStartManagerExtension(
        Func<IntPtr, bool> operation)
    {
        return TryGetActivationFactory(ExtensionFactoryClass,
            ExtensionFactoryId, factory =>
            {
                IntPtr extensionName = IntPtr.Zero;
                IntPtr runtimeClass = IntPtr.Zero;
                IntPtr extensionObject = IntPtr.Zero;
                IntPtr manager = IntPtr.Zero;
                try
                {
                    if (CreateWindowsString(StartManagerExtensionName,
                            out extensionName) < 0
                        || CreateWindowsString(StartManagerRuntimeClass,
                            out runtimeClass) < 0)
                    {
                        return false;
                    }

                    IntPtr vtable = Marshal.ReadIntPtr(factory);
                    var getFactory = GetDelegate<GetExtensionFactoryDelegate>(
                        vtable, 10);
                    int result = getFactory(factory, extensionName,
                        runtimeClass, out extensionObject);
                    LogFailure(nameof(TryUseStartManagerExtension), result);
                    if (result < 0 || extensionObject == IntPtr.Zero)
                        return false;

                    Guid managerId = StartManagerId;
                    result = Marshal.QueryInterface(extensionObject,
                        ref managerId, out manager);
                    LogFailure(nameof(TryUseStartManagerExtension), result);
                    return result >= 0 && manager != IntPtr.Zero
                        && operation(manager);
                }
                finally
                {
                    if (manager != IntPtr.Zero) Marshal.Release(manager);
                    if (extensionObject != IntPtr.Zero)
                        Marshal.Release(extensionObject);
                    if (runtimeClass != IntPtr.Zero)
                        WindowsDeleteString(runtimeClass);
                    if (extensionName != IntPtr.Zero)
                        WindowsDeleteString(extensionName);
                }
            });
    }

    private static bool TryGetActivationFactory(string runtimeClassName,
        Guid interfaceId, Func<IntPtr, bool> operation)
    {
        IntPtr className = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        try
        {
            if (CreateWindowsString(runtimeClassName, out className) < 0)
                return false;
            int result = RoGetActivationFactory(className, ref interfaceId,
                out factory);
            LogFailure(nameof(TryGetActivationFactory), result);
            return result >= 0 && factory != IntPtr.Zero && operation(factory);
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
        }
    }

    private static T GetDelegate<T>(IntPtr vtable, int slot)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(
        Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));

    private static int CreateWindowsString(string value, out IntPtr handle) =>
        WindowsCreateString(value, (uint)value.Length, out handle);

    private static string GetAppId(string folderPath) =>
        "MultiExplorer.Folder." + StartPinService.GetTileId(folderPath)
            ["MultiExplorer-".Length..];

    private static string GetShortcutPath(string folderPath)
    {
        string pinnedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned",
            "StartMenu", "MultiExplorer", GetAppId(folderPath));
        return Path.Combine(pinnedRoot,
            SanitizeFileName(GetDisplayName(folderPath)) + ".lnk");
    }

    private static string GetDisplayName(string folderPath)
    {
        string trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? folderPath : name;
    }

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        string sanitized = string.Concat(value.Select(character =>
            invalid.Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "Folder" : sanitized;
    }

    private static void ThrowOnFailure(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"Windows could not {operation}.", result);
    }

    private static void LogFailure(string operation, int result)
    {
        if (result < 0)
            AppLog.Debug(operation,
                $"The native Start service returned 0x{result:X8}.");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateIdentifierDelegate(IntPtr @this,
        IntPtr appId, out IntPtr identifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateUserPinnedTileDelegate(IntPtr @this,
        IntPtr identifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStringDelegate(IntPtr @this,
        out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTileIdDelegate(IntPtr @this,
        IntPtr unifiedId, out IntPtr tileId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestTileChangeDelegate(IntPtr @this,
        IntPtr tileId, out IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAsyncStatusDelegate(IntPtr @this,
        out int status);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAsyncErrorDelegate(IntPtr @this,
        out int errorCode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBooleanResultDelegate(IntPtr @this,
        out byte result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetExtensionFactoryDelegate(IntPtr @this,
        IntPtr extensionName, IntPtr runtimeClass, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CoreQueryWindowServiceDelegate(IntPtr window,
        ref Guid serviceId, ref Guid interfaceId, out IntPtr service);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString,
        uint length, out IntPtr value);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr className,
        ref Guid interfaceId, out IntPtr factory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className,
        string windowName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module,
        IntPtr procedureName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags,
        string item1, string? item2);

    private readonly record struct StartPinRequest(bool Started,
        IntPtr Operation);
}
