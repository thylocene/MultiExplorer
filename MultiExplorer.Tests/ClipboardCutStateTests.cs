namespace MultiExplorer.Tests;

public sealed class ClipboardCutStateTests
{
    [Fact]
    public void SetPaths_TracksPathsCaseInsensitively()
    {
        string path = Path.Combine(Path.GetTempPath(), "MultiExplorer", "Cut.txt");

        try
        {
            ClipboardCutState.SetPaths([path]);

            Assert.True(ClipboardCutState.Contains(path.ToUpperInvariant()));
        }
        finally
        {
            ClipboardCutState.Clear();
        }
    }

    [Fact]
    public void Clear_RemovesCutCue()
    {
        string path = Path.Combine(Path.GetTempPath(), "MultiExplorer", "Cut.txt");
        ClipboardCutState.SetPaths([path]);

        ClipboardCutState.Clear();

        Assert.False(ClipboardCutState.Contains(path));
    }
}
