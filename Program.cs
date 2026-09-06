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
        // Single-instance guard: if another instance is already running, signal it to
        // show its window and then exit immediately.
        using var mutex = new Mutex(true, "MultiExplorer.SingleInstance.v1", out bool ownsMutex);
        if (!ownsMutex)
        {
            NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, WM_SHOW_INSTANCE,
                                      IntPtr.Zero, IntPtr.Zero);
            return;
        }

        // Must be called before any window is created.
        // ApplicationHighDpiMode in the csproj generates a manifest entry but
        // does NOT call this method unless ApplicationConfiguration.Initialize()
        // is used.  Our hand-written Main() must call it explicitly so WinForms
        // opts into per-monitor DPI handling rather than bitmap-scaling content.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new MainForm();
        if (args.Length == 2 && args[0] == "--capture")
        {
            form.Shown += (_, _) =>
            {
                var timer = new System.Windows.Forms.Timer { Interval = 2000 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    using var image = new Bitmap(form.Width, form.Height);
                    using (Graphics graphics = Graphics.FromImage(image))
                        graphics.CopyFromScreen(form.Left, form.Top, 0, 0, image.Size);
                    image.Save(args[1], ImageFormat.Png);
                    var windows = new List<string>();
                    try { DumpWindows(form.Handle, 0, windows); }
                    catch (System.Exception ex) { windows.Add(ex.ToString()); }
                    System.IO.File.WriteAllLines(args[1] + ".txt", windows);
                    form.Close();
                };
                timer.Start();
            };
        }
        Application.Run(form);
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
