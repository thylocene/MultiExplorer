using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Runtime.InteropServices.ComTypes;

[assembly: InternalsVisibleTo("MultiExplorer.Tests")]

namespace MultiExplorer;

// This type must be public because the managed COM callback interfaces nested
// below are passed to Marshal.GetComInterfaceForObject. A public nested type is
// not COM-visible when its declaring type is internal (Type.IsVisible is false).
public static class NativeMethods
{
    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x, y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    // OLE IDropTarget uses POINTL (signed screen coordinates). It is layout-
    // compatible with POINT but kept distinct to match the COM signature.
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL
    {
        public int x, y;
    }

    [ComVisible(true)]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter(
            [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
            uint keyState, POINTL point, ref uint effect);
        [PreserveSig] int DragOver(uint keyState, POINTL point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(
            [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
            uint keyState, POINTL point, ref uint effect);
    }

    [ComImport]
    [Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataObjectAsyncCapability
    {
        [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOperationAsync);
        [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isAsync);
        [PreserveSig] int StartOperation([MarshalAs(UnmanagedType.Interface)] IBindCtx? reserved);
        [PreserveSig] int InOperation([MarshalAs(UnmanagedType.Bool)] out bool inOperation);
        [PreserveSig] int EndOperation(int result,
            [MarshalAs(UnmanagedType.Interface)] IBindCtx? reserved, uint effects);
    }

    // Used with LVM_HITTEST to determine whether a point lands on a list-view item.
    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO
    {
        public POINT pt;
        public uint  flags;
        public int   iItem;
        public int   iSubItem;
        public int   iGroup;
    }

    // LVHITTESTINFO.flags values
    public const uint LVHT_NOWHERE = 0x0001;
    public const uint LVHT_ONITEM  = 0x000E; // LVHT_ONITEMICON | LVHT_ONITEMLABEL | LVHT_ONITEMSTATEICON

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FOLDERSETTINGS
    {
        public uint ViewMode;   // 1=icon 2=smallicon 3=list 4=details 5=thumbnail 6=tile
        public uint fFlags;     // FWF_* flags (0 = defaults)
    }

    // Used with IFolderView2::SetSortColumns.
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // direction: 1=ascending, -1=descending
    [StructLayout(LayoutKind.Sequential)]
    public struct SORTCOLUMN
    {
        public PROPERTYKEY propkey;
        public int         direction;
    }

    // ── IExplorerBrowser — called by us (Runtime Callable Wrapper) ────────────
    //
    // CLSID_ExplorerBrowser: 71F96385-DDD6-48D3-A0C1-AE06E8B055FB
    // Instantiated via Type.GetTypeFromCLSID / Activator.CreateInstance.

    [ComImport]
    [Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerBrowser
    {
        [PreserveSig] int Initialize(IntPtr hwndParent, ref RECT prc, ref FOLDERSETTINGS pfs);
        [PreserveSig] int Destroy();
        [PreserveSig] int SetRect(IntPtr phdwp, RECT rcBrowser);
        [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);
        [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);
        [PreserveSig] int SetFolderSettings(ref FOLDERSETTINGS pfs);
        [PreserveSig] int Advise(IntPtr psbe, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint dwFlag);
        [PreserveSig] int GetOptions(out uint pdwFlag);
        [PreserveSig] int BrowseToIDList(IntPtr pidl, uint uFlags);
        [PreserveSig] int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);
        [PreserveSig] int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, int dwFlags);
        [PreserveSig] int RemoveAll();
        [PreserveSig] int GetCurrentView(ref Guid riid, out IntPtr ppv);
    }

    // Implemented by the host and passed to IExplorerBrowser::Advise.  Navigation
    // is asynchronous, so these callbacks are the only reliable way to know that
    // the shell has finished constructing a replacement folder view.
    [ComVisible(true)]
    [Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerBrowserEvents
    {
        [PreserveSig] int OnNavigationPending(IntPtr pidlFolder);
        [PreserveSig] int OnViewCreated(IntPtr psv);
        [PreserveSig] int OnNavigationComplete(IntPtr pidlFolder);
        [PreserveSig] int OnNavigationFailed(IntPtr pidlFolder);
    }

    // ── IObjectWithSite — called by us on the browser (Runtime Callable Wrapper) ─
    //
    // We call SetSite before Initialize to give the browser a non-null host.
    // Without a site the browser may dereference an uninitialised pointer during
    // user-initiated navigation and produce an AccessViolationException.

    [ComImport]
    [Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectWithSite
    {
        [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);
        [PreserveSig] int GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvSite);
    }

    // ── IFolderViewSettings — implemented by us as a host service ────────────
    //
    // The shell view queries the host's IServiceProvider for this interface
    // (guidService == IID_IFolderViewSettings) during view creation.
    // Returning S_OK with FVM_DETAILS and no suppression flags tells the view
    // to render a full Details layout — including the column header band.
    //
    // No [ComImport] — we implement this interface (CCW), not import it.

    [ComVisible(true)]
    [Guid("AE8C987D-8797-4ED3-BE72-2A47DD938DB0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFolderViewSettings
    {
        [PreserveSig] int GetFolderFlags(out uint pfolderMask, out uint pfolderFlags);
        [PreserveSig] int GetSortColumns(IntPtr rgSortColumns, uint cColumns);
        [PreserveSig] int GetGroupBy(IntPtr pkey, out int pfGroupAscending);
        [PreserveSig] int GetViewMode(out uint puViewMode);
        [PreserveSig] int GetIconSize(out uint puIconSize);
        [PreserveSig] int GetColumnStates(IntPtr rgKeyNames, uint cColumns);
        [PreserveSig] int GetDefaultColumnWidth(IntPtr pkey, out uint pcxColumn);
    }

    // ── IServiceProvider — implemented by us as the browser's site ────────────
    //
    // We return E_NOTIMPL for all service queries.  The browser uses its own
    // defaults rather than trying to call through a null site pointer.
    //
    // No [ComImport] — we implement this interface in managed code (CCW).

    [ComVisible(true)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    // ── IShellView — called by us to select items ────────────────────────────
    //
    // Partial declaration: only the methods up to and including SelectItem are
    // needed.  All 12 methods must be declared in vtable order so that
    // SelectItem lands at the correct slot (slot 14 from IUnknown).
    //
    // SelectItem(NULL, SVSI_SELECT=1) applies the flag to ALL items in the view.
    // This is the documented API for "select all" — what Explorer does for Ctrl+A.

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellView
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
        // IShellView
        [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
        [PreserveSig] int EnableModeless(int fEnable);
        [PreserveSig] int UIActivate(uint uState);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr psvPrev, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phwnd);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(IntPtr pfs);
        [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        [PreserveSig] int SaveViewState();
        /// <summary>
        /// pidlItem = NULL → applies uFlags to ALL items in the view.
        /// SVSI_SELECT (1) selects; SVSI_DESELECT (0) deselects.
        /// </summary>
        [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
    }

    // ── IFolderView — called by us to query the current folder and selection ─────
    //
    // Methods are declared in vtable order (slots 3–10 from IUnknown).
    // GetFolder retrieves the folder object; ItemCount(SVGIO_SELECTION = 1)
    // returns how many items are currently selected — used by blank-area detection.

    [ComImport]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);            // slot 3
        [PreserveSig] int SetCurrentViewMode(uint ViewMode);                 // slot 4
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);          // slot 5
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);            // slot 6
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);           // slot 7
        [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv); // slot 8
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);            // slot 9
        [PreserveSig] int GetFocusedItem(out int piItem);                    // slot 10
    }

    // IFolderView2 extends IFolderView.  All inherited and preceding methods
    // are declared so SetCurrentFolderFlags lands on its documented vtable slot.
    [ComImport]
    [Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFolderView2
    {
        // IFolderView
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint ViewMode);
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
        [PreserveSig] int GetSpacing(out POINT ppt);
        [PreserveSig] int GetDefaultSpacing(out POINT ppt);
        [PreserveSig] int GetAutoArrange(out int pfAutoArrange);
        [PreserveSig] int SelectItem(int iItem, uint dwFlags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);

        // IFolderView2 methods preceding SetCurrentFolderFlags
        [PreserveSig] int SetGroupBy(IntPtr key, int fAscending);
        [PreserveSig] int GetGroupBy(IntPtr key, out int pfAscending);
        [PreserveSig] int SetViewProperty(IntPtr pidl, IntPtr key, IntPtr propvar);
        [PreserveSig] int GetViewProperty(IntPtr pidl, IntPtr key, IntPtr propvar);
        [PreserveSig] int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
        [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
        [PreserveSig] int SetText(uint iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);

        [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
        [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
        [PreserveSig] int GetSortColumnCount(out int pcColumns);
        [PreserveSig] int SetSortColumns(ref SORTCOLUMN rgSortColumns, int cColumns);
        [PreserveSig] int GetSortColumns(IntPtr rgSortColumns, int cColumns);
        // GetItem is slot 29 in the real vtable — must be declared before GetVisibleItem
        // so that SetViewModeAndIconSize lands at the correct vtable offset (slot 35).
        [PreserveSig] int GetItem(int iItem, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetVisibleItem(int iStart, int fPrevious, out int piItem);
        [PreserveSig] int GetSelectedItem(int iDirection, out int piItem);
        [PreserveSig] int GetSelection(int fNoneImpliesFolder, out IntPtr ppsia);
        [PreserveSig] int GetSelectionState(IntPtr pidl, out uint pdwFlags);
        [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string pszVerb);
        [PreserveSig] int SetViewModeAndIconSize(uint uViewMode, int iImageSize);
        [PreserveSig] int GetViewModeAndIconSize(out uint puViewMode, out int piImageSize);
    }

    // ── IPersistFolder2 — called by us to get the current folder PIDL ─────────
    //
    // Inherits IPersistFolder → IPersist → IUnknown.
    // All three inherited methods must be declared to keep GetCurFolder at the
    // correct vtable slot (slot 5 from IUnknown).

    [ComImport]
    [Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFolder2
    {
        [PreserveSig] int GetClassID(out Guid pClassID); // IPersist   — slot 3
        [PreserveSig] int Initialize(IntPtr pidl);       // IPersistFolder — slot 4
        [PreserveSig] int GetCurFolder(out IntPtr ppidl);// IPersistFolder2 — slot 5
    }

    // ── IInputObject — implemented by IExplorerBrowser ───────────────────────
    //
    // IExplorerBrowser implements IInputObject (documented on MSDN).
    // TranslateAcceleratorIO handles BOTH frame-level commands (Ctrl+Shift+N,
    // Ctrl+F, etc.) AND inner shell-view commands (Ctrl+A, F2, Alt+Enter).
    // UIActivateIO must be called to tell the browser it is the active input
    // target; without it, frame commands are silently discarded.

    [ComImport]
    [Guid("68284fAA-6A48-11D0-8c78-00C04fd918b4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInputObject
    {
        [PreserveSig] int UIActivateIO(int fActivate, IntPtr pmsg);
        [PreserveSig] int HasFocusIO();
        [PreserveSig] int TranslateAcceleratorIO(IntPtr pmsg);
    }

    // ── MSG — passed by pointer to IShellView::TranslateAccelerator ─────────

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX;
        public int    ptY;
    }

    // ── Dedicated native-window thread support ───────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName,
        string? lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("ole32.dll")]
    public static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    public static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    public static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget dropTarget);

    [DllImport("ole32.dll")]
    public static extern int RevokeDragDrop(IntPtr hwnd);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr hDrop, uint fileIndex,
        [Out] StringBuilder? fileName, uint characterCount);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClipboardFormatW(string format);

    public const uint GMEM_MOVEABLE     = 0x0002;
    public const uint GMEM_ZEROINIT     = 0x0040;

    public const uint WS_CHILD         = 0x40000000;
    public const uint WS_POPUP         = 0x80000000;
    public const uint WS_VISIBLE       = 0x10000000;
    public const uint WS_CLIPCHILDREN  = 0x02000000;
    public const uint WS_CLIPSIBLINGS  = 0x04000000;
    public const uint WM_QUIT          = 0x0012;
    public const uint WM_APP_INVOKE    = 0x8001;
    public const uint DROPEFFECT_NONE  = 0;
    public const uint DROPEFFECT_COPY  = 1;
    public const uint DROPEFFECT_MOVE  = 2;
    public const uint MK_SHIFT         = 0x0004;
    public const uint MK_CONTROL       = 0x0008;
    public const uint MK_RBUTTON       = 0x0002;

    // ── IComServiceProvider — [ComImport] version for querying COM objects ───────
    //
    // IServiceProvider above lacks [ComImport] because BrowserSite implements it as
    // a CCW.  This separate declaration with [ComImport] is used to CALL QueryService
    // on IExplorerBrowser itself (which also implements IServiceProvider).

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    // ── IShellItem — shell item object (file or folder) ──────────────────────────
    //
    // Created via SHCreateItemFromParsingName.  Passed to INameSpaceTreeControl
    // methods.  Only method signatures up to Compare are needed here.

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // ── IShellBrowser — partial, through GetControlWindow ───────────────────────
    //
    // Inherits IOleWindow → IUnknown.  Methods are declared in vtable order so that
    // GetControlWindow lands at the correct slot (slot 13 from IUnknown).
    // GetControlWindow(FCW_TREE = 4) returns the navigation-pane tree HWND.

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
        // IShellBrowser
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
        [PreserveSig] int EnableModelessSB(int fEnable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
    }

    // ── INameSpaceTreeControl — folder tree in the navigation pane ──────────────
    //
    // Methods are declared in vtable order through EnsureItemVisible (slot 16 from
    // IUnknown, the 14th INameSpaceTreeControl method).  EnsureItemVisible expands
    // all ancestor nodes in the tree and scrolls to make the item visible — which
    // is exactly the "expand to current folder" behaviour.

    [ComImport]
    [Guid("028212A3-B627-47E9-8855-9F598112A7AB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INameSpaceTreeControl
    {
        [PreserveSig] int Initialize(IntPtr hwndParent, ref RECT prc, uint nsctsFlags);
        [PreserveSig] int TreeAdvise(IntPtr punkChangeNotify, out uint pdwCookie);
        [PreserveSig] int TreeUnadvise(uint dwCookie);
        [PreserveSig] int AppendRoot(IShellItem psiRoot, uint grfEnumFlags, uint grfRootStyle, IntPtr pif);
        [PreserveSig] int InsertRoot(int iIndex, IShellItem psiRoot, uint grfEnumFlags, uint grfRootStyle, IntPtr pif);
        [PreserveSig] int RemoveRoot(IShellItem psiRoot);
        [PreserveSig] int RemoveAllRoots();
        [PreserveSig] int GetRootItems(out IntPtr ppsiaRootItems);
        [PreserveSig] int SetItemState(IShellItem psi, uint nstcisMask, uint nstcisFlags);
        [PreserveSig] int GetItemState(IShellItem psi, uint nstcisMask, out uint pnstcisFlags);
        [PreserveSig] int GetSelectedItems(out IntPtr psiaItems);
        [PreserveSig] int GetItemCustomState(IShellItem psi, out int piStateNumber);
        [PreserveSig] int SetItemCustomState(IShellItem psi, int iStateNumber);
        [PreserveSig] int EnsureItemVisible(IShellItem psi);
    }

    // ── SysTreeView32 — used to sync the navigation pane ────────────────────────
    //
    // IExplorerBrowser runs in-process, so the SysTreeView32 inside the
    // navigation pane belongs to our process.  We can pass stack/heap pointers
    // directly to SendMessage without cross-process shared memory.

    [StructLayout(LayoutKind.Sequential)]
    public struct TVITEM
    {
        public uint   mask;
        public IntPtr hItem;
        public uint   state;
        public uint   stateMask;
        public IntPtr pszText;    // pointer to caller-allocated Unicode buffer
        public int    cchTextMax;
        public int    iImage;
        public int    iSelectedImage;
        public int    cChildren;  // 0 = leaf, 1 = has children, I_CHILDRENCALLBACK = -1
        public IntPtr lParam;
    }

    public const uint TVIF_TEXT     = 0x0001;
    public const uint TVIF_HANDLE   = 0x0010;

    public const uint TVGN_ROOT  = 0x0000; // first root item
    public const uint TVGN_NEXT  = 0x0001; // next sibling
    public const uint TVGN_CHILD = 0x0004; // first child
    public const uint TVGN_CARET = 0x0009; // currently selected (keyboard caret) item

    public const uint TVE_EXPAND    = 0x0002;
    public const uint TVM_EXPAND        = 0x1102;
    public const uint TVM_GETNEXTITEM   = 0x110A;
    public const uint TVM_SELECTITEM    = 0x110B;
    public const uint TVM_GETITEM       = 0x113E; // TVM_GETITEMW (Unicode)
    public const uint TVM_ENSUREVISIBLE = 0x1114;

    /// <summary>
    /// General-purpose SendMessage that returns IntPtr — needed for HTREEITEM values
    /// which are pointer-sized and must not be truncated to int on 64-bit.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessageI(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>SendMessage overload for TVM_GETITEM (TVITEM passed by ref).</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessageTv(IntPtr hWnd, uint Msg, IntPtr wParam, ref TVITEM lParam);

    // ── IOleCommandTarget — standard OLE commands on shell views ────────────────
    //
    // OLECMDID_CUT=5, OLECMDID_COPY=6, OLECMDID_PASTE=7, OLECMDID_PROPERTIES=10,
    // OLECMDID_DELETE=24  — all in CGID_NULL (pass IntPtr.Zero as pguidCmdGroup).
    // The shell folder view (CDefView) implements this interface and routes each
    // command to the selected items, exactly like the corresponding keyboard shortcut.

    [ComImport]
    [Guid("B722BCCB-4E68-101B-A2BC-00AA00404770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleCommandTarget
    {
        [PreserveSig] int QueryStatus(IntPtr pguidCmdGroup, uint cCmds, IntPtr prgCmds, IntPtr pCmdText);
        [PreserveSig] int Exec(IntPtr pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut);
    }

    // ── Shell P/Invokes ───────────────────────────────────────────────────────

    // ── Focus / keyboard P/Invokes ────────────────────────────────────────────

    /// <summary>Returns a related window handle (e.g. first child via GW_CHILD = 5).</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>Returns the parent window handle, or IntPtr.Zero for top-level windows.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>Returns true if hWnd is a child or descendant of hWndParent.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    /// <summary>Copies the window class name into lpClassName.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc callback, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    /// <summary>Sends LVM_HITTEST to a SysListView32 to find the item under a point.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref LVHITTESTINFO lParam);

    public const uint LVM_HITTEST = 0x1012; // LVM_FIRST (0x1000) + 18

    /// <summary>Sets Win32 keyboard focus to the specified window.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>Returns the HWND that currently has keyboard focus in this thread.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    /// <summary>Copies the thread's 256-byte keyboard state table into lpKeyState.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetKeyboardState([Out] byte[] lpKeyState);

    /// <summary>Sets the thread's 256-byte keyboard state table from lpKeyState.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetKeyboardState(byte[] lpKeyState);

    /// <summary>
    /// Translates the virtual-key code and keyboard state to a Unicode character.
    /// Returns the number of characters placed in pwszBuff (1 = normal char, -1 = dead key).
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicode(uint wVirtKey, uint wScanCode,
        [In] byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszBuff,
        int cchBuff, uint wFlags);

    /// <summary>Translates a virtual-key code into a scan code or character value.</summary>
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>Posts a message to the message queue of the thread that owns the window.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Registers a message that is unique across the system (WM range 0xC000–0xFFFF).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    /// <summary>Returns the screen rect of a window in physical pixels.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>Converts a screen-coordinate point to client coordinates of hWnd.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>Returns the client-area rectangle of hWnd (top/left are always 0).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    public const uint GW_HWNDNEXT = 2;

    // ── Shell context-menu P/Invokes and interfaces ───────────────────────────

    /// <summary>Binds to the parent IShellFolder of a PIDL and returns the child PIDL
    /// (ppidlLast points INTO pidl — do NOT CoTaskMemFree it separately).</summary>
    [DllImport("shell32.dll")]
    public static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        out IntPtr ppv, out IntPtr ppidlLast);

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl,
            ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pStrRet);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu,
            uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType,
            IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CMINVOKECOMMANDINFO
    {
        public uint   cbSize;
        public uint   fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;       // zero-based ordinal: (IntPtr)(selectedId - 1)
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int    nShow;
        public uint   dwHotKey;
        public IntPtr hIcon;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>Displays a context menu and returns the selected command ID
    /// when TPM_RETURNCMD is set (0 = cancelled).</summary>
    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags,
        int x, int y, IntPtr hwnd, IntPtr lptpm);

    public const uint TPM_RETURNCMD   = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint CMF_EXPLORE     = 0x0004; // adds "Explore" verb + standard shell extras

    // ── Shell P/Invokes ───────────────────────────────────────────────────────

    /// <summary>Returns a pointer into pidl pointing at its last SHITEMID (the child item).</summary>
    [DllImport("shell32.dll")]
    public static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    /// <summary>Creates an IShellItem from a file-system path.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    /// <summary>Retrieves a COM interface pointer from an accessible object in a window.</summary>
    [DllImport("oleacc.dll")]
    public static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        int idObject,
        ref Guid riid,
        out IntPtr ppvObject);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // Legacy-named, but still supported by the Windows shell and useful here
    // because it owns its progress/conflict UI on the calling STA thread.  By
    // invoking it away from the WinForms thread, a long copy no longer makes the
    // whole application modal.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    public const uint FO_MOVE = 0x0001;
    public const uint FO_COPY = 0x0002;
    public const ushort FOF_ALLOWUNDO       = 0x0040;
    public const ushort FOF_NOCONFIRMMKDIR  = 0x0200;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string? lParam);

    // Grants the specified process the right to call SetForegroundWindow.
    // Must be called by the current foreground process before handing off to
    // another process that needs to bring its window to the front.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(
        IntPtr hWnd, int x, int y, int width, int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    public static readonly IntPtr HWND_BROADCAST    = new IntPtr(0xFFFF);
    public const uint WM_SETTINGCHANGE              = 0x001A;
    public const int  SHCNE_ASSOCCHANGED            = unchecked((int)0x08000000);
    public const uint SHCNF_IDLIST                  = 0x0000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int    iIcon;
        public uint   dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes,
        ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ── Layered-window helpers (Windows 8+: WS_EX_LAYERED on child windows) ─────

    /// <summary>Sets window opacity / colour-key for a WS_EX_LAYERED window.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public const uint LWA_ALPHA = 0x00000002; // bAlpha controls per-window opacity

    /// <summary>Retrieves a window's style or extended style word.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>Sets a window's style or extended style word.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    public const int GWL_STYLE       = -16;
    public const int GWL_EXSTYLE     = -20;
    public const int WS_EX_LAYERED   = 0x00080000;

    // ── Global hotkey registration ────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY    = 0x0312;
    public const int MOD_ALT      = 0x0001;
    public const int MOD_CONTROL  = 0x0002;
    public const int MOD_SHIFT    = 0x0004;
    public const int MOD_WIN      = 0x0008;
    public const int MOD_NOREPEAT = 0x4000;  // prevents repeated firing while key is held
    public const int VK_M         = 0x4D;

    // ── IShellItemArray — returned by IFolderView2::GetSelection ─────────────────

    [ComImport]
    [Guid("B63EA76D-9D54-473A-A26F-E1D10D9A8E5B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(uint AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int GetCount(out uint pdwNumItems);
        [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
        [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
    }

    // Invoke these two IShellItemArray methods from the raw interface pointer.
    // Some Windows 11 shell views expose the interface correctly but .NET can
    // cache a failed QueryInterface on the RCW, making a managed cast unreliable.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellItemArrayGetCountDelegate(IntPtr self, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellItemArrayGetItemAtDelegate(
        IntPtr self, uint index, out IntPtr shellItem);

    internal static int ShellItemArrayGetCount(IntPtr shellItemArray, out uint count)
    {
        IntPtr vtable = Marshal.ReadIntPtr(shellItemArray);
        IntPtr method = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<ShellItemArrayGetCountDelegate>(method)(
            shellItemArray, out count);
    }

    internal static int ShellItemArrayGetItemAt(
        IntPtr shellItemArray, uint index, out IntPtr shellItem)
    {
        IntPtr vtable = Marshal.ReadIntPtr(shellItemArray);
        IntPtr method = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<ShellItemArrayGetItemAtDelegate>(method)(
            shellItemArray, index, out shellItem);
    }

    // ── IPreviewHandler — hosted in PreviewPane to render file previews ───────────

    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPreviewHandler
    {
        [PreserveSig] int SetWindow(IntPtr hwnd, ref RECT prc);
        [PreserveSig] int SetRect(ref RECT prc);
        [PreserveSig] int DoPreview();
        [PreserveSig] int Unload();
        [PreserveSig] int SetFocus();
        [PreserveSig] int QueryFocus(out IntPtr phwnd);
        [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
    }

    // ── IInitializeWithFile — first-choice init for preview handlers ──────────────

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithFile
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    // ── IInitializeWithItem — fallback init for handlers that prefer IShellItem ───

    [ComImport]
    [Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithItem
    {
        [PreserveSig] int Initialize(IShellItem psi, uint grfMode);
    }
}
