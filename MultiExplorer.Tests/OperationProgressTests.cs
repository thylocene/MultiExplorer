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
            ItemCountIsComplete = true,
        };

        Assert.Equal(75, OperationProgressMath.CalculatePercentage(state));
    }

    [Fact]
    public void RemainingItemCount_StaysUnknownWhileFolderDeleteIsRunning()
    {
        var state = new FileOperationState
        {
            Kind = FileOperationKind.Delete,
            Status = FileOperationStatus.Running,
            TotalItems = 1,
            CompletedItems = 1,
            ItemCountIsComplete = false,
        };

        Assert.Null(OperationProgressMath.GetRemainingItemCount(state));
        Assert.Equal(0, OperationProgressMath.CalculatePercentage(state));

        state.Status = FileOperationStatus.Completed;
        Assert.Equal(0, OperationProgressMath.GetRemainingItemCount(state));
        Assert.Equal(100, OperationProgressMath.CalculatePercentage(state));
    }

    [Fact]
    public void RemainingItemCount_UsesExactCountWhenItWasMeasured()
    {
        var state = new FileOperationState
        {
            Kind = FileOperationKind.Copy,
            Status = FileOperationStatus.Running,
            TotalItems = 5,
            CompletedItems = 2,
            ItemCountIsComplete = true,
        };

        Assert.Equal(3, OperationProgressMath.GetRemainingItemCount(state));
    }

    [Fact]
    public void RemainingPermanentDeleteItems_UsesShellWorkAsAnEstimate()
    {
        var state = new FileOperationState
        {
            Kind = FileOperationKind.DeletePermanently,
            Status = FileOperationStatus.Running,
            TotalItems = 1,
            CompletedItems = 1,
            TotalWork = 406_449,
            WorkCompleted = 16_466,
            ProcessedItems = 16_465,
        };

        Assert.Null(OperationProgressMath.GetRemainingItemCount(state));
        Assert.Equal(389_983UL,
            OperationProgressMath.EstimateRemainingPermanentDeleteItems(state));
    }

    [Fact]
    public void DeleteSpeed_UsesChildCallbacksAfterTheSelectedFolderCompletes()
    {
        var state = new FileOperationState
        {
            Kind = FileOperationKind.DeletePermanently,
            TotalItems = 1,
            CompletedItems = 1,
            ProcessedItems = 22_154,
        };
        var rate = new OperationRateAverage(TimeSpan.FromSeconds(2));

        rate.AddSample(0, OperationProgressMath.GetCompletedItemsForRate(state));
        state.ProcessedItems += 500;

        Assert.Equal(500, rate.AddSample(1_000,
            OperationProgressMath.GetCompletedItemsForRate(state)));
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
    public void RateAverage_UsesTheMostRecentTwoSeconds()
    {
        var average = new OperationRateAverage(TimeSpan.FromSeconds(2));

        Assert.Null(average.AddSample(0, 0));
        Assert.Equal(200, average.AddSample(500, 100));
        Assert.Equal(200, average.AddSample(1_000, 200));
        Assert.Equal(150, average.AddSample(2_000, 300));
        Assert.Equal(150, average.AddSample(2_500, 400));
    }

    [Fact]
    public void RateAverage_IncludesIdleTimeAfterABurst()
    {
        var average = new OperationRateAverage(TimeSpan.FromSeconds(2));

        average.AddSample(0, 0);
        average.AddSample(100, 1_000);
        average.AddSample(500, 1_000);
        average.AddSample(1_000, 1_000);
        average.AddSample(1_500, 1_000);

        Assert.Equal(500, average.AddSample(2_000, 1_000));
        Assert.Equal(0, average.AddSample(2_100, 1_000));
    }

    [Fact]
    public void RateAverage_ResetStartsANewMeasurementWindow()
    {
        var average = new OperationRateAverage(TimeSpan.FromSeconds(2));
        average.AddSample(0, 0);
        average.AddSample(1_000, 1_000);

        average.Reset();

        Assert.Null(average.AddSample(2_000, 1_000));
        Assert.Equal(500, average.AddSample(3_000, 1_500));
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

    [Theory]
    [InlineData(unchecked((int)0x80004004))]
    [InlineData(unchecked((int)0x800704C7))]
    [InlineData(unchecked((int)0x80270000))]
    public void ResultClassifier_RecognizesUserCancellation(int result)
    {
        Assert.True(FileOperationResultClassifier.IsCancellation(result));
    }

    [Fact]
    public void ResultClassifier_DoesNotTreatNetworkFailureAsCancellation()
    {
        Assert.False(FileOperationResultClassifier.IsCancellation(
            unchecked((int)0x80070040)));
    }

    [Fact]
    public void ShellFlags_HideOnlyTheNativeProgressWindowForCopies()
    {
        const uint fofSilent = 0x0004;
        const uint fofAllowUndo = 0x0040;
        const uint fofNoConfirmMkdir = 0x0200;
        const uint fofNoErrorUi = 0x0400;

        uint flags = ShellFileOperationOptions.CreateFlags(
            FileOperationKind.Copy,
            showNativeProgressDialog: false);

        Assert.Equal(fofSilent, flags & fofSilent);
        Assert.Equal(fofAllowUndo, flags & fofAllowUndo);
        Assert.Equal(fofNoConfirmMkdir, flags & fofNoConfirmMkdir);
        Assert.Equal(0u, flags & fofNoErrorUi);
    }

    [Fact]
    public void FailureMessage_IncludesNetworkDetailsAndRecoveryGuidance()
    {
        var request = new FileOperationRequest
        {
            Kind = FileOperationKind.Copy,
            Sources = [@"C:\Source\report.txt"],
            Destination = @"\\server\share\Reports",
        };
        var state = new FileOperationState
        {
            Kind = FileOperationKind.Copy,
            CurrentItem = @"C:\Source\report.txt",
            Error = "The specified network name is no longer available.",
            Result = unchecked((int)0x80070040),
        };

        string message = FileOperationFailurePresentation.CreateMessage(
            state, request);

        Assert.Contains(@"Destination: \\server\share\Reports", message);
        Assert.Contains("The specified network name is no longer available.", message);
        Assert.Contains("Windows error: 0x80070040", message);
        Assert.Contains("Check that the network location is connected", message);
    }

    [Fact]
    public void FailureMessage_OmitsNetworkGuidanceForLocalPaths()
    {
        var request = new FileOperationRequest
        {
            Kind = FileOperationKind.Move,
            Sources = [@"C:\Source\report.txt"],
            Destination = @"D:\Reports",
        };
        var state = new FileOperationState
        {
            Kind = FileOperationKind.Move,
            Error = "Access is denied.",
            Result = unchecked((int)0x80070005),
        };

        string message = FileOperationFailurePresentation.CreateMessage(
            state, request);

        Assert.DoesNotContain("Check that the network location", message);
    }

    [Fact]
    public void FailureLog_IdentifiesTheFailedItemAndWindowsError()
    {
        var state = new FileOperationState
        {
            Kind = FileOperationKind.Copy,
            CurrentItem = @"C:\Source\restricted.txt",
            Error = "Access is denied.",
            FailureStage = "IFileOperation.CopyItem",
            FailureExceptionType = "System.UnauthorizedAccessException",
            Result = unchecked((int)0x80070005),
        };

        string message = FileOperationFailurePresentation.CreateLogMessage(state);

        Assert.Contains(@"C:\Source\restricted.txt", message);
        Assert.Contains("Access is denied.", message);
        Assert.Contains("IFileOperation.CopyItem", message);
        Assert.Contains("System.UnauthorizedAccessException", message);
        Assert.Contains("0x80070005", message);
    }

    [Theory]
    [InlineData((int)FileOperationKind.Copy, "copy")]
    [InlineData((int)FileOperationKind.Move, "move")]
    [InlineData((int)FileOperationKind.Delete, "delete")]
    [InlineData((int)FileOperationKind.DeletePermanently, "permanent delete")]
    public void CleanupWarningMessage_IdentifiesOperationAndLog(
        int kind, string expectedAction)
    {
        var state = new FileOperationState { Kind = (FileOperationKind)kind };

        string message = FileOperationPresentation.CreateCleanupWarningMessage(state);

        Assert.Contains(expectedAction, message);
        Assert.Contains("app.log", message);
    }
}
