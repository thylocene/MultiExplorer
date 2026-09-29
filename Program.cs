using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MultiExplorer;

static class Program
{
    // Registered once at startup; shared with MainForm so it can handle the message in WndProc.
    internal static readonly uint WM_SHOW_INSTANCE =
#if DEBUG
        NativeMethods.RegisterWindowMessage("MultiExplorer.ShowInstance.Debug.v1");
#else
        NativeMethods.RegisterWindowMessage("MultiExplorer.ShowInstance.v1");
#endif

    [STAThread]
    static void Main(string[] args)
    {
        if (ShouldRelaunchWithIdentity(args)
            && PackageIdentityLauncher.TryRelaunchWithIdentity(args))
            return;

        // Must be set before the first top-level window is created so the
        // taskbar cannot group MultiExplorer under a folder-pin shortcut.
        PackageIdentityLauncher.ConfigureProcessApplicationUserModelId();

        string? activationPath = StartPinService.DecodeActivationPath(args);
        if (activationPath is not null && File.Exists(activationPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(activationPath)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                AppLog.Error(ex, nameof(Main),
                    $"Could not open '{activationPath}'.");
            }
            return;
        }

        bool startedWithWindows = IsStartupLaunch(args);

        // Single-instance guard: if another instance is already running, signal it to
        // show its window and then exit immediately. A Windows sign-in launch is
        // intentionally silent if the application is already running.
        using var mutex = new Mutex(true, SingleInstanceMutexName, out bool ownsMutex);
        if (!ownsMutex)
        {
            if (activationPath is not null)
                ActivationRequestStore.TryWriteFolder(activationPath);
            if (!startedWithWindows)
                NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, WM_SHOW_INSTANCE,
                                          IntPtr.Zero, IntPtr.Zero);
            return;
        }

        // Must be called before any window is created.
        // ApplicationHighDpiMode in the csproj generates a manifest entry but
        // does NOT call this method unless ApplicationConfiguration.Initialize()
        // is used.  Our hand-written Main() must call it explicitly so WinForms
        // opts into per-monitor DPI handling rather than bitmap-scaling content.
        var settings = SettingsManager.Load();
        ThemeManager.SetCurrent(ThemeManager.Parse(settings.ApplicationTheme));

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new MainForm(settings, startedWithWindows,
            activationPath);
        Application.Run(form);
    }

    internal static bool IsStartupLaunch(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldRelaunchWithIdentity(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
#if DEBUG
        // The installed package points to its own executable. A developer build
        // must run directly so testing it never starts the installed version.
        return false;
#else
        return !args.Any(static argument =>
            argument.Equals("--no-identity-relaunch",
                StringComparison.OrdinalIgnoreCase));
#endif
    }

    internal static string SingleInstanceMutexName =>
#if DEBUG
        "MultiExplorer.SingleInstance.Debug.v1";
#else
        "MultiExplorer.SingleInstance.v1";
#endif
}
