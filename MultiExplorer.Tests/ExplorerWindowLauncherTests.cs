namespace MultiExplorer.Tests;

public sealed class ExplorerWindowLauncherTests
{
    [Theory]
    [InlineData(-1920, 0, 1920, 1040, 1200, 800, -1560, 120, 1200, 800)]
    [InlineData(1920, 0, 1280, 984, 1600, 1000, 1920, 0, 1280, 984)]
    [InlineData(0, 40, 1600, 900, 900, 500, 350, 240, 900, 500)]
    public void CalculateWindowBounds_CentresAndFitsWindowOnTargetScreen(
        int screenX, int screenY, int screenWidth, int screenHeight,
        int windowWidth, int windowHeight,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        Rectangle destination = ExplorerWindowLauncher.CalculateWindowBounds(
            new Rectangle(screenX, screenY, screenWidth, screenHeight),
            new Size(windowWidth, windowHeight));

        Assert.Equal(new Rectangle(expectedX, expectedY, expectedWidth, expectedHeight),
            destination);
    }
}
