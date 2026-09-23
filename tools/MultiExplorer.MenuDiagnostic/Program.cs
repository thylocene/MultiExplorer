using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using MultiExplorer;
using Windows.UI.StartScreen;

namespace MultiExplorer.MenuDiagnostic;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppLog.MessageLogged += static entry =>
            Console.Error.WriteLine(entry.Message);

        if (args.Length == 2
            && args[0].Equals("--dump-menu", StringComparison.OrdinalIgnoreCase))
            return DumpMenu(Path.GetFullPath(args[1]), supportedOnly: false);

        if (args.Length is 1 or 2
            && args[0].Equals("--secondary-tile-capability",
                StringComparison.OrdinalIgnoreCase))
            return CheckSecondaryTileCapability(
                args.Length == 2 ? Path.GetFullPath(args[1]) : null);

        if (args.Length == 3
            && args[0].Equals("--secondary-tile-roundtrip",
                StringComparison.OrdinalIgnoreCase))
        {
            return TestSecondaryTileRoundTrip(
                Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        }

        if (args.Length == 2
            && args[0].Equals("--dump-supported-menu",
                StringComparison.OrdinalIgnoreCase))
            return DumpMenu(Path.GetFullPath(args[1]), supportedOnly: true);

        if (args.Length == 2
            && args[0].Equals("--quick-access-state",
                StringComparison.OrdinalIgnoreCase))
        {
            string folderPath = Path.GetFullPath(args[1]);
            bool isPinned = QuickAccessService.IsPinned(folderPath);
            Console.WriteLine(isPinned ? "pinned" : "not pinned");
            return isPinned ? 0 : 1;
        }

        if (args.Length == 3
            && args[0].Equals("--set-quick-access",
                StringComparison.OrdinalIgnoreCase))
        {
            string folderPath = Path.GetFullPath(args[1]);
            bool pin = args[2].Equals("pin", StringComparison.OrdinalIgnoreCase);
            if (!pin && !args[2].Equals("unpin",
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The action must be pin or unpin.");

            bool dispatched = QuickAccessService.TrySetPinned(folderPath, pin);
            Console.WriteLine(dispatched ? "command sent" : "command failed");
            return dispatched ? 0 : 1;
        }

        if (args.Length == 3
            && args[0].Equals("--invoke-verb",
                StringComparison.OrdinalIgnoreCase))
            return InvokeNamedVerb(Path.GetFullPath(args[1]), args[2]);

        if (args.Length == 3
            && args[0].Equals("--invoke-menu-label",
                StringComparison.OrdinalIgnoreCase))
            return InvokeMenuLabel(Path.GetFullPath(args[1]), args[2]);

        if (args.Length is 3 or 4
            && args[0].Equals("--live-invoke",
                StringComparison.OrdinalIgnoreCase))
            return InvokeThroughLiveView(Path.GetFullPath(args[1]), args[2],
                args.Length == 4 ? Path.GetFullPath(args[3]) : null);

        if (args.Length is 2 or 3
            && args[0].Equals("--share", StringComparison.OrdinalIgnoreCase))
            return TestMultiExplorerShareDialog(Path.GetFullPath(args[1]),
                args.Length == 3 ? Path.GetFullPath(args[2]) : null);

        if (args.Length == 2
            && args[0].Equals("--send-to-check", StringComparison.OrdinalIgnoreCase))
            return TestSendToMenu(Path.GetFullPath(args[1]));

        if (args.Length == 3
            && args[0].Equals("--native-share", StringComparison.OrdinalIgnoreCase))
            return TestNativeShare(Path.GetFullPath(args[1]),
                Path.GetFullPath(args[2]));

        if (args.Length == 3
            && args[0].Equals("--shell-application-share",
                StringComparison.OrdinalIgnoreCase))
            return TestShellApplicationShare(Path.GetFullPath(args[1]),
                Path.GetFullPath(args[2]));

        if (args.Length == 3
            && args[0].Equals("--command-bar-click", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse(args[2], true, out ShellContextCommand expected))
            {
                Console.Error.WriteLine($"Unknown command-bar button: {args[2]}");
                return 2;
            }

            return TestCommandBarClick(Path.GetFullPath(args[1]), expected);
        }

        if (args.Length != 4)
        {
            Console.Error.WriteLine(
                "Usage: MultiExplorer.MenuDiagnostic <path> <menu-label> <output.png> <custom|native>\n" +
                "   or: MultiExplorer.MenuDiagnostic --secondary-tile-capability\n" +
                "   or: MultiExplorer.MenuDiagnostic --dump-menu <path>\n" +
                "   or: MultiExplorer.MenuDiagnostic --dump-supported-menu <path>\n" +
                "   or: MultiExplorer.MenuDiagnostic --quick-access-state <folder>\n" +
                "   or: MultiExplorer.MenuDiagnostic --set-quick-access <folder> <pin|unpin>\n" +
                "   or: MultiExplorer.MenuDiagnostic --invoke-verb <path> <verb>\n" +
                "   or: MultiExplorer.MenuDiagnostic --invoke-menu-label <path> <label>\n" +
                "   or: MultiExplorer.MenuDiagnostic --live-invoke <path> <verb> [result.txt]\n" +
                "   or: MultiExplorer.MenuDiagnostic --command-bar-click <path> <button>\n" +
                "   or: MultiExplorer.MenuDiagnostic --share <file> [output.png]\n" +
                "   or: MultiExplorer.MenuDiagnostic --send-to-check <file>\n" +
                "   or: MultiExplorer.MenuDiagnostic --native-share <file> <output.png>\n" +
                "   or: MultiExplorer.MenuDiagnostic --shell-application-share <file> <output.png>");
            return 2;
        }

        string path = Path.GetFullPath(args[0]);
        string targetLabel = args[1];
        string outputPath = Path.GetFullPath(args[2]);
        bool customRendering = args[3].Equals("custom",
            StringComparison.OrdinalIgnoreCase);
        if (!customRendering && !args[3].Equals("native",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Rendering mode must be custom or native.");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        ThemeManager.SetCurrent(ApplicationTheme.Dark);

        using var owner = new Form
        {
            Text = $"MultiExplorer menu diagnostic ({args[3]})",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80),
            ClientSize = new Size(260, 80),
            ShowInTaskbar = false,
            TopMost = true,
        };
        owner.Show();
        owner.Activate();

        using var automation = new PopupAutomation(
            NativeMethods.GetCurrentThreadId(), owner.Handle, targetLabel, outputPath);
        NativeMethods.IContextMenu contextMenu = CreateContextMenu(path, owner.Handle,
            out NativeMethods.IShellFolder folder, out IntPtr absolutePidl);
        try
        {
            automation.Start();
            ShellContextMenu.Show(contextMenu, owner.Handle,
                new Point(owner.Right + 20, owner.Top + 20),
                executeCommandBarCommand: static _ => true,
                isCommandBarCommandEnabled: static _ => true,
                dpi: owner.DeviceDpi,
                useCustomDarkRendering: customRendering);
        }
        finally
        {
            Marshal.ReleaseComObject(contextMenu);
            Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(absolutePidl);
        }

        if (!automation.Succeeded)
        {
            Console.Error.WriteLine(automation.ErrorMessage);
            return 1;
        }

        Console.WriteLine(outputPath);
        Console.WriteLine(Path.ChangeExtension(outputPath, ".txt"));
        return 0;
    }

    private static int CheckSecondaryTileCapability(string? outputPath)
    {
        try
        {
            bool exists = SecondaryTile.Exists("MultiExplorerCapabilityProbe");
            string message = $"available; probe exists={exists}";
            Console.WriteLine(message);
            if (outputPath is not null) File.WriteAllText(outputPath, message);
            return 0;
        }
        catch (Exception ex)
        {
            string message = $"unavailable; 0x{ex.HResult:X8}; {ex.Message}";
            Console.WriteLine(message);
            if (outputPath is not null) File.WriteAllText(outputPath, message);
            return 1;
        }
    }

    private static int TestSecondaryTileRoundTrip(
        string targetPath, string outputPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        using var owner = new Form
        {
            Text = "MultiExplorer Start pin test",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(360, 100),
            TopMost = true,
        };
        int result = 1;
        owner.Shown += async (_, _) =>
        {
            const string tileId = "MultiExplorerCapabilityProbe";
            string message;
            try
            {
                var tile = new SecondaryTile(
                    tileId,
                    "MultiExplorer pin test",
                    $"--open \"{targetPath}\"",
                    new Uri("ms-appx:///Assets/Square150x150Logo.png"),
                    TileSize.Square150x150);
                WinRT.Interop.InitializeWithWindow.Initialize(tile, owner.Handle);

                // The platform asks the user to confirm a secondary tile. The
                // diagnostic accepts its own confirmation and removes the tile
                // immediately; the application itself never automates this UI.
                using var confirmation = new System.Threading.Timer(_ =>
                {
                    if (!owner.IsDisposed)
                        owner.BeginInvoke(() => SendKeys.SendWait("{ENTER}"));
                }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

                bool created = await tile.RequestCreateAsync();
                bool removed = !created
                    || await StartScreenManager.GetDefault()
                        .TryRemoveSecondaryTileAsync(tileId);
                message = $"created={created}; removed={removed}; "
                    + $"stillExists={SecondaryTile.Exists(tileId)}";
                result = created && removed ? 0 : 1;
            }
            catch (Exception ex)
            {
                message = $"failed; 0x{ex.HResult:X8}; {ex.Message}";
            }

            File.WriteAllText(outputPath, message);
            owner.Close();
        };

        Application.Run(owner);
        return result;
    }

    private static int DumpMenu(string path, bool supportedOnly)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var owner = new Form { ShowInTaskbar = false };
        owner.CreateControl();

        NativeMethods.IContextMenu contextMenu = CreateContextMenu(path, owner.Handle,
            out NativeMethods.IShellFolder folder, out IntPtr absolutePidl);
        IntPtr menu = NativeMethods.CreatePopupMenu();
        try
        {
            if (contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF,
                    NativeMethods.CMF_EXPLORE) < 0)
                return 1;

            if (supportedOnly)
                ShellContextMenu.RemoveUnavailableCanonicalCommands(menu,
                    contextMenu);

            var label = new StringBuilder(256);
            int count = NativeMethods.GetMenuItemCount(menu);
            for (int position = 0; position < count; position++)
            {
                uint commandId = NativeGetMenuItemID(menu, position);
                label.Clear();
                NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION);
                string verb = commandId is 0 or uint.MaxValue or > 0x7FFF
                    ? string.Empty
                    : GetCanonicalVerb(contextMenu, commandId - 1);
                Console.WriteLine(
                    $"{position,2}: id={commandId,5} verb={verb,-24} label={label}");
            }

            return 0;
        }
        finally
        {
            if (menu != IntPtr.Zero) NativeMethods.DestroyMenu(menu);
            Marshal.ReleaseComObject(contextMenu);
            Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(absolutePidl);
        }
    }

    private static int InvokeNamedVerb(string path, string verb)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var owner = new Form { ShowInTaskbar = false };
        owner.CreateControl();

        NativeMethods.IContextMenu contextMenu = CreateContextMenu(path,
            owner.Handle, out NativeMethods.IShellFolder folder,
            out IntPtr absolutePidl);
        try
        {
            bool invoked = ShellContextMenu.TryInvokeNamedShellCommand(
                contextMenu, owner.Handle, prepareExtensionVerbs: true, verb);
            Console.WriteLine(invoked ? "command sent" : "command failed");
            return invoked ? 0 : 1;
        }
        finally
        {
            Marshal.ReleaseComObject(contextMenu);
            Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(absolutePidl);
        }
    }

    private static int InvokeMenuLabel(string path, string targetLabel)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var owner = new Form { ShowInTaskbar = false };
        owner.CreateControl();

        NativeMethods.IContextMenu contextMenu = CreateContextMenu(path,
            owner.Handle, out NativeMethods.IShellFolder folder,
            out IntPtr absolutePidl);
        IntPtr menu = NativeMethods.CreatePopupMenu();
        try
        {
            if (contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF,
                    NativeMethods.CMF_EXPLORE) < 0)
                return 1;

            var label = new StringBuilder(256);
            int count = NativeMethods.GetMenuItemCount(menu);
            for (int position = 0; position < count; position++)
            {
                label.Clear();
                NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION);
                if (!ShellContextMenu.NormalizeMenuLabel(label.ToString()).Equals(
                        targetLabel, StringComparison.OrdinalIgnoreCase))
                    continue;

                uint commandId = NativeGetMenuItemID(menu, position);
                var invocation = new NativeMethods.CMINVOKECOMMANDINFO
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.CMINVOKECOMMANDINFO>(),
                    fMask = 0x00004000 | 0x20000000,
                    hwnd = owner.Handle,
                    lpVerb = (IntPtr)(commandId - 1),
                    lpVerbW = (IntPtr)(commandId - 1),
                    nShow = 1,
                    ptInvoke = new NativeMethods.POINT(owner.Left, owner.Top),
                };
                int result = contextMenu.InvokeCommand(ref invocation);
                Console.WriteLine($"InvokeCommand returned 0x{result:X8}.");
                return result >= 0 ? 0 : 1;
            }

            Console.Error.WriteLine($"'{targetLabel}' was not present.");
            return 1;
        }
        finally
        {
            if (menu != IntPtr.Zero) NativeMethods.DestroyMenu(menu);
            Marshal.ReleaseComObject(contextMenu);
            Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(absolutePidl);
        }
    }

    private static int InvokeThroughLiveView(string path, string canonicalVerb,
        string? outputPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        string parentPath = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The selected path has no parent folder.");
        using var owner = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2000, -2000),
            ClientSize = new Size(640, 480),
        };
        var host = new ExplorerHost(parentPath) { Dock = DockStyle.Fill };
        owner.Controls.Add(host);

        int result = 1;
        bool started = false;
        host.InitialNavigationCompleted += async (_, _) =>
        {
            if (started) return;
            started = true;
            await Task.Delay(750);
            Point invocationPoint = owner.PointToScreen(new Point(20, 20));
            bool invoked = host.TryInvokeSelectedShellCommand(
                path, canonicalVerb, invocationPoint);
            string message = (invoked ? "command sent" : "command failed")
                + $"; administrator={new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}";
            Console.WriteLine(message);
            if (outputPath is not null)
                File.WriteAllText(outputPath, message);
            result = invoked ? 0 : 1;
            owner.Close();
        };
        owner.Shown += (_, _) => host.LaunchExplorer();
        Application.Run(owner);
        return result;
    }

    private static string GetCanonicalVerb(NativeMethods.IContextMenu contextMenu,
        uint commandOffset)
    {
        const uint GcsVerbW = 4;
        const int CharacterCapacity = 260;
        IntPtr buffer = Marshal.AllocHGlobal(CharacterCapacity * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return contextMenu.GetCommandString((UIntPtr)commandOffset, GcsVerbW,
                       IntPtr.Zero, buffer, CharacterCapacity) >= 0
                ? Marshal.PtrToStringUni(buffer) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetMenuItemID")]
    private static extern uint NativeGetMenuItemID(IntPtr menu, int position);


    private static int TestMultiExplorerShareDialog(string path, string? outputPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        ThemeManager.SetCurrent(ApplicationTheme.Dark);

        using var owner = new Form
        {
            Text = "MultiExplorer sharing diagnostic",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(320, 100),
            ShowInTaskbar = false,
            TopMost = true,
        };
        owner.Shown += (_, _) =>
        {
            using var dialog = new ShareDialog([path], new FileSharingService(),
                static (_, _) => { }, static (_, _) => true);
            using var captureTimer = new System.Windows.Forms.Timer
            {
                Interval = 1000,
                Enabled = outputPath is not null,
            };
            captureTimer.Tick += (_, _) =>
            {
                captureTimer.Stop();
                if (outputPath is not null)
                {
                    CaptureWindow(dialog.Handle, outputPath);
                    Console.WriteLine(outputPath);
                }
                dialog.Close();
            };
            dialog.ShowDialog(owner);
            owner.Close();
        };
        Application.Run(owner);
        return 0;
    }

    private static int TestNativeShare(string path, string outputPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        using var owner = new Form
        {
            Text = "MultiExplorer native sharing diagnostic",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(320, 100),
            TopMost = true,
        };
        int result = 1;
        owner.Shown += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            owner.Activate();
            NativeMethods.IContextMenu contextMenu = CreateContextMenu(path,
                owner.Handle, out NativeMethods.IShellFolder folder,
                out IntPtr absolutePidl);
            try
            {
                bool invoked = ShellContextMenu.TryInvokeNamedShellCommand(
                    contextMenu, owner.Handle, prepareExtensionVerbs: true,
                    "launchsharedialog", "Windows.ModernShareFlyout",
                    "Windows.ModernShare", "share");
                Console.WriteLine(invoked
                    ? "Native Shell share command: invoked"
                    : "Native Shell share command: unavailable");
                result = invoked ? 0 : 1;
                if (invoked)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    CaptureForegroundWindow(outputPath);
                    Console.WriteLine(outputPath);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(contextMenu);
                Marshal.ReleaseComObject(folder);
                NativeMethods.CoTaskMemFree(absolutePidl);
                owner.Close();
            }
        };
        Application.Run(owner);
        return result;
    }

    private static int TestSendToMenu(string path)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        ThemeManager.SetCurrent(ApplicationTheme.Dark);

        using var owner = new Form
        {
            Text = "MultiExplorer Send to diagnostic",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(320, 100),
            ShowInTaskbar = false,
            TopMost = true,
        };
        bool available = false;
        owner.Shown += (_, _) =>
        {
            NativeMethods.IContextMenu contextMenu = CreateContextMenu(path,
                owner.Handle, out NativeMethods.IShellFolder folder,
                out IntPtr absolutePidl);
            try
            {
                using var closeTimer = new System.Threading.Timer(_ =>
                    NativeMethods.SendMessageI(owner.Handle, 0x001F,
                        IntPtr.Zero, IntPtr.Zero), null,
                    TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                available = ShellContextMenu.ShowSubmenu(contextMenu,
                    owner.Handle, new Point(owner.Left, owner.Bottom), "Send to");
            }
            finally
            {
                Marshal.ReleaseComObject(contextMenu);
                Marshal.ReleaseComObject(folder);
                NativeMethods.CoTaskMemFree(absolutePidl);
                owner.Close();
            }
        };
        Application.Run(owner);
        Console.WriteLine(available
            ? "Windows Send to menu: available"
            : "Windows Send to menu: unavailable");
        return available ? 0 : 1;
    }

    private static int TestShellApplicationShare(string path, string outputPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        using var owner = new Form
        {
            Text = "MultiExplorer Shell automation sharing diagnostic",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(320, 100),
            TopMost = true,
        };
        int result = 1;
        owner.Shown += async (_, _) =>
        {
            object? shell = null;
            object? folder = null;
            object? item = null;
            object? verbs = null;
            try
            {
                SendKeys.SendWait("{ESC}");
                owner.Activate();
                Type shellType = Type.GetTypeFromProgID("Shell.Application",
                    throwOnError: true)!;
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException(
                        "Windows did not create the Shell automation service.");
                dynamic shellAutomation = shell;
                folder = shellAutomation.NameSpace(Path.GetDirectoryName(path));
                if (folder is null)
                    throw new InvalidOperationException(
                        "Windows did not open the file's parent folder.");
                dynamic shellFolder = folder;
                item = shellFolder.ParseName(Path.GetFileName(path));
                if (item is null)
                    throw new InvalidOperationException(
                        "Windows did not find the selected file.");
                dynamic shellItem = item;
                verbs = shellItem.Verbs();
                dynamic shellVerbs = verbs;
                bool invoked = false;
                for (int index = 0; index < shellVerbs.Count; index++)
                {
                    object verb = shellVerbs.Item(index);
                    try
                    {
                        dynamic shellVerb = verb;
                        string name = ((string)shellVerb.Name)
                            .Replace("&", string.Empty, StringComparison.Ordinal)
                            .Trim();
                        if (!name.Equals("Share", StringComparison.OrdinalIgnoreCase))
                            continue;

                        shellVerb.DoIt();
                        invoked = true;
                        break;
                    }
                    finally
                    {
                        if (Marshal.IsComObject(verb))
                            Marshal.FinalReleaseComObject(verb);
                    }
                }
                if (!invoked)
                    throw new InvalidOperationException(
                        "Windows did not return a Share command for the file.");

                await Task.Delay(TimeSpan.FromSeconds(5));
                CaptureForegroundWindow(outputPath);
                Console.WriteLine(outputPath);
                result = 0;
                SendKeys.SendWait("{ESC}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            finally
            {
                if (verbs is not null && Marshal.IsComObject(verbs))
                    Marshal.FinalReleaseComObject(verbs);
                if (item is not null && Marshal.IsComObject(item))
                    Marshal.FinalReleaseComObject(item);
                if (folder is not null && Marshal.IsComObject(folder))
                    Marshal.FinalReleaseComObject(folder);
                if (shell is not null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
                owner.Close();
            }
        };
        Application.Run(owner);
        return result;
    }

    private static void CaptureForegroundWindow(string outputPath)
    {
        IntPtr window = GetForegroundWindow();
        CaptureWindow(window, outputPath);
    }

    private static void CaptureWindow(IntPtr window, string outputPath)
    {
        if (window == IntPtr.Zero
            || !NativeMethods.GetWindowRect(window, out NativeMethods.RECT rectangle))
            throw new InvalidOperationException(
                "The diagnostic could not find the requested window.");

        var bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top,
            rectangle.Right, rectangle.Bottom);
        bounds.Intersect(SystemInformation.VirtualScreen);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException(
                "The sharing diagnostic found an empty foreground window.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? Environment.CurrentDirectory);
        using var image = new Bitmap(bounds.Width, bounds.Height,
            PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            bool printed;
            IntPtr deviceContext = graphics.GetHdc();
            try
            {
                printed = PrintWindow(window, deviceContext, 2);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
            if (!printed)
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }
        image.Save(outputPath, ImageFormat.Png);

        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(window, title, title.Capacity);
        File.WriteAllText(Path.ChangeExtension(outputPath, ".txt"),
            $"HWND=0x{window.ToInt64():X}{Environment.NewLine}"
            + $"Title={title}{Environment.NewLine}Bounds={bounds}{Environment.NewLine}");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext,
        uint flags);

    private static int TestCommandBarClick(string path,
        ShellContextCommand expected)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        ThemeManager.SetCurrent(ApplicationTheme.Light);

        using var owner = new Form
        {
            Text = $"MultiExplorer command-bar diagnostic ({expected})",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80),
            ClientSize = new Size(260, 80),
            ShowInTaskbar = false,
            TopMost = true,
        };
        owner.Show();
        owner.Activate();

        ShellContextCommand? selected = null;
        using var automation = new CommandBarClickAutomation(
            NativeMethods.GetCurrentThreadId(), owner.Handle, expected);
        NativeMethods.IContextMenu contextMenu = CreateContextMenu(path, owner.Handle,
            out NativeMethods.IShellFolder folder, out IntPtr absolutePidl);
        try
        {
            automation.Start();
            ShellContextMenu.Show(contextMenu, owner.Handle,
                new Point(owner.Right + 20, owner.Top + 20),
                executeCommandBarCommand: command =>
                {
                    selected = command;
                    return true;
                },
                isCommandBarCommandEnabled: static _ => true,
                dpi: owner.DeviceDpi,
                useCustomDarkRendering: false);
        }
        finally
        {
            Marshal.ReleaseComObject(contextMenu);
            Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(absolutePidl);
        }

        if (automation.ErrorMessage is { Length: > 0 })
        {
            Console.Error.WriteLine(automation.ErrorMessage);
            return 1;
        }

        if (selected != expected)
        {
            Console.Error.WriteLine(
                $"Clicked {expected}, but MultiExplorer resolved {selected?.ToString() ?? "nothing"}.");
            return 1;
        }

        Console.WriteLine($"{expected}: passed");
        return 0;
    }

    private static NativeMethods.IContextMenu CreateContextMenu(string path,
        IntPtr owner, out NativeMethods.IShellFolder folder, out IntPtr absolutePidl)
    {
        int result = NativeMethods.SHParseDisplayName(path, IntPtr.Zero,
            out absolutePidl, 0, out _);
        if (result != 0 || absolutePidl == IntPtr.Zero)
            throw new InvalidOperationException($"Could not parse '{path}' (0x{result:X8}).");

        var folderId = typeof(NativeMethods.IShellFolder).GUID;
        result = NativeMethods.SHBindToParent(absolutePidl, ref folderId,
            out IntPtr folderPointer, out IntPtr childPidl);
        if (result != 0 || folderPointer == IntPtr.Zero)
        {
            NativeMethods.CoTaskMemFree(absolutePidl);
            throw new InvalidOperationException(
                $"Could not bind to the parent of '{path}' (0x{result:X8}).");
        }

        folder = (NativeMethods.IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
        Marshal.Release(folderPointer);
        var contextMenuId = typeof(NativeMethods.IContextMenu).GUID;
        result = folder.GetUIObjectOf(owner, 1, [childPidl], ref contextMenuId,
            IntPtr.Zero, out IntPtr contextMenuPointer);
        if (result != 0 || contextMenuPointer == IntPtr.Zero)
        {
            Marshal.ReleaseComObject(folder);
            NativeMethods.CoTaskMemFree(absolutePidl);
            throw new InvalidOperationException(
                $"Could not create the context menu for '{path}' (0x{result:X8}).");
        }

        try
        {
            return (NativeMethods.IContextMenu)
                Marshal.GetObjectForIUnknown(contextMenuPointer);
        }
        finally
        {
            Marshal.Release(contextMenuPointer);
        }
    }

    private sealed class CommandBarClickAutomation : IDisposable
    {
        private const uint MnGetHMenu = 0x01E1;
        private const uint WmCancelMode = 0x001F;
        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;
        private readonly uint _threadId;
        private readonly IntPtr _owner;
        private readonly ShellContextCommand _expected;
        private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(10);
        private readonly object _gate = new();
        private System.Threading.Timer? _timer;
        private bool _finished;

        internal CommandBarClickAutomation(uint threadId, IntPtr owner,
            ShellContextCommand expected)
        {
            _threadId = threadId;
            _owner = owner;
            _expected = expected;
        }

        internal string ErrorMessage { get; private set; } = string.Empty;

        internal void Start() => _timer = new System.Threading.Timer(
            Inspect, null, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));

        private void Inspect(object? state)
        {
            lock (_gate)
            {
                if (_finished) return;

                if (TryFindCommandRow(out NativeMethods.RECT bounds))
                {
                    int index = Array.IndexOf(ShellContextCommandBar.Commands, _expected);
                    int x = bounds.Left + (2 * index + 1)
                        * (bounds.Right - bounds.Left)
                        / (2 * ShellContextCommandBar.Commands.Length);
                    int y = bounds.Top + (bounds.Bottom - bounds.Top) / 2;
                    SetCursorPos(x, y);
                    mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
                    _finished = true;
                    _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                    return;
                }

                if (DateTime.UtcNow < _deadline) return;

                ErrorMessage = "Timed out locating the context-menu action row.";
                _finished = true;
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                PostMessage(_owner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private bool TryFindCommandRow(out NativeMethods.RECT bounds)
        {
            NativeMethods.RECT locatedBounds = default;
            bool found = false;
            EnumThreadWindows(_threadId, (window, _) =>
            {
                var className = new StringBuilder(32);
                GetClassName(window, className, className.Capacity);
                if (!className.ToString().Equals("#32768", StringComparison.Ordinal)
                    || !IsWindowVisible(window))
                    return true;

                IntPtr menu = NativeMethods.SendMessageI(window, MnGetHMenu,
                    IntPtr.Zero, IntPtr.Zero);
                int itemCount = NativeMethods.GetMenuItemCount(menu);
                for (int position = 0; position < itemCount; position++)
                {
                    if (GetMenuItemID(menu, position) != ShellContextCommandBar.CommandId
                        || !GetMenuItemRect(IntPtr.Zero, menu, (uint)position,
                            out locatedBounds))
                        continue;

                    found = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            bounds = locatedBounds;
            return found;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (!_finished)
                    PostMessage(_owner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
                _finished = true;
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                _timer?.Dispose();
            }
        }

        private delegate bool EnumThreadWindowCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId,
            EnumThreadWindowCallback callback, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className,
            int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetMenuItemID(IntPtr menu, int position);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu,
            uint item, out NativeMethods.RECT rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint x, uint y,
            uint data, UIntPtr extraInfo);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message,
            IntPtr wParam, IntPtr lParam);
    }

    private sealed class PopupAutomation : IDisposable
    {
        private const uint MnGetHMenu = 0x01E1;
        private const uint WmCancelMode = 0x001F;
        private readonly uint _threadId;
        private readonly IntPtr _owner;
        private readonly string _targetLabel;
        private readonly string _outputPath;
        private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(12);
        private readonly object _gate = new();
        private System.Threading.Timer? _timer;
        private DateTime? _hoveredAt;
        private bool _finished;

        internal PopupAutomation(uint threadId, IntPtr owner, string targetLabel,
            string outputPath)
        {
            _threadId = threadId;
            _owner = owner;
            _targetLabel = targetLabel;
            _outputPath = outputPath;
        }

        internal bool Succeeded { get; private set; }
        internal string ErrorMessage { get; private set; } = "Diagnostic did not complete.";

        internal void Start() => _timer = new System.Threading.Timer(
            Inspect, null, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));

        private void Inspect(object? state)
        {
            lock (_gate)
            {
                if (_finished) return;
                try
                {
                    List<PopupWindow> popups = EnumeratePopups();
                    if (_hoveredAt is null)
                    {
                        if (TryFindMenuItem(popups, _targetLabel,
                                out NativeMethods.RECT itemRectangle))
                        {
                            SetCursorPos(itemRectangle.Left + Math.Min(30,
                                    Math.Max(2, itemRectangle.Right - itemRectangle.Left - 2)),
                                itemRectangle.Top + Math.Max(1,
                                    (itemRectangle.Bottom - itemRectangle.Top) / 2));
                            _hoveredAt = DateTime.UtcNow;
                        }
                    }
                    else if (DateTime.UtcNow - _hoveredAt >= TimeSpan.FromSeconds(1)
                        && popups.Count >= 2)
                    {
                        popups = EnumeratePopups();
                        Capture(popups);
                        Succeeded = true;
                        ErrorMessage = string.Empty;
                        Finish();
                    }

                    if (DateTime.UtcNow >= _deadline)
                    {
                        Capture(popups);
                        ErrorMessage = _hoveredAt is null
                            ? $"Timed out locating '{_targetLabel}'."
                            : $"The '{_targetLabel}' submenu did not open.";
                        Finish();
                    }
                }
                catch (Exception ex)
                {
                    ErrorMessage = ex.ToString();
                    Finish();
                }
            }
        }

        private void Capture(IReadOnlyList<PopupWindow> popups)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)
                ?? Environment.CurrentDirectory);
            Rectangle bounds = popups
                .Select(static popup => popup.Bounds)
                .Aggregate(Rectangle.Union);
            bounds.Inflate(8, 8);
            bounds.Intersect(SystemInformation.VirtualScreen);
            using var image = new Bitmap(bounds.Width, bounds.Height,
                PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            image.Save(_outputPath, ImageFormat.Png);

            var report = new StringBuilder();
            report.AppendLine($"Target: {_targetLabel}");
            report.AppendLine($"Popup count: {popups.Count}");
            foreach (PopupWindow popup in popups.OrderBy(static popup => popup.Bounds.Left))
            {
                report.AppendLine($"HWND=0x{popup.Window.ToInt64():X} "
                    + $"HMENU=0x{popup.Menu.ToInt64():X} Bounds={popup.Bounds}");
                AppendMenu(report, popup.Menu);
            }
            File.WriteAllText(Path.ChangeExtension(_outputPath, ".txt"),
                report.ToString());
        }

        private static void AppendMenu(StringBuilder report, IntPtr menu)
        {
            int count = NativeMethods.GetMenuItemCount(menu);
            var label = new StringBuilder(512);
            for (int position = 0; position < count; position++)
            {
                label.Clear();
                NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION);
                var info = new MenuItemInfo
                {
                    cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                    fMask = 0x0127,
                };
                GetMenuItemInfo(menu, (uint)position, true, ref info);
                GetMenuItemRect(IntPtr.Zero, menu, (uint)position,
                    out NativeMethods.RECT itemRectangle);
                report.AppendLine($"  [{position}] id={info.wID} "
                    + $"type=0x{info.fType:X} state=0x{info.fState:X} "
                    + $"data=0x{info.dwItemData:X} submenu=0x{info.hSubMenu.ToInt64():X} "
                    + $"rect=({itemRectangle.Left},{itemRectangle.Top})-"
                    + $"({itemRectangle.Right},{itemRectangle.Bottom}) label='{label}'");
            }
        }

        private List<PopupWindow> EnumeratePopups()
        {
            var popups = new List<PopupWindow>();
            EnumThreadWindows(_threadId, (window, _) =>
            {
                var className = new StringBuilder(32);
                GetClassName(window, className, className.Capacity);
                if (!className.ToString().Equals("#32768", StringComparison.Ordinal)
                    || !IsWindowVisible(window)
                    || !GetWindowRect(window, out NativeMethods.RECT rectangle))
                    return true;

                IntPtr menu = NativeMethods.SendMessageI(window, MnGetHMenu,
                    IntPtr.Zero, IntPtr.Zero);
                if (menu != IntPtr.Zero)
                    popups.Add(new PopupWindow(window, menu,
                        Rectangle.FromLTRB(rectangle.Left, rectangle.Top,
                            rectangle.Right, rectangle.Bottom)));
                return true;
            }, IntPtr.Zero);
            return popups;
        }

        private static bool TryFindMenuItem(IEnumerable<PopupWindow> popups,
            string targetLabel, out NativeMethods.RECT rectangle)
        {
            var label = new StringBuilder(512);
            foreach (PopupWindow popup in popups)
            {
                int count = NativeMethods.GetMenuItemCount(popup.Menu);
                for (int position = 0; position < count; position++)
                {
                    label.Clear();
                    NativeMethods.GetMenuString(popup.Menu, (uint)position, label,
                        label.Capacity, NativeMethods.MF_BYPOSITION);
                    string normalized = label.ToString().Replace("&", string.Empty,
                        StringComparison.Ordinal).Trim();
                    if (!normalized.Equals(targetLabel,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    return GetMenuItemRect(IntPtr.Zero, popup.Menu,
                        (uint)position, out rectangle);
                }
            }

            rectangle = default;
            return false;
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            PostMessage(_owner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Finish();
                _timer?.Dispose();
            }
        }

        private sealed record PopupWindow(IntPtr Window, IntPtr Menu, Rectangle Bounds);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MenuItemInfo
        {
            internal uint cbSize;
            internal uint fMask;
            internal uint fType;
            internal uint fState;
            internal uint wID;
            internal IntPtr hSubMenu;
            internal IntPtr hbmpChecked;
            internal IntPtr hbmpUnchecked;
            internal nuint dwItemData;
            internal IntPtr dwTypeData;
            internal uint cch;
            internal IntPtr hbmpItem;
        }

        private delegate bool EnumThreadWindowCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId,
            EnumThreadWindowCallback callback, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className,
            int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window,
            out NativeMethods.RECT rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu,
            uint item, out NativeMethods.RECT rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", CharSet = CharSet.Unicode,
            EntryPoint = "GetMenuItemInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMenuItemInfo(IntPtr menu, uint item,
            [MarshalAs(UnmanagedType.Bool)] bool byPosition,
            ref MenuItemInfo itemInfo);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message,
            IntPtr wParam, IntPtr lParam);
    }
}
