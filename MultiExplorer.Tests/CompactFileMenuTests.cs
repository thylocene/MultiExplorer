namespace MultiExplorer.Tests;

public sealed class CompactFileMenuTests
{
    [Fact]
    public void CopyAsPathMenuLabel_ShowsTheApplicationShortcut()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 42, "Copy as path"));

            Assert.Equal(1,
                PanelView.EnsureCopyAsPathShortcutLabels(menu));

            var label = new System.Text.StringBuilder(128);
            Assert.True(NativeMethods.GetMenuString(menu, 0, label,
                label.Capacity, NativeMethods.MF_BYPOSITION) > 0);
            Assert.Equal("Copy as path\tCtrl+Shift+C", label.ToString());
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Theory]
    [InlineData(96, 20)]
    [InlineData(144, 30)]
    [InlineData(192, 40)]
    public void MenuIconSize_ScalesAtTheTargetDpi(int dpi, int expectedPixels)
    {
        Assert.Equal(expectedPixels, NativeMenuIconSet.GetIconPixelSize(dpi));
    }

    [Theory]
    [InlineData(96, 32)]
    [InlineData(144, 48)]
    [InlineData(192, 64)]
    public void MenuIconBitmap_AddsDpiScaledTextSpacing(int dpi, int expectedPixels)
    {
        Assert.Equal(expectedPixels, NativeMenuIconSet.GetBitmapPixelWidth(dpi));
    }

    [Theory]
    [InlineData("Give access to", 4)]
    [InlineData("Restore previous versions", 5)]
    [InlineData("Include in library", 6)]
    [InlineData("Pin to Start", 7)]
    [InlineData("Unpin from Start", 7)]
    [InlineData("Pin to Quick access", 7)]
    [InlineData("Unpin from Quick access", 7)]
    [InlineData("Copy as path\tCtrl+Shift+C", 8)]
    [InlineData("Create shortcut", 9)]
    [InlineData("&Properties", 10)]
    public void FolderMenuCommand_MapsToHighDpiGlyph(string label,
        int expected)
    {
        Assert.True(PanelView.TryGetFolderMenuGlyph(label,
            out CompactMenuGlyph glyph));
        Assert.Equal((CompactMenuGlyph)expected, glyph);
    }
}
