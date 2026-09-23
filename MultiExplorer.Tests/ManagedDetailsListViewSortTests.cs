namespace MultiExplorer.Tests;

public sealed class ManagedDetailsListViewSortTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ClickingCurrentColumn_TogglesDirection(
        bool currentAscending, bool expectedAscending)
    {
        (string columnId, bool ascending) =
            ManagedDetailsListView.GetNextSortState(
                "Name", currentAscending, "Name");

        Assert.Equal("Name", columnId);
        Assert.Equal(expectedAscending, ascending);
    }

    [Theory]
    [InlineData("Name", "DateModified")]
    [InlineData("DateModified", "Type")]
    [InlineData("Type", "Size")]
    [InlineData("Size", "System.Author")]
    public void ClickingDifferentColumn_StartsAscending(
        string currentColumnId, string selectedColumnId)
    {
        (string columnId, bool ascending) =
            ManagedDetailsListView.GetNextSortState(
                currentColumnId, currentAscending: false, selectedColumnId);

        Assert.Equal(selectedColumnId, columnId);
        Assert.True(ascending);
    }
}
