namespace MultiExplorer.Tests;

public class MainFormLayoutTests
{
    [Fact]
    public void GetApplicationShortcut_MapsF1WithoutModifiers()
    {
        Assert.Equal(CommandBar.Cmd.Help,
            MainForm.GetApplicationShortcut(0x0100, (IntPtr)Keys.F1, Keys.None));
    }

    [Fact]
    public void GetApplicationShortcut_MapsF5WithoutModifiers()
    {
        Assert.Equal(CommandBar.Cmd.Refresh,
            MainForm.GetApplicationShortcut(0x0100, (IntPtr)Keys.F5, Keys.None));
    }

    [Theory]
    [InlineData(0x4F, CommandBar.Cmd.FolderOptions)]
    [InlineData(0x4C, CommandBar.Cmd.ViewLog)]
    [InlineData(0x51, CommandBar.Cmd.Exit)]
    [InlineData(0x4E, CommandBar.Cmd.NewTab)]
    [InlineData(0x54, CommandBar.Cmd.NewTab)]
    public void GetApplicationShortcut_MapsSupportedCtrlKeys(
        int virtualKey, CommandBar.Cmd expected)
    {
        Assert.Equal(expected,
            MainForm.GetApplicationShortcut(0x0100, (IntPtr)virtualKey, Keys.Control));
    }

    [Fact]
    public void GetApplicationShortcut_MapsCtrlShiftNToNewFolder()
    {
        Assert.Equal(CommandBar.Cmd.NewFolder,
            MainForm.GetApplicationShortcut(
                0x0100,
                (IntPtr)Keys.N,
                Keys.Control | Keys.Shift));
    }

    [Fact]
    public void GetApplicationShortcut_MapsCtrlShiftCToCopyPaths()
    {
        Assert.Equal(CommandBar.Cmd.CopyPaths,
            MainForm.GetApplicationShortcut(
                0x0100,
                (IntPtr)Keys.C,
                Keys.Control | Keys.Shift));
    }

    [Theory]
    [InlineData(0x4F, Keys.Control | Keys.Shift)]
    [InlineData(0x4C, Keys.Alt)]
    [InlineData(0x48, Keys.Control)]
    [InlineData((int)Keys.F1, Keys.Control)]
    [InlineData((int)Keys.F1, Keys.Shift)]
    [InlineData((int)Keys.F5, Keys.Control)]
    [InlineData((int)Keys.F5, Keys.Shift)]
    public void GetApplicationShortcut_RejectsOtherCombinations(
        int virtualKey, Keys modifiers)
    {
        Assert.Null(MainForm.GetApplicationShortcut(
            0x0100, (IntPtr)virtualKey, modifiers));
    }

    [Theory]
    [InlineData(0x0100, 0x51, Keys.Control, true)]
    [InlineData(0x0100, 0x51, Keys.Control | Keys.Shift, false)]
    [InlineData(0x0100, 0x51, Keys.Control | Keys.Alt, false)]
    [InlineData(0x0100, 0x50, Keys.Control, false)]
    [InlineData(0x0101, 0x51, Keys.Control, false)]
    public void IsQuitShortcut_MatchesOnlyCtrlQ(
        int message, int virtualKey, Keys modifiers, bool expected)
    {
        Assert.Equal(expected,
            MainForm.IsQuitShortcut(message, (IntPtr)virtualKey, modifiers));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldShowMainWindowForGlobalHotkey_UsesTrayState(
        bool isInSystemTray, bool expected)
    {
        Assert.Equal(expected,
            MainForm.ShouldShowMainWindowForGlobalHotkey(isInSystemTray));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1213)]
    public void CanLayoutExpandedPanels_RejectsTransientMinimizedWidths(int layoutWidth)
    {
        Assert.False(MainForm.CanLayoutExpandedPanels(layoutWidth));
    }

    [Fact]
    public void CanLayoutExpandedPanels_AcceptsMinimumUsableWidth()
    {
        Assert.True(MainForm.CanLayoutExpandedPanels(1214));
    }

    [Theory]
    [InlineData(600, 1600, 600)]
    [InlineData(50, 1600, 600)]
    [InlineData(1500, 1600, 986)]
    [InlineData(50, 1200, 593)]
    public void ConstrainSplitterLeft_KeepsBothPanelsUsable(
        int splitterLeft, int layoutWidth, int expected)
    {
        Assert.Equal(expected, MainForm.ConstrainSplitterLeft(splitterLeft, layoutWidth));
    }

    [Theory]
    [InlineData(0.5, 1200, 593)]
    [InlineData(0.25, 1200, 593)]
    [InlineData(0.5, 800, 393)]
    public void SplitterLeftFromRatio_PreservesRelativePosition(
        double ratio, int layoutWidth, int expected)
    {
        Assert.Equal(expected, MainForm.SplitterLeftFromRatio(ratio, layoutWidth));
    }

    [Theory]
    [InlineData(0.01, 800, 393)]
    [InlineData(0.99, 800, 393)]
    public void SplitterLeftFromRatio_KeepsBothPanelsUsable(
        double ratio, int layoutWidth, int expected)
    {
        Assert.Equal(expected, MainForm.SplitterLeftFromRatio(ratio, layoutWidth));
    }

    [Fact]
    public void CalculateSplitterRatio_UsesWidthExcludingSplitterBar()
    {
        Assert.Equal(0.5, MainForm.CalculateSplitterRatio(593, 1200), precision: 3);
    }

    [Theory]
    [InlineData(1000, 2500, MainForm.ActiveRefreshIntervalMilliseconds)]
    [InlineData(2499, 2500, MainForm.ActiveRefreshIntervalMilliseconds)]
    [InlineData(2500, 2500, MainForm.IdleRefreshIntervalMilliseconds)]
    [InlineData(3000, 2500, MainForm.IdleRefreshIntervalMilliseconds)]
    public void GetPathPollInterval_UsesFastWindowThenIdleWatchdog(
        long now, long fastRefreshUntil, int expected)
    {
        Assert.Equal(expected,
            MainForm.GetPathPollInterval(now, fastRefreshUntil));
    }
}
