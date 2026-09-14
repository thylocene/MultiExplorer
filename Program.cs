using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MultiExplorer;

static class Program
{
    // Registered once at startup; shared with MainForm so it can handle the message in WndProc.
    internal static readonly uint WM_SHOW_INSTANCE =
        NativeMethods.RegisterWindowMessage("MultiExplorer.ShowInstance.v1");

    [STAThread]
    static void Main(string[] args)
    {
        bool startedWithWindows = IsStartupLaunch(args);

        // Single-instance guard: if another instance is already running, signal it to
        // show its window and then exit immediately. A Windows sign-in launch is
        // intentionally silent if the application is already running.
        using var mutex = new Mutex(true, "MultiExplorer.SingleInstance.v1", out bool ownsMutex);
        if (!ownsMutex)
        {
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
        // --theme=light/dark is also useful for deterministic screenshot diagnostics;
        // normal interactive launches always use the persisted selection.
        string? themeOverride = args.FirstOrDefault(a =>
            a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase));
        string selectedTheme = themeOverride is null
            ? settings.ApplicationTheme
            : themeOverride["--theme=".Length..];
        ThemeManager.SetCurrent(ThemeManager.Parse(selectedTheme));

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        int captureIndex = Array.FindIndex(args, a => a == "--capture");
        var form = new MainForm(settings, startedWithWindows);
        if (captureIndex >= 0 && captureIndex + 1 < args.Length)
        {
            string capturePath = args[captureIndex + 1];
            bool selectAllForCapture = args.Any(a => a == "--select-all");
            form.Shown += (_, _) =>
            {
                var timer = new System.Windows.Forms.Timer { Interval = 2000 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (selectAllForCapture)
                    {
                        ExplorerHost? host = FindControls<ExplorerHost>(form).FirstOrDefault(h => h.Visible);
                        if (host != null)
                        {
                            host.FocusShellView();
                            host.SelectAll();
                            Application.DoEvents();
                        }
                    }
                    using var image = new Bitmap(form.Width, form.Height);
                    using (Graphics graphics = Graphics.FromImage(image))
                        graphics.CopyFromScreen(form.Left, form.Top, 0, 0, image.Size);
                    image.Save(capturePath, ImageFormat.Png);
                    var windows = new List<string>();
                    try { DumpWindows(form.Handle, 0, windows); }
                    catch (System.Exception ex) { windows.Add(ex.ToString()); }
                    System.IO.File.WriteAllLines(capturePath + ".txt", windows);
                    // Application.Exit produces ApplicationExitCall rather than
                    // UserClosing, so the diagnostic run cannot remain hidden in
                    // the tray when MinimizeToTray is enabled.
                    Application.Exit();
                };
                timer.Start();
            };
        }
        Application.Run(form);
    }

    internal static bool IsStartupLaunch(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (T descendant in FindControls<T>(child))
                yield return descendant;
        }
    }

    private static void DumpWindows(System.IntPtr parent, int depth, List<string> output)
    {
        if (depth >= 12 || output.Count >= 1000) return;
        System.IntPtr child = NativeMethods.GetWindow(parent, 5);
        while (child != System.IntPtr.Zero
               && NativeMethods.GetParent(child) == parent
               && output.Count < 1000)
        {
            var name = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(child, name, name.Capacity);
            NativeMethods.GetWindowRect(child, out NativeMethods.RECT rect);
            output.Add($"{new string(' ', depth * 2)}0x{child.ToInt64():X} {name} " +
                       $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            DumpWindows(child, depth + 1, output);
            System.IntPtr next = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
            if (next == child) break;
            child = next;
        }
    }
}
