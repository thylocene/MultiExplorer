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
}
