using Xunit;

namespace MultiExplorer.Tests;

public sealed class ComInteropTests
{
    [Theory]
    [InlineData(typeof(NativeMethods.IExplorerBrowserEvents))]
    [InlineData(typeof(NativeMethods.IFolderViewSettings))]
    public void ManagedComCallbackInterface_IsPubliclyVisible(Type interfaceType)
    {
        Assert.True(interfaceType.IsVisible,
            $"{interfaceType.FullName} must be visible to COM marshalling.");
    }

    [Fact]
    public void NamespaceTreeControl_UsesWindowsSdkInterfaceIdentifier()
        => Assert.Equal(
            new Guid("028212A3-B627-47E9-8856-C14265554E4F"),
            typeof(NativeMethods.INameSpaceTreeControl).GUID);

    [Theory]
    [InlineData(new[] { "Desktop", "This PC", "Windows (C:)", "Projects", "MultiExplorer" }, @"C:\Projects\MultiExplorer")]
    [InlineData(new[] { "Desktop", "This PC", "D:" }, @"D:\")]
    public void NavigationTreePath_IsBuiltFromDriveAncestor(string[] parts, string expected)
        => Assert.Equal(expected, ExplorerHost.TryBuildNavigationTreePath(parts));

    [Fact]
    public void NavigationTreePath_RejectsPinnedItemWithoutDriveAncestor()
        => Assert.Null(ExplorerHost.TryBuildNavigationTreePath(
            new[] { "Desktop", "Quick access", "Projects" }));

    [Fact]
    public void ContextMenuScreenLocation_UnpacksSignedCoordinates()
    {
        int packed = unchecked((ushort)-120) | (unchecked((ushort)-45) << 16);

        Assert.Equal(new System.Drawing.Point(-120, -45),
            ExplorerHost.GetContextMenuScreenLocation(
                (IntPtr)packed, new System.Drawing.Point(10, 20)));
    }

    [Fact]
    public void ContextMenuScreenLocation_UsesCursorForKeyboardRequest()
    {
        var cursor = new System.Drawing.Point(250, 300);

        Assert.Equal(cursor, ExplorerHost.GetContextMenuScreenLocation(
            (IntPtr)(-1), cursor));
    }

    [Fact]
    public void NavigationContextPath_MouseRequest_DoesNotUseStaleSelection()
        => Assert.Null(ExplorerHost.SelectNavigationContextPath(
            hitPath: null,
            selectedPath: @"C:\Intel",
            allowSelectedFallback: false));

    [Fact]
    public void NavigationContextPath_KeyboardRequest_UsesCurrentSelection()
        => Assert.Equal(@"C:\Intel", ExplorerHost.SelectNavigationContextPath(
            hitPath: null,
            selectedPath: @"C:\Intel",
            allowSelectedFallback: true));

    [Fact]
    public void NavigationContextPath_ClickedFolderWinsOverPreviousSelection()
        => Assert.Equal(@"C:\Astronomy", ExplorerHost.SelectNavigationContextPath(
            hitPath: @"C:\Astronomy",
            selectedPath: @"C:\Intel",
            allowSelectedFallback: true));

}
