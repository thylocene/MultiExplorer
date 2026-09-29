namespace MultiExplorer.Tests;

public sealed class FileOperationMetricsTests
{
    [Fact]
    public void Policy_SkipsRecursiveMetricsForDeletes()
    {
        var request = new FileOperationRequest
        {
            Kind = FileOperationKind.Delete,
            Sources = [@"C:\Source\folder"],
        };

        Assert.False(FileOperationMetrics.ShouldRecursivelyMeasure(request));
    }

    [Fact]
    public void Policy_SkipsRecursiveMetricsForSameVolumeMoves()
    {
        var request = new FileOperationRequest
        {
            Kind = FileOperationKind.Move,
            Sources = [@"C:\Source\folder", @"C:\Source\file.txt"],
            Destination = @"C:\Destination",
        };

        Assert.True(FileOperationMetrics.AreAllSourcesOnDestinationVolume(
            request.Sources, request.Destination));
        Assert.False(FileOperationMetrics.ShouldRecursivelyMeasure(request));
    }

    [Fact]
    public void Policy_UsesRecursiveMetricsForCrossVolumeMoves()
    {
        var request = new FileOperationRequest
        {
            Kind = FileOperationKind.Move,
            Sources = [@"C:\Source\folder"],
            Destination = @"D:\Destination",
        };

        Assert.False(FileOperationMetrics.AreAllSourcesOnDestinationVolume(
            request.Sources, request.Destination));
        Assert.True(FileOperationMetrics.ShouldRecursivelyMeasure(request));
    }

    [Fact]
    public void Policy_RetainsRecursiveMetricsForNetworkCopies()
    {
        var request = new FileOperationRequest
        {
            Kind = FileOperationKind.Copy,
            Sources = [@"\\server\share\folder"],
            Destination = @"C:\Destination",
        };

        Assert.True(FileOperationMetrics.ShouldRecursivelyMeasure(request));
    }

    [Fact]
    public void Calculate_ReturnsExactMetricsForSmallLocalCopy()
    {
        string testRoot = CreateTestRoot();
        string sourceDirectory = Path.Combine(testRoot, "source");
        string nestedDirectory = Path.Combine(sourceDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "first.txt"), "first");
        File.WriteAllText(Path.Combine(nestedDirectory, "second.txt"), "second");

        try
        {
            var request = new FileOperationRequest
            {
                Kind = FileOperationKind.Copy,
                Sources = [sourceDirectory],
                Destination = Path.Combine(testRoot, "destination"),
            };

            FileOperationSourceMetrics metrics = FileOperationMetrics.Calculate(
                request,
                static () => false);

            Assert.True(metrics.IsComplete);
            Assert.Equal(4, metrics.ItemCount);
            Assert.Equal(11UL, metrics.TotalBytes);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Calculate_SkipsDirectoryTraversalForDeletes()
    {
        string testRoot = CreateTestRoot();
        string sourceDirectory = Path.Combine(testRoot, "source");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "first.txt"), "first");
        File.WriteAllText(Path.Combine(sourceDirectory, "second.txt"), "second");

        try
        {
            var request = new FileOperationRequest
            {
                Kind = FileOperationKind.Delete,
                Sources = [sourceDirectory],
            };

            FileOperationSourceMetrics metrics = FileOperationMetrics.Calculate(
                request,
                static () => false);

            Assert.False(metrics.IsComplete);
            Assert.Equal(1, metrics.ItemCount);
            Assert.Equal(0UL, metrics.TotalBytes);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "MultiExplorer.FileOperationMetrics.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        return testRoot;
    }
}
