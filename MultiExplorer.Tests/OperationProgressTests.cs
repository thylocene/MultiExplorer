namespace MultiExplorer.Tests;

public sealed class OperationProgressTests
{
    [Fact]
    public void SmoothSamples_ReducesAnIsolatedPeak()
    {
        double[] smoothed = OperationProgressMath.SmoothSamples(
            [0, 0, 100, 0, 0],
            radius: 2);

        Assert.Equal(100D / 3D, smoothed[2], precision: 8);
        Assert.True(smoothed[1] > 0);
        Assert.True(smoothed[3] > 0);
    }

    [Fact]
    public void SmoothSamples_PreservesAFlatHistory()
    {
        double[] smoothed = OperationProgressMath.SmoothSamples(
            [42, 42, 42, 42, 42],
            radius: 2);

        Assert.All(smoothed, value => Assert.Equal(42, value));
    }

    [Fact]
    public void CalculatePercentage_PrefersShellWorkEstimate()
    {
        var state = new FileOperationState
        {
            TotalItems = 10,
            CompletedItems = 1,
            TotalWork = 200,
            WorkCompleted = 100,
        };

        Assert.Equal(50, OperationProgressMath.CalculatePercentage(state));
    }

    [Fact]
    public void CalculatePercentage_FallsBackToCompletedItems()
    {
        var state = new FileOperationState
        {
            TotalItems = 4,
            CompletedItems = 3,
        };

        Assert.Equal(75, OperationProgressMath.CalculatePercentage(state));
    }

    [Theory]
    [InlineData(1_000UL, 100UL, 0UL, 0UL)]
    [InlineData(1_000UL, 100UL, 25UL, 250UL)]
    [InlineData(1_000UL, 100UL, 100UL, 1_000UL)]
    [InlineData(1_000UL, 100UL, 150UL, 1_000UL)]
    public void EstimateCompletedBytes_MapsAndClampsShellWork(
        ulong totalBytes, ulong totalWork, ulong workCompleted, ulong expected)
    {
        Assert.Equal(expected, OperationProgressMath.EstimateCompletedBytes(
            totalBytes, totalWork, workCompleted));
    }

    [Fact]
    public void ApplyMonotonicWorkProgress_DoesNotAllowPercentageToRegress()
    {
        var state = new FileOperationState
        {
            TotalBytes = 10_000,
            TotalWork = 100,
            WorkCompleted = 60,
            BytesCompleted = 6_000,
        };

        OperationProgressMath.ApplyMonotonicWorkProgress(state, 200, 80);

        Assert.Equal(60, OperationProgressMath.CalculatePercentage(state));
        Assert.Equal(100UL, state.TotalWork);
        Assert.Equal(60UL, state.WorkCompleted);
        Assert.Equal(6_000UL, state.BytesCompleted);
    }

    [Fact]
    public void ApplyMonotonicWorkProgress_AcceptsForwardProgress()
    {
        var state = new FileOperationState
        {
            TotalBytes = 10_000,
            TotalWork = 100,
            WorkCompleted = 40,
            BytesCompleted = 4_000,
        };

        OperationProgressMath.ApplyMonotonicWorkProgress(state, 200, 120);

        Assert.Equal(60, OperationProgressMath.CalculatePercentage(state));
        Assert.Equal(200UL, state.TotalWork);
        Assert.Equal(120UL, state.WorkCompleted);
        Assert.Equal(6_000UL, state.BytesCompleted);
    }

    [Fact]
    public void ApplyRequestContext_UsesCommonSourceFolderAndDestinationName()
    {
        var request = new FileOperationRequest
        {
            Kind = FileOperationKind.Copy,
            Sources =
            [
                @"C:\Projects\first.txt",
                @"C:\Projects\folder",
            ],
            Destination = @"C:\Temp",
        };
        var state = new FileOperationState();

        FileOperationPresentation.ApplyRequestContext(state, request);

        Assert.Equal("Projects", state.SourceDisplayName);
        Assert.Equal("Temp", state.DestinationDisplayName);
    }

    [Fact]
    public void GetSourceLocationName_DescribesDifferentParentsAsMultipleLocations()
    {
        string result = FileOperationPresentation.GetSourceLocationName(
            [@"C:\Projects\first.txt", @"D:\Downloads\second.txt"]);

        Assert.Equal("multiple locations", result);
    }
}
