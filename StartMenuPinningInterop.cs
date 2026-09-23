using System.Runtime.InteropServices;
using Windows.UI.StartScreen;
using WinRT;

namespace MultiExplorer;

/// <summary>
/// Creates the secondary-tile record and asks the Windows 11 Start service to
/// pin it. These interfaces are private, so failures are returned without
/// displaying Windows' separate confirmation flyout.
/// </summary>
internal static class StartMenuPinningInterop
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const string PinnableSurfaceClassName =
        "Windows.Internal.ApplicationModel.StartPinnableSurface";
    private static readonly Guid SecondaryTilePrivateId =
        new("2D7F0D3B-EC36-463B-9F69-D7238D77C122");
    private static readonly Guid PinnableSurfaceFactoryId =
        new("F27684E4-E634-4807-BE9A-4838381FCBFC");
    internal static bool TryPin(SecondaryTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        IntPtr secondaryTilePrivate = IntPtr.Zero;
        IntPtr appUserModelId = IntPtr.Zero;
        IntPtr tileAbi = IntPtr.Zero;
        try
        {
            int result = WindowsCreateString(
                PackageIdentityLauncher.ApplicationUserModelId,
                (uint)PackageIdentityLauncher.ApplicationUserModelId.Length,
                out appUserModelId);
            if (result < 0) return false;

            tileAbi = MarshalInspectable<SecondaryTile>.FromManaged(tile);
            Guid secondaryTilePrivateId = SecondaryTilePrivateId;
            result = Marshal.QueryInterface(tileAbi, ref secondaryTilePrivateId,
                out secondaryTilePrivate);
            if (result < 0 || secondaryTilePrivate == IntPtr.Zero) return false;

            bool existsInTileStore = SecondaryTile.Exists(tile.TileId);
            IntPtr privateVtable = Marshal.ReadIntPtr(secondaryTilePrivate);
            if (!existsInTileStore)
            {
                IntPtr validatePointer = Marshal.ReadIntPtr(privateVtable,
                    12 * IntPtr.Size);
                var validate = Marshal.GetDelegateForFunctionPointer<
                    NoArgumentDelegate>(validatePointer);
                result = validate(secondaryTilePrivate);
                if (result < 0)
                {
                    AppLog.Debug(nameof(TryPin),
                        $"Start tile validation returned 0x{result:X8}.");
                    return false;
                }
            }

            // Start can only pin a secondary tile after its record has been
            // written to the tile store.
            IntPtr createPointer = Marshal.ReadIntPtr(privateVtable,
                13 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<
                NoArgumentDelegate>(createPointer);
            result = create(secondaryTilePrivate);
            if (result < 0)
            {
                AppLog.Debug(nameof(TryPin),
                    $"Saving the Start tile returned 0x{result:X8}.");
                return false;
            }

            return TryUsePinnableSurface(surface =>
            {
                IntPtr vtable = Marshal.ReadIntPtr(surface);
                IntPtr pinPointer = Marshal.ReadIntPtr(vtable,
                    8 * IntPtr.Size);
                var pin = Marshal.GetDelegateForFunctionPointer<
                    PinTileDelegate>(pinPointer);
                int pinResult = pin(surface, appUserModelId, tileAbi);
                if (pinResult >= 0) return true;

                AppLog.Debug(nameof(TryPin),
                    $"The Windows 11 Start service returned 0x{pinResult:X8}.");
                return false;
            });
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            BadImageFormatException or
            COMException or
            InvalidOperationException or
            MarshalDirectiveException)
        {
            AppLog.Debug(ex, nameof(TryPin),
                "Direct Start pinning is unavailable.");
            return false;
        }
        finally
        {
            if (secondaryTilePrivate != IntPtr.Zero)
                Marshal.Release(secondaryTilePrivate);
            if (tileAbi != IntPtr.Zero)
                MarshalInspectable<SecondaryTile>.DisposeAbi(tileAbi);
            if (appUserModelId != IntPtr.Zero)
                WindowsDeleteString(appUserModelId);
        }
    }

    internal static bool TryUnpin(string tileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);
        IntPtr appUserModelId = IntPtr.Zero;
        IntPtr tileIdString = IntPtr.Zero;
        try
        {
            int result = WindowsCreateString(
                PackageIdentityLauncher.ApplicationUserModelId,
                (uint)PackageIdentityLauncher.ApplicationUserModelId.Length,
                out appUserModelId);
            if (result < 0) return false;
            result = WindowsCreateString(tileId, (uint)tileId.Length,
                out tileIdString);
            if (result < 0) return false;

            return TryUsePinnableSurface(surface =>
            {
                IntPtr vtable = Marshal.ReadIntPtr(surface);
                IntPtr unpinPointer = Marshal.ReadIntPtr(vtable,
                    11 * IntPtr.Size);
                var unpin = Marshal.GetDelegateForFunctionPointer<
                    UnpinTileDelegate>(unpinPointer);
                int unpinResult = unpin(surface, appUserModelId,
                    tileIdString);
                if (unpinResult >= 0) return true;

                AppLog.Debug(nameof(TryUnpin),
                    $"The Windows 11 Start service returned 0x{unpinResult:X8}.");
                return false;
            });
        }
        finally
        {
            if (tileIdString != IntPtr.Zero)
                WindowsDeleteString(tileIdString);
            if (appUserModelId != IntPtr.Zero)
                WindowsDeleteString(appUserModelId);
        }
    }

    internal static bool TryGetPinnedState(string tileId, out bool isPinned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);
        isPinned = false;
        IntPtr appUserModelId = IntPtr.Zero;
        IntPtr tileIdString = IntPtr.Zero;
        try
        {
            int result = WindowsCreateString(
                PackageIdentityLauncher.ApplicationUserModelId,
                (uint)PackageIdentityLauncher.ApplicationUserModelId.Length,
                out appUserModelId);
            if (result < 0) return false;
            result = WindowsCreateString(tileId, (uint)tileId.Length,
                out tileIdString);
            if (result < 0) return false;

            bool pinnedValue = false;
            bool succeeded = TryUsePinnableSurface(surface =>
            {
                IntPtr vtable = Marshal.ReadIntPtr(surface);
                IntPtr isPinnedPointer = Marshal.ReadIntPtr(vtable,
                    9 * IntPtr.Size);
                var getPinned = Marshal.GetDelegateForFunctionPointer<
                    IsTilePinnedDelegate>(isPinnedPointer);
                int stateResult = getPinned(surface, appUserModelId,
                    tileIdString, out byte pinned);
                if (stateResult < 0)
                {
                    AppLog.Debug(nameof(TryGetPinnedState),
                        $"The Windows 11 Start service returned 0x{stateResult:X8}.");
                    return false;
                }

                pinnedValue = pinned != 0;
                return true;
            });
            isPinned = pinnedValue;
            return succeeded;
        }
        finally
        {
            if (tileIdString != IntPtr.Zero)
                WindowsDeleteString(tileIdString);
            if (appUserModelId != IntPtr.Zero)
                WindowsDeleteString(appUserModelId);
        }
    }

    private static bool TryUsePinnableSurface(Func<IntPtr, bool> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        IntPtr module = IntPtr.Zero;
        IntPtr className = IntPtr.Zero;
        IntPtr activationFactory = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr surface = IntPtr.Zero;
        try
        {
            string modulePath = Path.Combine(Environment.SystemDirectory,
                "StartTileData.dll");
            module = LoadLibraryEx(modulePath, IntPtr.Zero,
                LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero) return false;

            IntPtr entryPoint = GetProcAddress(module,
                "DllGetActivationFactory");
            if (entryPoint == IntPtr.Zero) return false;

            int result = WindowsCreateString(PinnableSurfaceClassName,
                (uint)PinnableSurfaceClassName.Length, out className);
            if (result < 0) return false;

            var getActivationFactory = Marshal.GetDelegateForFunctionPointer<
                DllGetActivationFactoryDelegate>(entryPoint);
            result = getActivationFactory(className, out activationFactory);
            if (result < 0 || activationFactory == IntPtr.Zero) return false;

            Guid factoryId = PinnableSurfaceFactoryId;
            result = Marshal.QueryInterface(activationFactory, ref factoryId,
                out factory);
            if (result < 0 || factory == IntPtr.Zero) return false;

            IntPtr factoryVtable = Marshal.ReadIntPtr(factory);
            IntPtr getCurrentPointer = Marshal.ReadIntPtr(factoryVtable,
                6 * IntPtr.Size);
            var getCurrent = Marshal.GetDelegateForFunctionPointer<
                GetCurrentDelegate>(getCurrentPointer);
            result = getCurrent(factory, out surface);
            return result >= 0 && surface != IntPtr.Zero && operation(surface);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            BadImageFormatException or
            COMException or
            InvalidOperationException or
            MarshalDirectiveException)
        {
            AppLog.Debug(ex, nameof(TryUsePinnableSurface),
                "The Windows 11 Start service is unavailable.");
            return false;
        }
        finally
        {
            if (surface != IntPtr.Zero) Marshal.Release(surface);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (activationFactory != IntPtr.Zero)
                Marshal.Release(activationFactory);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
            if (module != IntPtr.Zero) FreeLibrary(module);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NoArgumentDelegate(IntPtr @this);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetActivationFactoryDelegate(
        IntPtr className, out IntPtr activationFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentDelegate(IntPtr @this,
        out IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PinTileDelegate(IntPtr @this,
        IntPtr applicationUserModelId, IntPtr tile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsTilePinnedDelegate(IntPtr @this,
        IntPtr applicationUserModelId, IntPtr tileId, out byte isPinned);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnpinTileDelegate(IntPtr @this,
        IntPtr applicationUserModelId, IntPtr tileId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi,
        SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module,
        string procedureName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        string sourceString, uint length, out IntPtr value);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);

}
