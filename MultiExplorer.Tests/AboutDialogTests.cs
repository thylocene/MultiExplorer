namespace MultiExplorer.Tests;

public class AboutDialogTests
{
    [Fact]
    public void QuickLookAcknowledgement_ContainsDescriptionAndProjectLink()
    {
        Assert.Contains(
            "QuickLook is a separate third-party application",
            PanelView.QuickLookAcknowledgementText);
        Assert.Contains(
            PanelView.QuickLookProjectUrl,
            PanelView.QuickLookAcknowledgementText);
        Assert.Equal(
            "https://github.com/ql-win/quicklook",
            PanelView.QuickLookProjectUrl);
    }
}
