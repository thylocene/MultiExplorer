namespace MultiExplorer.Tests;

public class MainFormLayoutTests
{
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
