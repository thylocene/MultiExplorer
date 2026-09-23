namespace MultiExplorer.Tests;

using System.Runtime.InteropServices;

public sealed class ExplorerFolderViewStateTests
{
    private const int ENotImpl = unchecked((int)0x80004001);

    [Fact]
    public void FolderViewSettings_DefersModeAndIconSizeToExplorerPropertyBag()
    {
        var settings = new FolderViewSettingsImpl();

        Assert.Equal(ENotImpl, settings.GetViewMode(out uint viewMode));
        Assert.Equal(0u, viewMode);
        Assert.Equal(ENotImpl, settings.GetIconSize(out uint iconSize));
        Assert.Equal(0u, iconSize);
    }

    [Fact]
    public void FolderViewSettings_DefaultsToNameAscending()
    {
        var settings = new FolderViewSettingsImpl();
        IntPtr sortColumnPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<NativeMethods.SORTCOLUMN>());
        try
        {
            Assert.Equal(0, settings.GetSortColumns(
                sortColumnPointer, 1, out uint columnCount));

            Assert.Equal(1u, columnCount);
            NativeMethods.SORTCOLUMN sortColumn =
                Marshal.PtrToStructure<NativeMethods.SORTCOLUMN>(sortColumnPointer);
            Assert.Equal(
                new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
                sortColumn.propkey.fmtid);
            Assert.Equal(10u, sortColumn.propkey.pid);
            Assert.Equal(1, sortColumn.direction);
        }
        finally
        {
            Marshal.FreeHGlobal(sortColumnPointer);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void SupportedViewModes_MatchShellFolderViewRange(
        uint viewMode, bool expected)
    {
        Assert.Equal(expected,
            ExplorerFolderViewState.IsSupportedViewMode(viewMode));
    }
}
