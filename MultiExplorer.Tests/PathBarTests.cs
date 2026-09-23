namespace MultiExplorer.Tests;

public sealed class PathBarTests
{
    [Fact]
    public void SetHistory_KeepsOnlyFifteenMostRecentEntries()
    {
        using var pathBar = new PathBar();
        string[] paths = Enumerable.Range(1, 20)
            .Select(index => $@"C:\History\{index:D2}")
            .ToArray();

        pathBar.SetHistory(paths);

        Assert.Equal(PathBar.MaxHistoryEntries, pathBar.GetHistory().Count);
        Assert.Equal(paths.Take(PathBar.MaxHistoryEntries), pathBar.GetHistory());
    }

    [Fact]
    public void SetHistory_RemovesDuplicatesBeforeApplyingLimit()
    {
        using var pathBar = new PathBar();
        string[] paths =
        [
            @"C:\Current",
            @"C:\Duplicate",
            @"c:\duplicate\",
            .. Enumerable.Range(1, 20)
                .Select(index => $@"C:\Older\{index:D2}")
        ];

        pathBar.SetHistory(paths);

        List<string> history = pathBar.GetHistory();
        Assert.Equal(PathBar.MaxHistoryEntries, history.Count);
        Assert.Equal(@"C:\Current", history[0]);
        Assert.Single(history.Where(path =>
            path.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(@"C:\Duplicate", StringComparison.OrdinalIgnoreCase)));
    }
}
