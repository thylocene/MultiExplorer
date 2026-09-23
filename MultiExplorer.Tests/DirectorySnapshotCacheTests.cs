namespace MultiExplorer.Tests;

public sealed class DirectorySnapshotCacheTests
{
    [Fact]
    public async Task GetAsync_CoalescesReadsAndInvalidateRefreshesTheSnapshot()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"MultiExplorer-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "first.txt"), "first");

            Task<DirectorySnapshot> firstRead = DirectorySnapshotCache.GetAsync(root);
            Task<DirectorySnapshot> secondRead = DirectorySnapshotCache.GetAsync(root);
            DirectorySnapshot[] snapshots = await Task.WhenAll(firstRead, secondRead);

            Assert.Same(snapshots[0], snapshots[1]);
            Assert.Single(snapshots[0].Items);

            File.WriteAllText(Path.Combine(root, "second.txt"), "second");
            DirectorySnapshotCache.Invalidate(root);

            DirectorySnapshot refreshed = await DirectorySnapshotCache.GetAsync(root);

            Assert.NotSame(snapshots[0], refreshed);
            Assert.Equal(2, refreshed.Items.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CountItems_HonorsHiddenAndSystemVisibility()
    {
        var snapshot = new DirectorySnapshot(
            @"C:\test",
            DateTime.UtcNow,
            [
                CreateItem("visible.txt", FileAttributes.Normal),
                CreateItem("hidden.txt", FileAttributes.Hidden),
                CreateItem("system.txt", FileAttributes.System),
                CreateItem("folder", FileAttributes.Directory),
            ]);

        Assert.Equal(
            new DirectoryItemCount(FileCount: 1, FolderCount: 1),
            snapshot.CountItems(
                showHiddenItems: false,
                showProtectedSystemItems: false));
        Assert.Equal(
            new DirectoryItemCount(FileCount: 2, FolderCount: 1),
            snapshot.CountItems(
                showHiddenItems: true,
                showProtectedSystemItems: false));
        Assert.Equal(
            new DirectoryItemCount(FileCount: 3, FolderCount: 1),
            snapshot.CountItems(
                showHiddenItems: true,
                showProtectedSystemItems: true));
    }

    private static DirectorySnapshotItem CreateItem(
        string name,
        FileAttributes attributes) =>
        new(
            name,
            Path.Combine(@"C:\test", name),
            DateTime.MinValue,
            DateTime.MinValue,
            attributes,
            Length: 0);
}
