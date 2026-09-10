namespace MultiExplorer.Tests;

public sealed class TabMoveTests
{
    [Theory]
    [InlineData(4, 2, 0, 0)]
    [InlineData(4, 0, 3, 2)]
    [InlineData(4, 1, 4, 3)]
    [InlineData(4, 2, 2, 2)]
    [InlineData(4, 2, 3, 2)]
    public void ReorderTargetIndex_AccountsForRemovalBeforeInsertion(
        int count, int source, int insertion, int expected)
    {
        Assert.Equal(expected,
            PanelView.ReorderTargetIndex(count, source, insertion));
    }
}
