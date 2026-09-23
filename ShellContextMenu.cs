using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>Displays a native Shell context menu and routes its owner messages.</summary>
internal static class ShellContextMenu
{
    internal static bool CompleteForwardedMenuMessage(
        int message, int handlerResult, ref IntPtr messageResult)
    {
        // IContextMenu2::HandleMenuMsg reports S_OK only when it handled the
        // message. WM_DRAWITEM and WM_MEASUREITEM additionally require a non-zero
        // window-procedure result; returning zero makes Windows discard extension
        // measurements and can collapse the submenu to a narrow vertical strip.
        if (handlerResult != 0) return false;
        if (message is 0x002B or 0x002C)
            messageResult = (IntPtr)1;
        return true;
    }

    internal static void Show(NativeMethods.IContextMenu contextMenu, IntPtr ownerWindow,
        Point screenLocation, Action<IntPtr>? customizeMenu = null,
        Func<int, bool>? executeCustomCommand = null,
        Func<string, bool>? executeCanonicalCommand = null,
        Func<ShellContextCommand, bool>? executeCommandBarCommand = null,
        Func<ShellContextCommand, bool>? isCommandBarCommandEnabled = null,
        Func<ShellContextCommand, bool>? isCommandBarCommandVisible = null,
        int dpi = 96, uint queryFlags = NativeMethods.CMF_EXPLORE,
        bool useCustomDarkRendering = true)
    {
        ArgumentNullException.ThrowIfNull(contextMenu);
        if (ownerWindow == IntPtr.Zero)
            throw new ArgumentException("A context-menu owner window is required.",
                nameof(ownerWindow));

        IntPtr menu = IntPtr.Zero;
        try
        {
            menu = NativeMethods.CreatePopupMenu();
            int queryResult = menu == IntPtr.Zero ? -1 : contextMenu.QueryContextMenu(menu,
                0, 1, 0x7FFF, queryFlags);
            if (menu == IntPtr.Zero || queryResult < 0)
            {
                AppLog.Debug(nameof(ShellContextMenu),
                    $"QueryContextMenu returned 0x{queryResult:X8}.");
                return;
            }

            RemoveUnavailableCanonicalCommands(menu, contextMenu);
            customizeMenu?.Invoke(menu);
            IReadOnlyDictionary<ShellContextCommand, uint> shellCommands =
                new Dictionary<ShellContextCommand, uint>();
            ShellContextCommandBar? commandBar = null;
            if (executeCommandBarCommand is not null)
            {
                shellCommands = FindShellCommands(menu);
                commandBar = new ShellContextCommandBar(ownerWindow, menu, dpi,
                    isCommandBarCommandEnabled, isCommandBarCommandVisible);
                RemoveDuplicateShellCommands(menu, shellCommands,
                    commandBar.VisibleCommands);
                if (!commandBar.Insert()) commandBar = null;
            }

            // The command row makes Windows fall back to a classic light canvas
            // for the root popup, so that one menu is owner-drawn. Cascading Shell
            // menus remain completely native and retain their correct sizing and
            // Windows dark-mode rendering.
            using DarkShellMenuRenderer? darkMenuRenderer = ThemeManager.IsDark
                && useCustomDarkRendering
                ? new DarkShellMenuRenderer(dpi)
                : null;
            darkMenuRenderer?.Apply(menu);

            using var messages = new ShellContextMenuMessageWindow(ownerWindow,
                contextMenu as NativeMethods.IContextMenu2,
                contextMenu as NativeMethods.IContextMenu3,
                commandBar,
                darkMenuRenderer);
            using IDisposable? hoverTracking = commandBar?.StartHoverTracking();
            int selected = NativeMethods.TrackPopupMenuEx(menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                screenLocation.X, screenLocation.Y, ownerWindow, IntPtr.Zero);
            if (selected <= 0) return;
            if (selected == ShellContextCommandBar.CommandId)
            {
                if (commandBar?.TryResolveSelectedCommand(selected,
                        out ShellContextCommand command) != true)
                    return;

                if (executeCommandBarCommand?.Invoke(command) == true) return;
                if (shellCommands.TryGetValue(command, out uint commandOffset))
                {
                    InvokeShellCommand(contextMenu, ownerWindow, commandOffset,
                        screenLocation);
                    return;
                }

                if (command == ShellContextCommand.Share)
                    TryInvokeNamedShellCommand(contextMenu, ownerWindow,
                        "Windows.ModernShare", "share");
                return;
            }

            if (executeCustomCommand?.Invoke(selected) == true) return;

            uint selectedOffset = (uint)(selected - 1);
            string canonicalVerb = GetCanonicalVerb(contextMenu, selectedOffset);
            if (canonicalVerb.Length > 0
                && executeCanonicalCommand?.Invoke(canonicalVerb) == true)
                return;

            if (!InvokeShellCommand(contextMenu, ownerWindow, selectedOffset,
                    screenLocation))
            {
                string commandName = canonicalVerb.Length > 0
                    ? canonicalVerb
                    : $"menu command {selected}";
                AppLog.Warn(null, nameof(ShellContextMenu),
                    $"Windows could not run '{commandName}'.");
            }
        }
        finally
        {
            if (menu != IntPtr.Zero) NativeMethods.DestroyMenu(menu);
        }
    }

    private static IReadOnlyDictionary<ShellContextCommand, uint> FindShellCommands(
        IntPtr menu)
    {
        var commands = new Dictionary<ShellContextCommand, uint>();
        int menuItemCount = NativeMethods.GetMenuItemCount(menu);
        if (menuItemCount <= 0) return commands;

        var label = new StringBuilder(128);
        for (int position = 0; position < menuItemCount; position++)
        {
            uint commandId = GetMenuItemID(menu, position);
            if (commandId is 0 or uint.MaxValue || commandId > 0x7FFF)
                continue;

            label.Clear();
            if (NativeMethods.GetMenuString(menu, (uint)position, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION) <= 0
                || !TryMapMenuLabel(label.ToString(), out ShellContextCommand command))
                continue;

            commands.TryAdd(command, commandId - 1);
        }

        return commands;
    }

    internal static bool TryMapMenuLabel(string label, out ShellContextCommand command)
    {
        string normalized = NormalizeMenuLabel(label);
        command = normalized.ToLowerInvariant() switch
        {
            "cut" => ShellContextCommand.Cut,
            "copy" => ShellContextCommand.Copy,
            "rename" => ShellContextCommand.Rename,
            "share" => ShellContextCommand.Share,
            "delete" => ShellContextCommand.Delete,
            _ => default,
        };
        return normalized.ToLowerInvariant() is
            "cut" or "copy" or "rename" or "share" or "delete";
    }

    internal static string NormalizeMenuLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Split('\t', 2)[0]
            .Trim()
            .TrimEnd('.', '…');
    }

    /// <summary>
    /// Displays one Shell-owned submenu without showing the complete context menu.
    /// The command is still executed by the original Shell context-menu object.
    /// </summary>
    internal static bool ShowSubmenu(NativeMethods.IContextMenu contextMenu,
        IntPtr ownerWindow, Point screenLocation, string submenuLabel)
    {
        ArgumentNullException.ThrowIfNull(contextMenu);
        ArgumentException.ThrowIfNullOrWhiteSpace(submenuLabel);
        if (ownerWindow == IntPtr.Zero)
            throw new ArgumentException("A context-menu owner window is required.",
                nameof(ownerWindow));

        IntPtr rootMenu = NativeMethods.CreatePopupMenu();
        if (rootMenu == IntPtr.Zero) return false;

        try
        {
            if (contextMenu.QueryContextMenu(rootMenu, 0, 1, 0x7FFF,
                    NativeMethods.CMF_EXPLORE) < 0)
                return false;

            int itemCount = NativeMethods.GetMenuItemCount(rootMenu);
            var label = new StringBuilder(128);
            for (int position = 0; position < itemCount; position++)
            {
                IntPtr submenu = NativeMethods.GetSubMenu(rootMenu, position);
                if (submenu == IntPtr.Zero) continue;

                label.Clear();
                if (NativeMethods.GetMenuString(rootMenu, (uint)position, label,
                        label.Capacity, NativeMethods.MF_BYPOSITION) <= 0
                    || !NormalizeMenuLabel(label.ToString()).Equals(submenuLabel,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                using var messages = new ShellContextMenuMessageWindow(ownerWindow,
                    contextMenu as NativeMethods.IContextMenu2,
                    contextMenu as NativeMethods.IContextMenu3,
                    commandBar: null, darkMenuRenderer: null);

                // Send to is populated only when Windows receives this message.
                NativeMethods.SendMessageI(ownerWindow, 0x0117, submenu,
                    (IntPtr)(position & 0xFFFF));
                int selected = NativeMethods.TrackPopupMenuEx(submenu,
                    NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                    screenLocation.X, screenLocation.Y, ownerWindow, IntPtr.Zero);
                return selected <= 0
                    || InvokeShellCommand(contextMenu, ownerWindow,
                        (uint)(selected - 1), screenLocation);
            }

            return false;
        }
        finally
        {
            NativeMethods.DestroyMenu(rootMenu);
        }
    }

    private static void RemoveDuplicateShellCommands(IntPtr menu,
        IReadOnlyDictionary<ShellContextCommand, uint> shellCommands,
        IReadOnlyList<ShellContextCommand> visibleCommands)
    {
        foreach ((ShellContextCommand command, uint offset) in shellCommands)
        {
            if (visibleCommands.Contains(command))
                NativeMethods.DeleteMenu(menu, offset + 1, 0);
        }

        RemoveRedundantSeparators(menu);
    }

    internal static void RemoveUnavailableCanonicalCommands(IntPtr menu,
        NativeMethods.IContextMenu contextMenu)
    {
        ArgumentNullException.ThrowIfNull(contextMenu);
        if (menu == IntPtr.Zero)
            throw new ArgumentException("A menu handle is required.", nameof(menu));

        RemoveHiddenCanonicalCommands(menu, contextMenu,
            IsCanonicalCommandAvailable);
    }

    private static void RemoveHiddenCanonicalCommands(IntPtr menu,
        NativeMethods.IContextMenu contextMenu,
        Func<string, bool> isCanonicalCommandVisible)
    {
        int itemCount = NativeMethods.GetMenuItemCount(menu);
        for (int position = itemCount - 1; position >= 0; position--)
        {
            IntPtr submenu = NativeMethods.GetSubMenu(menu, position);
            if (submenu != IntPtr.Zero)
                RemoveHiddenCanonicalCommands(submenu, contextMenu,
                    isCanonicalCommandVisible);

            uint commandId = GetMenuItemID(menu, position);
            if (commandId is 0 or uint.MaxValue or > 0x7FFF)
                continue;

            string canonicalVerb = GetCanonicalVerb(contextMenu,
                commandId - 1);
            if (canonicalVerb.Length > 0
                && !isCanonicalCommandVisible(canonicalVerb))
                NativeMethods.DeleteMenu(menu, (uint)position,
                    NativeMethods.MF_BYPOSITION);
        }

        RemoveRedundantSeparators(menu);
    }

    private static void RemoveRedundantSeparators(IntPtr menu)
    {
        const uint MfSeparator = 0x0800;
        int count = NativeMethods.GetMenuItemCount(menu);
        bool nextIsSeparator = true;
        for (int position = count - 1; position >= 0; position--)
        {
            uint state = GetMenuState(menu, (uint)position, NativeMethods.MF_BYPOSITION);
            bool isSeparator = state != uint.MaxValue && (state & MfSeparator) != 0;
            if (isSeparator && nextIsSeparator)
            {
                NativeMethods.DeleteMenu(menu, (uint)position,
                    NativeMethods.MF_BYPOSITION);
                continue;
            }

            nextIsSeparator = isSeparator;
        }
    }

    private static bool InvokeShellCommand(NativeMethods.IContextMenu contextMenu,
        IntPtr ownerWindow, uint commandOffset, Point? invocationPoint = null)
    {
        const uint CmicMaskUnicode = 0x00004000;
        const uint CmicMaskPointInvoke = 0x20000000;
        var invocation = new NativeMethods.CMINVOKECOMMANDINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.CMINVOKECOMMANDINFO>(),
            fMask = CmicMaskUnicode
                    | (invocationPoint.HasValue ? CmicMaskPointInvoke : 0),
            hwnd = ownerWindow,
            lpVerb = (IntPtr)commandOffset,
            lpVerbW = (IntPtr)commandOffset,
            nShow = 1,
            ptInvoke = invocationPoint is { } point
                ? new NativeMethods.POINT(point.X, point.Y)
                : default,
        };
        int result = contextMenu.InvokeCommand(ref invocation);
        if (result < 0)
            AppLog.Debug(nameof(InvokeShellCommand),
                $"IContextMenu.InvokeCommand returned 0x{result:X8}.");
        return result >= 0;
    }

    internal static bool CanUseLiveSelectionContext(
        int selectionCount, string canonicalVerb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(selectionCount);
        ArgumentNullException.ThrowIfNull(canonicalVerb);
        return selectionCount == 1
               && canonicalVerb.Length > 0
               && IsCanonicalCommandAvailable(canonicalVerb);
    }

    internal static bool IsCanonicalCommandAvailable(string canonicalVerb)
    {
        ArgumentNullException.ThrowIfNull(canonicalVerb);

        // Windows exposes the Start commands through IContextMenu but refuses
        // to execute them in another desktop app. Its public Start API can pin
        // an app's own AppListEntry only, not an arbitrary file or folder.
        // The file Favorites verbs are replaced with our state-aware command.
        return canonicalVerb.ToLowerInvariant() is not
            ("pintostartscreen"
            or "unpinfromstartscreen"
            or "pintohomefile"
            or "unpinfromhomefile");
    }

    private static string GetCanonicalVerb(
        NativeMethods.IContextMenu contextMenu, uint commandOffset)
    {
        const uint GcsVerbW = 4;
        const int CharacterCapacity = 260;
        IntPtr buffer = Marshal.AllocHGlobal(CharacterCapacity * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return contextMenu.GetCommandString((UIntPtr)commandOffset,
                       GcsVerbW, IntPtr.Zero, buffer, CharacterCapacity) >= 0
                ? Marshal.PtrToStringUni(buffer) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool TryInvokeCanonicalShellCommand(
        NativeMethods.IContextMenu contextMenu, IntPtr ownerWindow,
        string canonicalVerb, Point invocationPoint)
    {
        ArgumentNullException.ThrowIfNull(contextMenu);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalVerb);

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return false;

        try
        {
            if (contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF,
                    NativeMethods.CMF_EXPLORE) < 0)
                return false;

            int itemCount = NativeMethods.GetMenuItemCount(menu);
            for (int position = 0; position < itemCount; position++)
            {
                uint commandId = GetMenuItemID(menu, position);
                if (commandId is 0 or uint.MaxValue or > 0x7FFF)
                    continue;

                uint commandOffset = commandId - 1;
                if (!GetCanonicalVerb(contextMenu, commandOffset).Equals(
                        canonicalVerb, StringComparison.OrdinalIgnoreCase))
                    continue;

                return InvokeShellCommand(contextMenu, ownerWindow,
                    commandOffset, invocationPoint);
            }

            return false;
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    internal static bool TryInvokeDefaultShellCommand(
        NativeMethods.IContextMenu contextMenu, IntPtr ownerWindow)
    {
        ArgumentNullException.ThrowIfNull(contextMenu);

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return false;

        try
        {
            if (contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF,
                    NativeMethods.CMF_DEFAULTONLY) < 0)
                return false;

            uint commandId = GetMenuDefaultItem(menu, false, 0);
            if (commandId is 0 or uint.MaxValue or > 0x7FFF)
            {
                int itemCount = NativeMethods.GetMenuItemCount(menu);
                commandId = Enumerable.Range(0, Math.Max(0, itemCount))
                    .Select(position => GetMenuItemID(menu, position))
                    .FirstOrDefault(static id => id is > 0 and <= 0x7FFF);
            }

            return commandId is > 0 and <= 0x7FFF
                && InvokeShellCommand(contextMenu, ownerWindow, commandId - 1);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    internal static bool TryInvokeNamedShellCommand(
        NativeMethods.IContextMenu contextMenu,
        IntPtr ownerWindow, params string[] verbs) =>
        TryInvokeNamedShellCommand(contextMenu, ownerWindow,
            prepareExtensionVerbs: false, verbs: verbs);

    internal static bool TryInvokeNamedShellCommand(
        NativeMethods.IContextMenu contextMenu, IntPtr ownerWindow,
        bool prepareExtensionVerbs, params string[] verbs)
    {
        ArgumentNullException.ThrowIfNull(contextMenu);
        ArgumentNullException.ThrowIfNull(verbs);

        IntPtr preparationMenu = IntPtr.Zero;
        try
        {
            if (prepareExtensionVerbs)
            {
                preparationMenu = NativeMethods.CreatePopupMenu();
                if (preparationMenu == IntPtr.Zero
                    || contextMenu.QueryContextMenu(preparationMenu, 0, 1, 0x7FFF,
                        NativeMethods.CMF_EXPLORE) < 0)
                    return false;
            }

            foreach (string verb in verbs)
            {
                IntPtr ansiVerbPointer = Marshal.StringToHGlobalAnsi(verb);
                IntPtr unicodeVerbPointer = Marshal.StringToHGlobalUni(verb);
                try
                {
                    var invocation = new NativeMethods.CMINVOKECOMMANDINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.CMINVOKECOMMANDINFO>(),
                        fMask = 0x00004000,
                        hwnd = ownerWindow,
                        lpVerb = ansiVerbPointer,
                        lpVerbW = unicodeVerbPointer,
                        nShow = 1,
                    };
                    if (contextMenu.InvokeCommand(ref invocation) >= 0) return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(unicodeVerbPointer);
                    Marshal.FreeHGlobal(ansiVerbPointer);
                }
            }

            return false;
        }
        finally
        {
            if (preparationMenu != IntPtr.Zero)
                NativeMethods.DestroyMenu(preparationMenu);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern uint GetMenuDefaultItem(IntPtr menu,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition, uint flags);

    private sealed class ShellContextMenuMessageWindow : NativeWindow, IDisposable
    {
        private const int WmDrawItem = 0x002B;
        private const int WmMeasureItem = 0x002C;
        private const int WmInitMenuPopup = 0x0117;
        private const int WmMenuChar = 0x0120;
        private const int WmApplyDarkMenuTheme = 0x8000 + 0x4D5;

        private readonly NativeMethods.IContextMenu2? _contextMenu2;
        private readonly NativeMethods.IContextMenu3? _contextMenu3;
        private readonly ShellContextCommandBar? _commandBar;
        private readonly DarkShellMenuRenderer? _darkMenuRenderer;
        private bool _disposed;

        internal ShellContextMenuMessageWindow(IntPtr ownerWindow,
            NativeMethods.IContextMenu2? contextMenu2,
            NativeMethods.IContextMenu3? contextMenu3,
            ShellContextCommandBar? commandBar,
            DarkShellMenuRenderer? darkMenuRenderer)
        {
            _contextMenu2 = contextMenu2;
            _contextMenu3 = contextMenu3;
            _commandBar = commandBar;
            _darkMenuRenderer = darkMenuRenderer;
            AssignHandle(ownerWindow);
        }

        protected override void WndProc(ref Message message)
        {
            if (!_disposed && message.Msg == WmApplyDarkMenuTheme)
            {
                ApplyThemeToOpenMenuWindows();
                message.Result = IntPtr.Zero;
                return;
            }

            if (!_disposed && _commandBar?.TryHandleMessage(ref message) == true)
                return;

            if (!_disposed && message.Msg == WmInitMenuPopup)
            {
                // Shell extensions populate cascading menus lazily. A successful
                // IContextMenu handler owns this message; sending it through the
                // base window procedure as well can initialize the same popup a
                // second time and invalidate its measured dimensions.
                if (!ForwardMenuMessage(ref message))
                    base.WndProc(ref message);
                ApplyThemeToOpenMenuWindows();
                NativeMethods.PostMessageW(Handle, WmApplyDarkMenuTheme,
                    IntPtr.Zero, IntPtr.Zero);
                return;
            }

            if (!_disposed
                && _darkMenuRenderer?.TryHandleMessage(ref message) == true)
                return;

            if (!_disposed
                && message.Msg is WmDrawItem or WmMeasureItem or WmMenuChar)
            {
                _darkMenuRenderer?.PrepareShellOwnerDrawMessage(ref message);
                if (ForwardMenuMessage(ref message))
                {
                    _darkMenuRenderer?.FinishShellOwnerDrawMessage(ref message);
                    return;
                }
            }

            base.WndProc(ref message);
        }

        private bool ForwardMenuMessage(ref Message message)
        {
            try
            {
                // IContextMenu3 supplies the LRESULT for every forwarded menu
                // message, not only WM_MENUCHAR. In particular, owner-draw
                // extensions can return meaningful results for initialization,
                // measuring and drawing that must reach the owner window.
                if (_contextMenu3 is { } contextMenu3)
                {
                    int contextMenu3Result = contextMenu3.HandleMenuMsg2((uint)message.Msg,
                        message.WParam, message.LParam, out IntPtr messageResult);
                    if (contextMenu3Result != 0) return false;

                    message.Result = messageResult;
                    LogInvalidOwnerDrawMeasurement(message, "IContextMenu3");
                    return true;
                }

                if (_contextMenu2 is not { } contextMenu2) return false;

                int handlerResult = contextMenu2.HandleMenuMsg(
                    (uint)message.Msg, message.WParam, message.LParam);
                IntPtr forwardedResult = message.Result;
                if (!CompleteForwardedMenuMessage(message.Msg,
                        handlerResult, ref forwardedResult))
                    return false;

                message.Result = forwardedResult;
                LogInvalidOwnerDrawMeasurement(message, "IContextMenu2");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug(ex, nameof(ShellContextMenuMessageWindow));
                return false;
            }
        }

        private static void LogInvalidOwnerDrawMeasurement(Message message,
            string handler)
        {
            if (message.Msg != WmMeasureItem || message.LParam == IntPtr.Zero)
                return;

            var measure = Marshal.PtrToStructure<MeasureItemStruct>(message.LParam);
            if (measure.CtlType != 1
                || measure.itemWidth > 0 && measure.itemHeight > 0)
                return;

            AppLog.Debug(nameof(ShellContextMenuMessageWindow),
                $"{handler} returned an empty owner-draw measurement for "
                + $"command {measure.itemID} (data 0x{measure.itemData:X}, "
                + $"width {measure.itemWidth}, height {measure.itemHeight}).");
        }

        private void ApplyThemeToOpenMenuWindows()
        {
            if (!ThemeManager.IsDark) return;

            EnumThreadWindows(NativeMethods.GetCurrentThreadId(),
                (window, _) =>
                {
                    var className = new StringBuilder(32);
                    NativeMethods.GetClassName(window, className,
                        className.Capacity);
                    if (className.ToString().Equals("#32768",
                            StringComparison.Ordinal))
                        ThemeManager.ApplyNativeWindow(window,
                            includeChildren: false);
                    return true;
                }, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            ReleaseHandle();
        }

        private delegate bool EnumThreadWindowCallback(
            IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct MeasureItemStruct
        {
            internal uint CtlType;
            internal uint CtlID;
            internal uint itemID;
            internal uint itemWidth;
            internal uint itemHeight;
            internal nuint itemData;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId,
            EnumThreadWindowCallback callback, IntPtr parameter);
    }
}
