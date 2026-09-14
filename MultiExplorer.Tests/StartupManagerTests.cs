namespace MultiExplorer.Tests;

public class StartupManagerTests
{
    [Fact]
    public void BuildRunCommand_QuotesExecutablePath()
    {
        Assert.Equal("\"C:\\Program Files\\MultiExplorer\\MultiExplorer.exe\" --startup",
            StartupManager.BuildRunCommand(@"C:\Program Files\MultiExplorer\MultiExplorer.exe"));
    }

    [Theory]
    [InlineData("--startup", true)]
    [InlineData("--STARTUP", true)]
    [InlineData("--theme=dark", false)]
    public void IsStartupLaunch_RecognizesOnlyStartupArgument(string argument, bool expected)
    {
        Assert.Equal(expected, Program.IsStartupLaunch([argument]));
    }

    [Fact]
    public void BuildRunCommand_RejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => StartupManager.BuildRunCommand(""));
    }
}
