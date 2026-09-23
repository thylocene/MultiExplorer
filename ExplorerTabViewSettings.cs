namespace MultiExplorer;

/// <summary>
/// Display preferences owned by one Explorer tab. These values deliberately
/// remain independent from Windows Explorer and every other MultiExplorer tab.
/// </summary>
internal sealed class ExplorerTabViewSettings(
    bool compactViewEnabled,
    bool itemCheckBoxesEnabled,
    bool fileExtensionsVisible,
    bool hiddenItemsVisible)
{
    internal bool CompactViewEnabled { get; private set; } = compactViewEnabled;
    internal bool ItemCheckBoxesEnabled { get; private set; } = itemCheckBoxesEnabled;
    internal bool FileExtensionsVisible { get; private set; } = fileExtensionsVisible;
    internal bool HiddenItemsVisible { get; private set; } = hiddenItemsVisible;

    internal void ToggleCompactView() => CompactViewEnabled = !CompactViewEnabled;

    internal void ToggleItemCheckBoxes() =>
        ItemCheckBoxesEnabled = !ItemCheckBoxesEnabled;

    internal void ToggleFileExtensions() =>
        FileExtensionsVisible = !FileExtensionsVisible;

    internal void ToggleHiddenItems() => HiddenItemsVisible = !HiddenItemsVisible;
}
