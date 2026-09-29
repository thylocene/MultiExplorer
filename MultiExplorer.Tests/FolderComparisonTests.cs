namespace MultiExplorer.Tests;

public sealed class FolderComparisonTests
{
    [Fact]
    public async Task CompareAsync_ConfirmsMatchingFourFileFoldersWereScanned()
    {
        using var folders = new ComparisonFolders();
        foreach (int index in Enumerable.Range(1, 4))
        {
            string name = $"file-{index}.txt";
            string content = $"content-{index}";
            folders.WriteLeft(name, content);
            folders.WriteRight(name, content);
        }

        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);

        Assert.Empty(result.Differences);
        Assert.Equal(4, result.LeftItems.Count);
        Assert.Equal(4, result.RightItems.Count);
        Assert.Equal(4,
            FolderComparisonService.GetMatchingFiles(result, 1000).Count);
        Assert.Equal("Compared 4 files per pane; 4 matches shown; no differences.",
            FolderCompareDialog.FormatComparisonStatus(result, 0, 4));
    }

    [Fact]
    public async Task CompareAsync_KeepsMatchingFilesWhenAnotherFileDiffers()
    {
        using var folders = new ComparisonFolders();
        foreach (int index in Enumerable.Range(1, 3))
        {
            string name = $"same-{index}.txt";
            folders.WriteLeft(name, "same");
            folders.WriteRight(name, "same");
        }
        folders.WriteLeft("different.txt", "short");
        folders.WriteRight("different.txt", "a longer value");

        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);

        Assert.Single(result.Differences);
        Assert.Equal(3,
            FolderComparisonService.GetMatchingFiles(result, 1000).Count);
        Assert.Equal("Compared 4 left / 4 right files; 3 matches shown; "
            + "1 difference; 0 selected.",
            FolderCompareDialog.FormatComparisonStatus(result, 0, 3));
    }

    [Theory]
    [InlineData(60, 0, nameof(FolderSyncAction.CopyLeftToRight))]
    [InlineData(0, 60, nameof(FolderSyncAction.CopyRightToLeft))]
    [InlineData(1, 0, nameof(FolderSyncAction.Skip))]
    [InlineData(2, 0, nameof(FolderSyncAction.Skip))]
    public async Task CompareAsync_SuggestsCopyForDifferentSizeOnlyWhenOneSideIsClearlyNewer(
        int leftOffsetSeconds, int rightOffsetSeconds,
        string expectedAction)
    {
        using var folders = new ComparisonFolders();
        const string name = "different-size.txt";
        folders.WriteLeft(name, "short");
        folders.WriteRight(name, "a longer value");
        DateTime baseline = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(folders.Left, name),
            baseline.AddSeconds(leftOffsetSeconds));
        File.SetLastWriteTimeUtc(Path.Combine(folders.Right, name),
            baseline.AddSeconds(rightOffsetSeconds));

        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);

        FolderDifference difference = Assert.Single(result.Differences);
        Assert.Equal(FolderDifferenceKind.DifferentSize, difference.Kind);
        Assert.Equal(expectedAction, difference.SuggestedAction.ToString());
    }

    [Fact]
    public async Task CompareAsync_ReportsDifferencesAndCollapsesMissingFolderContents()
    {
        using var folders = new ComparisonFolders();
        folders.WriteLeft("same.txt", "same");
        folders.WriteRight("same.txt", "same");
        folders.WriteLeft("only-left.txt", "left");
        folders.WriteRight("only-right.txt", "right");
        folders.WriteLeft(@"missing-folder\inside.txt", "inside");
        folders.WriteLeft(@"shared\left-newer.txt", "same");
        folders.WriteRight(@"shared\left-newer.txt", "same");
        folders.WriteLeft(@"shared\right-newer.txt", "same");
        folders.WriteRight(@"shared\right-newer.txt", "same");
        folders.WriteLeft("different-size.txt", "longer");
        folders.WriteRight("different-size.txt", "short");
        folders.WriteLeft("conflict", "file");
        Directory.CreateDirectory(Path.Combine(folders.Right, "conflict"));

        DateTime baseline = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(Path.Combine(folders.Left, "same.txt"), baseline);
        File.SetLastWriteTimeUtc(Path.Combine(folders.Right, "same.txt"), baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(folders.Left, @"shared\left-newer.txt"),
            baseline.AddMinutes(1));
        File.SetLastWriteTimeUtc(
            Path.Combine(folders.Right, @"shared\left-newer.txt"), baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(folders.Left, @"shared\right-newer.txt"), baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(folders.Right, @"shared\right-newer.txt"),
            baseline.AddMinutes(1));

        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);
        Dictionary<string, FolderDifferenceKind> differences = result.Differences
            .ToDictionary(static difference => difference.RelativePath,
                static difference => difference.Kind, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(FolderDifferenceKind.OnlyLeft,
            differences["only-left.txt"]);
        Assert.Equal(FolderDifferenceKind.OnlyRight,
            differences["only-right.txt"]);
        Assert.Equal(FolderDifferenceKind.OnlyLeft,
            differences["missing-folder"]);
        Assert.DoesNotContain(@"missing-folder\inside.txt", differences.Keys);
        Assert.Equal(FolderDifferenceKind.LeftNewer,
            differences[@"shared\left-newer.txt"]);
        Assert.Equal(FolderDifferenceKind.RightNewer,
            differences[@"shared\right-newer.txt"]);
        Assert.Equal(FolderDifferenceKind.DifferentSize,
            differences["different-size.txt"]);
        Assert.Equal(FolderDifferenceKind.TypeConflict,
            differences["conflict"]);
        Assert.DoesNotContain("same.txt", differences.Keys);
    }

    [Fact]
    public async Task BuildPlan_MapsSelectedCopiesAndDeletionsToTheirActualPaths()
    {
        using var folders = new ComparisonFolders();
        folders.WriteLeft(@"shared\left.txt", "left");
        folders.WriteRight(@"shared\right.txt", "right");
        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);
        FolderDifference copy = Assert.Single(result.Differences,
            static difference => difference.RelativePath == @"shared\left.txt");
        FolderDifference delete = Assert.Single(result.Differences,
            static difference => difference.RelativePath == @"shared\right.txt");

        IReadOnlyList<FolderSyncPlanItem> plan = FolderComparisonService.BuildPlan(
            result,
            [
                new FolderSyncSelection(copy, FolderSyncAction.CopyLeftToRight),
                new FolderSyncSelection(delete, FolderSyncAction.DeleteRight),
            ]);

        Assert.Equal(Path.Combine(folders.Left, @"shared\left.txt"), plan[0].SourcePath);
        Assert.Equal(Path.Combine(folders.Right, "shared"), plan[0].DestinationDirectory);
        Assert.Equal(Path.Combine(folders.Right, @"shared\left.txt"), plan[0].TargetPath);
        Assert.Equal(Path.Combine(folders.Right, @"shared\right.txt"), plan[1].SourcePath);
        Assert.Null(plan[1].DestinationDirectory);
    }

    [Fact]
    public async Task ValidatePlanAsync_RejectsFilesChangedAfterComparison()
    {
        using var folders = new ComparisonFolders();
        folders.WriteLeft("change.txt", "before");
        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);
        FolderDifference difference = Assert.Single(result.Differences);
        FolderSyncSelection[] selections =
            [new(difference, FolderSyncAction.CopyLeftToRight)];

        await FolderComparisonService.ValidatePlanAsync(result, selections);
        folders.WriteLeft("change.txt", "changed contents");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FolderComparisonService.ValidatePlanAsync(result, selections));
    }

    [Fact]
    public async Task ValidatePlanAsync_RejectsNewTargetAfterComparison()
    {
        using var folders = new ComparisonFolders();
        folders.WriteLeft("new.txt", "left");
        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);
        FolderDifference difference = Assert.Single(result.Differences);
        FolderSyncSelection[] selections =
            [new(difference, FolderSyncAction.CopyLeftToRight)];
        folders.WriteRight("new.txt", "unexpected target");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FolderComparisonService.ValidatePlanAsync(result, selections));
    }

    [Fact]
    public async Task ValidatePlanAsync_RejectsChangedContentsOfMissingFolder()
    {
        using var folders = new ComparisonFolders();
        folders.WriteLeft(@"missing\first.txt", "first");
        FolderComparisonResult result = await FolderComparisonService.CompareAsync(
            folders.Left, folders.Right);
        FolderDifference difference = Assert.Single(result.Differences);
        Assert.Equal("missing", difference.RelativePath);
        FolderSyncSelection[] selections =
            [new(difference, FolderSyncAction.CopyLeftToRight)];
        folders.WriteLeft(@"missing\second.txt", "second");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FolderComparisonService.ValidatePlanAsync(result, selections));
    }

    [Fact]
    public async Task CompareAsync_RejectsOverlappingRoots()
    {
        using var folders = new ComparisonFolders();
        string child = Path.Combine(folders.Left, "child");
        Directory.CreateDirectory(child);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            FolderComparisonService.CompareAsync(folders.Left, child));
    }

    private sealed class ComparisonFolders : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            $"MultiExplorer-compare-{Guid.NewGuid():N}");

        internal string Left => Path.Combine(_root, "left");
        internal string Right => Path.Combine(_root, "right");

        internal ComparisonFolders()
        {
            Directory.CreateDirectory(Left);
            Directory.CreateDirectory(Right);
        }

        internal void WriteLeft(string relativePath, string contents) =>
            Write(Left, relativePath, contents);

        internal void WriteRight(string relativePath, string contents) =>
            Write(Right, relativePath, contents);

        private static void Write(string root, string relativePath, string contents)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
