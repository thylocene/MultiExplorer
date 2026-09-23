namespace MultiExplorer.Tests;

public sealed class FileSharingServiceTests
{
    [Fact]
    public async Task CanShare_AllowsFilesAndRejectsFoldersAndMixedSelections()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "MultiExplorer.Tests", $"share-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "document.txt");
        await File.WriteAllTextAsync(file, "test");

        try
        {
            Assert.True(FileSharingService.CanShare([file]));
            Assert.True(FileSharingService.CanShare([file, file]));
            Assert.False(FileSharingService.CanShare([root]));
            Assert.False(FileSharingService.CanShare([file, root]));
            Assert.False(FileSharingService.CanShare([]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetShareableFiles_RemovesMissingAndDuplicateFiles()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "MultiExplorer.Tests", $"share-normalize-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "first.txt");
        string second = Path.Combine(root, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");

        try
        {
            Assert.Equal([first, second], FileSharingService.GetShareableFiles(
                [first, first.ToUpperInvariant(), second,
                 Path.Combine(root, "missing.txt"), root]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(26, 2)]
    [InlineData(2, 3)]
    public void MapEmailResult_ProducesAUsefulStatus(int result,
        int expected)
    {
        Assert.Equal((EmailShareStatus)expected,
            FileSharingService.MapEmailResult(result).Status);
    }
}
