namespace MultiExplorer.Tests;

public class StartupManagerTests
{
    [Fact]
    public void BuildTaskDefinition_CreatesImmediateCurrentUserLogonTask()
    {
        const string executablePath =
            @"C:\Program Files\MultiExplorer\MultiExplorer.exe";
        const string userId = "S-1-5-21-1000";

        string xml = StartupManager.BuildTaskDefinition(executablePath, userId);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns =
            "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(userId,
            document.Descendants(ns + "LogonTrigger")
                .Single().Element(ns + "UserId")?.Value);
        Assert.Null(document.Descendants(ns + "Delay").SingleOrDefault());
        Assert.Equal("InteractiveToken",
            document.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege",
            document.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("4",
            document.Descendants(ns + "Priority").Single().Value);
        Assert.Equal(executablePath,
            document.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--startup",
            document.Descendants(ns + "Arguments").Single().Value);
    }

    [Theory]
    [InlineData("--startup", true)]
    [InlineData("--STARTUP", true)]
    [InlineData("--theme=dark", false)]
    public void IsStartupLaunch_RecognizesOnlyStartupArgument(string argument, bool expected)
    {
        Assert.Equal(expected, Program.IsStartupLaunch([argument]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityRelaunch_RespectsExplicitOptOut(bool optOut)
    {
        string[] args = optOut ? ["--no-identity-relaunch"] : [];
#if DEBUG
        Assert.False(Program.ShouldRelaunchWithIdentity(args));
        Assert.Equal("MultiExplorer.SingleInstance.Debug.v1",
            Program.SingleInstanceMutexName);
#else
        Assert.Equal(!optOut, Program.ShouldRelaunchWithIdentity(args));
        Assert.Equal("MultiExplorer.SingleInstance.v1",
            Program.SingleInstanceMutexName);
#endif
    }

    [Fact]
    public void BuildTaskDefinition_RejectsEmptyInputs()
    {
        Assert.Throws<ArgumentException>(() =>
            StartupManager.BuildTaskDefinition("", "S-1-5-21-1000"));
        Assert.Throws<ArgumentException>(() =>
            StartupManager.BuildTaskDefinition(@"C:\MultiExplorer.exe", ""));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002), true)]
    [InlineData(unchecked((int)0x80070003), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(0, false)]
    public void IsTaskNotFoundExitCode_RecognizesWindowsNotFoundErrors(
        int exitCode, bool expected)
    {
        Assert.Equal(expected,
            StartupManager.IsTaskNotFoundExitCode(exitCode));
    }
}
