namespace MultiExplorer.Tests;

public class MainFormLayoutTests
{
    [Theory]
    [InlineData(0x4F, CommandBar.Cmd.FolderOptions)]
    [InlineData(0x4C, CommandBar.Cmd.ViewLog)]
    [InlineData(0x51, CommandBar.Cmd.Exit)]
    public void GetApplicationShortcut_MapsSupportedCtrlKeys(
        int virtualKey, CommandBar.Cmd expected)
    {
        Assert.Equal(expected,
            MainForm.GetApplicationShortcut(0x0100, (IntPtr)virtualKey, Keys.Control));
    }

    [Theory]
    [InlineData(0x4F, Keys.Control | Keys.Shift)]
    [InlineData(0x4C, Keys.Alt)]
    [InlineData(0x50, Keys.Control)]
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
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(213)]
    public void CanLayoutExpandedPanels_RejectsTransientMinimizedWidths(int layoutWidth)
    {
        Assert.False(MainForm.CanLayoutExpandedPanels(layoutWidth));
    }

    [Fact]
    public void CanLayoutExpandedPanels_AcceptsMinimumUsableWidth()
    {
        Assert.True(MainForm.CanLayoutExpandedPanels(214));
    }

    [Theory]
    [InlineData(500, 1200, 500)]
    [InlineData(50, 1200, 100)]
    [InlineData(1150, 1200, 1086)]
    public void ConstrainSplitterLeft_KeepsBothPanelsUsable(
        int splitterLeft, int layoutWidth, int expected)
    {
        Assert.Equal(expected, MainForm.ConstrainSplitterLeft(splitterLeft, layoutWidth));
    }
}
