using System.Drawing;
using System.Runtime.InteropServices;

namespace MultiExplorer.Tests;

public sealed class ShellContextCommandBarTests
{
    [Theory]
    [InlineData(96, 16)]
    [InlineData(144, 24)]
    [InlineData(192, 32)]
    public void IconSize_IsSmallerAndRenderedAtTheCurrentDpi(
        int dpi, int expectedPixels)
    {
        Assert.Equal(expectedPixels,
            ShellContextCommandBar.GetIconPixelSize(dpi));
    }

    [Theory]
    [InlineData(96, 380, 62)]
    [InlineData(144, 570, 93)]
    [InlineData(192, 760, 124)]
    public void GetPreferredSize_ScalesInDevicePixels(int dpi,
        int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new Size(expectedWidth, expectedHeight),
            ShellContextCommandBar.GetPreferredSize(dpi));
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(89, 1)]
    [InlineData(169, 2)]
    [InlineData(249, 3)]
    [InlineData(329, 4)]
    public void HitTest_MapsEachCellToItsCommand(int x, int expectedValue)
    {
        var bounds = new Rectangle(10, 20, 380, 74);

        Assert.Equal((ShellContextCommand)expectedValue,
            ShellContextCommandBar.HitTest(bounds, new Point(x, 30)));
    }

    [Fact]
    public void HitTest_RejectsPointsOutsideTheCommandRow()
    {
        var bounds = new Rectangle(10, 20, 380, 74);

        Assert.Null(ShellContextCommandBar.HitTest(bounds, new Point(9, 30)));
        Assert.Null(ShellContextCommandBar.HitTest(bounds, new Point(390, 30)));
    }

    [Fact]
    public void Insert_FindsCommandRowAtBottomOfMenu()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 1, "Open"));
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 2, "Properties"));
            var commandBar = new ShellContextCommandBar(
                new IntPtr(1), menu, 96);

            Assert.True(commandBar.Insert());
            Assert.Equal(3, commandBar.FindCommandItemPosition());
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void VisibleCommands_CanHideShareForFolderSelections()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);

        try
        {
            var commandBar = new ShellContextCommandBar(
                new IntPtr(1), menu, 96,
                isVisible: static command => command != ShellContextCommand.Share);

            Assert.Equal(
                [ShellContextCommand.Cut, ShellContextCommand.Copy,
                 ShellContextCommand.Rename, ShellContextCommand.Delete],
                commandBar.VisibleCommands);
            Assert.Equal(ShellContextCommand.Delete,
                ShellContextCommandBar.HitTest(commandBar.VisibleCommands,
                    new Rectangle(0, 0, 400, 74), new Point(350, 30)));
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void CommandsAndLabels_MatchExplorerOrder()
    {
        Assert.Equal(
            ["Cut", "Copy", "Rename", "Share", "Delete"],
            ShellContextCommandBar.Commands
                .Select(ShellContextCommandBar.GetLabel)
                .ToArray());
    }

    [Theory]
    [InlineData("Cu&t\tCtrl+X", 0)]
    [InlineData("&Copy", 1)]
    [InlineData("Rename...", 2)]
    [InlineData("Share…", 3)]
    [InlineData("&Delete", 4)]
    public void ShellMenuLabels_MapWithoutQueryingEveryShellCommand(
        string label, int expectedValue)
    {
        Assert.True(ShellContextMenu.TryMapMenuLabel(label,
            out ShellContextCommand command));
        Assert.Equal((ShellContextCommand)expectedValue, command);
    }

    [Theory]
    [InlineData("&Send to", "Send to")]
    [InlineData("Send to\tShortcut", "Send to")]
    [InlineData("Send to…", "Send to")]
    public void NormalizeMenuLabel_FindsTheSendToSubmenu(string label,
        string expected)
    {
        Assert.Equal(expected, ShellContextMenu.NormalizeMenuLabel(label));
    }

    [Theory]
    [InlineData(1, "PinToStartScreen", false)]
    [InlineData(1, "UnpinFromStartScreen", false)]
    [InlineData(1, "pintohomefile", false)]
    [InlineData(1, "open", true)]
    [InlineData(2, "PinToStartScreen", false)]
    [InlineData(1, "", false)]
    [InlineData(0, "open", false)]
    public void SingleItemShellCommands_UseTheLiveExplorerSelection(
        int selectionCount, string verb, bool expected)
    {
        Assert.Equal(expected,
            ShellContextMenu.CanUseLiveSelectionContext(
                selectionCount, verb));
    }

    [Theory]
    [InlineData("PinToStartScreen", false)]
    [InlineData("pintostartscreen", false)]
    [InlineData("UnpinFromStartScreen", false)]
    [InlineData("pintohomefile", false)]
    [InlineData("unpinfromhomefile", false)]
    [InlineData("copyaspath", true)]
    public void UnsupportedExplorerOnlyCommands_AreHidden(
        string canonicalVerb, bool expected)
    {
        Assert.Equal(expected,
            ShellContextMenu.IsCanonicalCommandAvailable(canonicalVerb));
    }

    [Theory]
    [InlineData(false, "Pin to Quick access")]
    [InlineData(true, "Unpin from Quick access")]
    public void QuickAccessCommand_ReflectsTheCurrentPinState(
        bool isPinned, string expectedLabel)
    {
        Assert.Equal(expectedLabel,
            QuickAccessService.GetCommandLabel(isPinned));
    }

    [Theory]
    [InlineData(false, "Add to Favorites")]
    [InlineData(true, "Remove from Favorites")]
    public void FavoriteCommand_ReflectsTheCurrentPinState(
        bool isPinned, string expectedLabel)
    {
        Assert.Equal(expectedLabel,
            QuickAccessService.GetFavoriteCommandLabel(isPinned));
    }

    [Theory]
    [InlineData(false, "Pin to Start")]
    [InlineData(true, "Unpin from Start")]
    public void StartCommand_ReflectsTheCurrentPinState(
        bool isPinned, string expectedLabel)
    {
        Assert.Equal(expectedLabel,
            StartPinService.GetCommandLabel(isPinned));
    }

    [Fact]
    public void StartTileIdentity_IsStableAndFitsTheWindowsLimit()
    {
        string first = StartPinService.GetTileId(Path.GetTempPath());
        string second = StartPinService.GetTileId(Path.GetTempPath());
        string different = StartPinService.GetTileId(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
        Assert.True(first.Length <= 64);
    }

    [Fact]
    public void StartTileArguments_PreserveTheFullPath()
    {
        string path = Path.GetTempPath();
        string[] arguments = StartPinService.GetActivationArguments(path)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Path.GetFullPath(path),
            StartPinService.DecodeActivationPath(arguments));
    }

    [Fact]
    public void QuickAccessCommand_IsInsertedAfterOpen()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 1, "Open"));
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 2, "Properties"));

            Assert.True(PanelView.AddQuickAccessMenuCommand(menu,
                isPinned: true));

            var label = new System.Text.StringBuilder(64);
            Assert.True(NativeMethods.GetMenuString(menu, 1, label,
                label.Capacity, NativeMethods.MF_BYPOSITION) > 0);
            Assert.Equal("Unpin from Quick access", label.ToString());
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void FavoriteCommand_ReplacesTheClassicShellEntry()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 1, "Open"));
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 2, "Add to &Favorites"));

            Assert.True(PanelView.AddFavoriteMenuCommand(menu,
                isPinned: true));

            var labels = new List<string>();
            for (int position = 0;
                 position < NativeMethods.GetMenuItemCount(menu);
                 position++)
            {
                var label = new System.Text.StringBuilder(64);
                if (NativeMethods.GetMenuString(menu, (uint)position, label,
                        label.Capacity, NativeMethods.MF_BYPOSITION) > 0)
                    labels.Add(label.ToString());
            }

            Assert.Contains("Remove from Favorites", labels);
            Assert.DoesNotContain("Add to &Favorites", labels);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void StartCommand_ReplacesTheClassicShellEntry()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 1, "Open"));
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 2, "Pin to &Start"));

            Assert.True(PanelView.AddStartMenuCommand(menu,
                isPinned: true));

            var labels = new List<string>();
            for (int position = 0;
                 position < NativeMethods.GetMenuItemCount(menu);
                 position++)
            {
                var label = new System.Text.StringBuilder(64);
                if (NativeMethods.GetMenuString(menu, (uint)position, label,
                        label.Capacity, NativeMethods.MF_BYPOSITION) > 0)
                    labels.Add(label.ToString());
            }

            Assert.Contains("Unpin from Start", labels);
            Assert.DoesNotContain("Pin to &Start", labels);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void IdentityRelaunch_QuotesArgumentsForWindows(
        string argument, string expected)
    {
        Assert.Equal(expected,
            PackageIdentityLauncher.QuoteCommandLineArgument(argument));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    public void QuickAccessPinnedProperty_HandlesShellValueTypes(
        object value, bool expected)
    {
        Assert.Equal(expected,
            QuickAccessService.ConvertPinnedProperty(value));
    }

    [Theory]
    [InlineData(0x002B)]
    [InlineData(0x002C)]
    public void ForwardedOwnerDrawMessages_ReturnHandledToWindows(int message)
    {
        IntPtr result = IntPtr.Zero;

        Assert.True(ShellContextMenu.CompleteForwardedMenuMessage(
            message, 0, ref result));
        Assert.Equal((IntPtr)1, result);
    }

    [Fact]
    public void ForwardedMenuMessage_DoesNotTreatSFalseAsHandled()
    {
        IntPtr result = IntPtr.Zero;

        Assert.False(ShellContextMenu.CompleteForwardedMenuMessage(
            0x002C, 1, ref result));
        Assert.Equal(IntPtr.Zero, result);
    }

    [Theory]
    [InlineData("Open", "Open", "")]
    [InlineData("Copy\tCtrl+C", "Copy", "Ctrl+C")]
    [InlineData("Properties\tAlt+Enter", "Properties", "Alt+Enter")]
    public void DarkMenuLabels_SplitTextFromShortcut(
        string label, string expectedText, string expectedShortcut)
    {
        (string text, string shortcut) = DarkShellMenuRenderer.SplitLabel(label);

        Assert.Equal(expectedText, text);
        Assert.Equal(expectedShortcut, shortcut);
    }

    [Fact]
    public void DarkMenuMeasure_ReservesSpaceForSubmenuArrow()
    {
        using var renderer = new DarkShellMenuRenderer(96);
        var command = new DarkShellMenuRenderer.MenuItemVisual(
            "A sufficiently long context-menu command label for measurement",
            false, true, false, false, false, IntPtr.Zero);
        var submenu = command with { HasSubMenu = true };

        Assert.True(renderer.Measure(submenu).Width > renderer.Measure(command).Width);
    }

    [Fact]
    public void DarkMenuApply_ConvertsSubmenuParentToDarkOwnerDrawRow()
    {
        const uint MfPopup = 0x0010;
        IntPtr menu = NativeMethods.CreatePopupMenu();
        IntPtr submenu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        Assert.NotEqual(IntPtr.Zero, submenu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                MfPopup | NativeMethods.MF_STRING,
                unchecked((nuint)submenu.ToInt64()), "7-Zip"));
            using var renderer = new DarkShellMenuRenderer(96);

            renderer.Apply(menu);

            Assert.True(renderer.TryGetRegisteredVisual(
                menu, 0, out var visual));
            Assert.Equal("7-Zip", visual?.Label);
            Assert.True(visual?.HasSubMenu);
        }
        finally
        {
            // Destroying the parent recursively destroys its submenu.
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void DarkMenuApply_DoesNotOwnerDrawShellSubmenuRows()
    {
        const uint MiimFType = 0x0100;
        const uint MftOwnerDraw = 0x0100;
        const uint MfPopup = 0x0010;
        IntPtr menu = NativeMethods.CreatePopupMenu();
        IntPtr submenu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        Assert.NotEqual(IntPtr.Zero, submenu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(submenu,
                NativeMethods.MF_STRING, 42, "Dynamic submenu command"));
            Assert.True(NativeMethods.AppendMenu(menu,
                MfPopup | NativeMethods.MF_STRING,
                unchecked((nuint)submenu.ToInt64()), "Dynamic submenu"));
            using var renderer = new DarkShellMenuRenderer(96);

            renderer.Apply(menu);

            Assert.True(renderer.TryGetRegisteredVisual(menu, 0, out _));
            Assert.False(renderer.TryGetRegisteredVisual(submenu, 0, out _));
            var info = new TestMenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<TestMenuItemInfo>(),
                fMask = MiimFType,
            };
            Assert.True(GetMenuItemInfo(submenu, 0, true, ref info));
            Assert.Equal(0u, info.fType & MftOwnerDraw);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void DarkMenuApply_PreservesExtensionSubmenuItemData()
    {
        const uint MiimData = 0x0020;
        const uint MiimFType = 0x0100;
        const uint MftOwnerDraw = 0x0100;
        const uint MfPopup = 0x0010;
        nuint extensionData = unchecked((nuint)0x1234ABCD);
        IntPtr menu = NativeMethods.CreatePopupMenu();
        IntPtr submenu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        Assert.NotEqual(IntPtr.Zero, submenu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                MfPopup | NativeMethods.MF_STRING,
                unchecked((nuint)submenu.ToInt64()), "Dynamic extension"));
            var data = new TestMenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<TestMenuItemInfo>(),
                fMask = MiimData,
                dwItemData = extensionData,
            };
            Assert.True(SetMenuItemInfo(menu, 0, true, ref data));
            using var renderer = new DarkShellMenuRenderer(96);

            renderer.Apply(menu);

            var restored = new TestMenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<TestMenuItemInfo>(),
                fMask = MiimData | MiimFType,
            };
            Assert.True(GetMenuItemInfo(menu, 0, true, ref restored));
            Assert.Equal(extensionData, restored.dwItemData);
            Assert.NotEqual(0u, restored.fType & MftOwnerDraw);
            Assert.True(renderer.TryGetRegisteredVisual(menu, 0, out _));
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void DarkMenuApply_ReplacesRegistrationAfterDynamicMenuRebuild()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);

        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 42, "First command"));
            using var renderer = new DarkShellMenuRenderer(96);
            renderer.Apply(menu);

            Assert.True(NativeMethods.DeleteMenu(
                menu, 0, NativeMethods.MF_BYPOSITION));
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 43, "Replacement command"));
            renderer.Apply(menu);

            Assert.True(renderer.TryGetRegisteredVisual(
                menu, 0, out var visual));
            Assert.Equal("Replacement command", visual?.Label);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Fact]
    public void DarkMenuTextCorrection_RecolorsBlackPixelsOnDarkBackground()
    {
        int corrected = DarkShellMenuRenderer.CorrectDarkNeutralPixel(
            unchecked((int)0xFF000000), 40);

        Assert.Equal(unchecked((int)0xFFF5F5F5), corrected);
    }

    [Theory]
    [InlineData(unchecked((int)0xFF282828))]
    [InlineData(unchecked((int)0xFF001464))]
    public void DarkMenuTextCorrection_PreservesBackgroundAndColoredPixels(
        int pixel)
    {
        Assert.Equal(pixel, DarkShellMenuRenderer.CorrectDarkNeutralPixel(
            pixel, 40));
    }

    [Fact]
    public void DarkMenuMeasure_DoesNotClaimAnExtensionOwnerDrawCollision()
    {
        const uint MftOwnerDraw = 0x0100;
        IntPtr normalMenu = NativeMethods.CreatePopupMenu();
        IntPtr extensionMenu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, normalMenu);
        Assert.NotEqual(IntPtr.Zero, extensionMenu);

        IntPtr measurePointer = IntPtr.Zero;
        try
        {
            Assert.True(NativeMethods.AppendMenu(normalMenu,
                NativeMethods.MF_STRING, 42, "Normal command"));
            Assert.True(NativeMethods.AppendMenu(extensionMenu,
                MftOwnerDraw, 42, null));
            using var renderer = new DarkShellMenuRenderer(96);
            renderer.Apply(normalMenu);
            renderer.Apply(extensionMenu);

            var measure = new TestMeasureItemStruct
            {
                CtlType = 1,
                itemID = 42,
                itemData = 0,
            };
            measurePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<TestMeasureItemStruct>());
            Marshal.StructureToPtr(measure, measurePointer, false);
            Message message = Message.Create(IntPtr.Zero, 0x002C,
                IntPtr.Zero, measurePointer);

            Assert.False(renderer.TryHandleMessage(ref message));
        }
        finally
        {
            if (measurePointer != IntPtr.Zero)
                Marshal.FreeHGlobal(measurePointer);
            NativeMethods.DestroyMenu(normalMenu);
            NativeMethods.DestroyMenu(extensionMenu);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TestMenuItemInfo
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

    [StructLayout(LayoutKind.Sequential)]
    private struct TestMeasureItemStruct
    {
        internal uint CtlType;
        internal uint CtlID;
        internal uint itemID;
        internal uint itemWidth;
        internal uint itemHeight;
        internal nuint itemData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "GetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemInfo(IntPtr menu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition,
        ref TestMenuItemInfo itemInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "SetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuItemInfo(IntPtr menu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition,
        ref TestMenuItemInfo itemInfo);
}
