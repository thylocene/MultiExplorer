namespace MultiExplorer.Tests;

public sealed class SelectionSizeCalculatorTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(800, "800 B")]
    [InlineData(1_000, "1.00 KB")]
    [InlineData(2_889, "2.89 KB")]
    [InlineData(1_000_000, "1.00 MB")]
    [InlineData(1_120_000, "1.12 MB")]
    [InlineData(1_000_000_000, "1.00 GB")]
    [InlineData(1_100_000_000, "1.10 GB")]
    public void FormatBytes_UsesDecimalStorageUnits(long bytes, string expected)
    {
        Assert.Equal(expected, SelectionSizeCalculator.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(0, true, 0, 0, "Selected: 0 files (0 B)")]
    [InlineData(1_180_000, true, 4, 0, "Selected: 4 files (1.18 MB)")]
    [InlineData(800, true, 1, 0, "Selected: 1 file (800 B)")]
    [InlineData(2_889, true, 0, 1, "Selected: 1 folder (2.89 KB)")]
    [InlineData(1_120_000, true, 4, 2, "Selected: 4 files, 2 folders (1.12 MB)")]
    [InlineData(800, false, 2, 0, "Selected: 2 files (at least 800 B)")]
    public void FormatSelection_IncludesTopLevelItemCounts(
        long bytes,
        bool isComplete,
        int selectedFileCount,
        int selectedFolderCount,
        string expected)
    {
        var result = new SelectedSizeResult(
            bytes,
            isComplete,
            selectedFileCount,
            selectedFolderCount);

        Assert.Equal(expected, SelectionSizeCalculator.FormatSelection(result));
    }

    [Fact]
    public async Task Calculate_SumsFilesAndSelectedFoldersRecursively()
    {
        string root = CreateTestDirectory();
        string selectedFolder = Directory.CreateDirectory(
            Path.Combine(root, "selected-folder")).FullName;
        string nestedFolder = Directory.CreateDirectory(
            Path.Combine(selectedFolder, "nested")).FullName;
        string selectedFile = Path.Combine(root, "selected.bin");

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(selectedFolder, "first.bin"), new byte[800]);
            await File.WriteAllBytesAsync(
                Path.Combine(nestedFolder, "second.bin"), new byte[1_200]);
            await File.WriteAllBytesAsync(selectedFile, new byte[889]);

            SelectedSizeResult result = SelectionSizeCalculator.Calculate(
                [selectedFolder, selectedFile], CancellationToken.None);

            Assert.Equal(2_889, result.Bytes);
            Assert.True(result.IsComplete);
            Assert.Equal(1, result.SelectedFileCount);
            Assert.Equal(1, result.SelectedFolderCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Calculate_DoesNotEnumerateNetworkPaths()
    {
        SelectedSizeResult result = SelectionSizeCalculator.Calculate(
            [@"\\unreachable.invalid\share\folder"], CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.False(result.IsComplete);
        Assert.Equal(0, result.SelectedFileCount);
        Assert.Equal(0, result.SelectedFolderCount);
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "MultiExplorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
