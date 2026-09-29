using System.Runtime.InteropServices;
using System.Text;

namespace MultiExplorer;

/// <summary>
/// Restarts the normal Win32 executable through MultiExplorer's sparse package
/// identity. The identity gives the app access to Windows secondary Start tiles
/// without moving the installed application into an MSIX container.
/// </summary>
internal static class PackageIdentityLauncher
{
    private const int AppModelErrorNoPackage = 15700;
    private const string UnpackagedApplicationUserModelId =
        "MultiExplorer.Application";
    internal const string ApplicationUserModelId =
        "MultiExplorer.Identity_h8abdfnbej3qa!App";
    private static readonly Guid ActivationManagerClassId =
        new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    internal static bool HasPackageIdentity()
    {
        int length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    internal static void ConfigureProcessApplicationUserModelId()
    {
        // A packaged launch already has the application ID declared by the
        // package manifest. Unpackaged launches need an explicit, stable ID;
        // otherwise Windows can associate this process with one of the folder
        // pin shortcuts that also target MultiExplorer.exe and then show that
        // folder's name and icon on the taskbar.
        if (HasPackageIdentity()) return;

        int result = SetCurrentProcessExplicitAppUserModelID(
            UnpackagedApplicationUserModelId);
        if (result < 0)
        {
            AppLog.Debug(nameof(ConfigureProcessApplicationUserModelId),
                $"Windows could not set the process application identifier " +
                $"(0x{result:X8}).");
        }
    }

    internal static bool TryRelaunchWithIdentity(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (HasPackageIdentity()) return false;

        object? activationManagerObject = null;
        try
        {
            Type activationManagerType = Type.GetTypeFromCLSID(
                ActivationManagerClassId, throwOnError: true)!;
            activationManagerObject = Activator.CreateInstance(
                activationManagerType);
            if (activationManagerObject is not IApplicationActivationManager manager)
                return false;

            string commandLine = string.Join(' ',
                arguments.Select(QuoteCommandLineArgument));
            int result = manager.ActivateApplication(
                ApplicationUserModelId, commandLine, 0, out _);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
            return true;
        }
        catch (Exception ex) when (
            ex is COMException or
            InvalidCastException or
            TypeLoadException)
        {
            // The identity is optional for portable and developer builds. If it
            // is not registered, continue as a normal unpackaged application.
            return false;
        }
        finally
        {
            if (activationManagerObject is not null
                && Marshal.IsComObject(activationManagerObject))
                Marshal.FinalReleaseComObject(activationManagerObject);
        }
    }

    internal static string QuoteCommandLineArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0
            && !argument.Any(static character =>
                char.IsWhiteSpace(character) || character == '"'))
            return argument;

        var quoted = new StringBuilder(argument.Length + 2);
        quoted.Append('"');
        int backslashCount = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
                quoted.Append('\\', backslashCount * 2 + 1);
            else
                quoted.Append('\\', backslashCount);

            quoted.Append(character);
            backslashCount = 0;
        }

        quoted.Append('\\', backslashCount * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        string applicationUserModelId);

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            uint options,
            out uint processId);

        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
            IntPtr shellItemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId);

        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
            IntPtr shellItemArray,
            out uint processId);
    }
}
