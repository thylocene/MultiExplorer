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

    [Theory]
    [InlineData(new[] { "Desktop", "This PC", "Windows (C:)", "Projects", "MultiExplorer" }, @"C:\Projects\MultiExplorer")]
    [InlineData(new[] { "Desktop", "This PC", "D:" }, @"D:\")]
    public void NavigationTreePath_IsBuiltFromDriveAncestor(string[] parts, string expected)
        => Assert.Equal(expected, ExplorerHost.TryBuildNavigationTreePath(parts));

    [Fact]
    public void NavigationTreePath_RejectsPinnedItemWithoutDriveAncestor()
        => Assert.Null(ExplorerHost.TryBuildNavigationTreePath(
            new[] { "Desktop", "Quick access", "Projects" }));

}
