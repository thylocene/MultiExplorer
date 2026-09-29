namespace MultiExplorer.Tests;

public sealed class OperationWindowPlacementTests
{
    [Fact]
    public void TryCalculateLocation_CentresWindowOverApplicationOnSecondScreen()
    {
        OperationWindowPlacement placement = CreatePlacement();

        bool result = placement.TryCalculateLocation(
            windowWidth: 500, windowHeight: 300, out int x, out int y);

        Assert.True(result);
        Assert.Equal(2550, x);
        Assert.Equal(350, y);
    }

    [Fact]
    public void TryCalculateLocation_ClampsWindowToMonitorWorkArea()
    {
        OperationWindowPlacement placement = CreatePlacement();
        placement.AnchorLeft = 3500;
        placement.AnchorTop = 900;
        placement.AnchorWidth = 600;
        placement.AnchorHeight = 400;

        bool result = placement.TryCalculateLocation(
            windowWidth: 600, windowHeight: 400, out int x, out int y);

        Assert.True(result);
        Assert.Equal(3240, x);
        Assert.Equal(640, y);
    }

    [Fact]
    public void TryCalculateLocation_PinsOversizedWindowToWorkAreaOrigin()
    {
        OperationWindowPlacement placement = CreatePlacement();

        bool result = placement.TryCalculateLocation(
            windowWidth: 2200, windowHeight: 1200, out int x, out int y);

        Assert.True(result);
        Assert.Equal(1920, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void TryCalculateLocation_RejectsInvalidWorkArea()
    {
        var placement = new OperationWindowPlacement();

        bool result = placement.TryCalculateLocation(
            windowWidth: 500, windowHeight: 300, out int x, out int y);

        Assert.False(result);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PromotionPolicy_RetriesUntilWindowIsTrackedAndTopMost(
        bool wasPromoted, bool isTopMost, bool expected)
    {
        Assert.Equal(expected, OperationWindowPromotionPolicy.RequiresPromotion(
            wasPromoted, isTopMost));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PromotionPolicy_ReassertsVisibleProgressWindowWithoutCoveringPopup(
        bool visible, bool hasVisibleOwnedPopup, bool expected)
    {
        Assert.Equal(expected,
            OperationWindowPromotionPolicy.ShouldReassertProgressWindow(
                visible, hasVisibleOwnedPopup));
    }

    private static OperationWindowPlacement CreatePlacement() => new()
    {
        AnchorLeft = 2200,
        AnchorTop = 100,
        AnchorWidth = 1200,
        AnchorHeight = 800,
        WorkAreaLeft = 1920,
        WorkAreaTop = 0,
        WorkAreaWidth = 1920,
        WorkAreaHeight = 1040,
    };
}
