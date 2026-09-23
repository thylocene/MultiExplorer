namespace MultiExplorer.Tests;

public sealed class ExplorerTabViewSettingsTests
{
    [Fact]
    public void NewExplorerHosts_DefaultToCompactView()
    {
        Assert.True(ExplorerHost.DefaultCompactViewEnabled);
    }

    [Fact]
    public void TogglingSettings_ChangesOnlyTheTargetTab()
    {
        var target = new ExplorerTabViewSettings(
            compactViewEnabled: false,
            itemCheckBoxesEnabled: false,
            fileExtensionsVisible: true,
            hiddenItemsVisible: false);
        var other = new ExplorerTabViewSettings(
            compactViewEnabled: false,
            itemCheckBoxesEnabled: false,
            fileExtensionsVisible: true,
            hiddenItemsVisible: false);

        target.ToggleCompactView();
        target.ToggleItemCheckBoxes();
        target.ToggleFileExtensions();
        target.ToggleHiddenItems();

        Assert.True(target.CompactViewEnabled);
        Assert.True(target.ItemCheckBoxesEnabled);
        Assert.False(target.FileExtensionsVisible);
        Assert.True(target.HiddenItemsVisible);

        Assert.False(other.CompactViewEnabled);
        Assert.False(other.ItemCheckBoxesEnabled);
        Assert.True(other.FileExtensionsVisible);
        Assert.False(other.HiddenItemsVisible);
    }
}
