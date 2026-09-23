namespace MultiExplorer.Tests;

public sealed class ShellNewMenuTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Microsoft Office\\WINWORD.EXE\" /n /f \"C:\\Temp\\New Document.docx\"",
        "C:\\Program Files\\Microsoft Office\\WINWORD.EXE", "/n /f \"C:\\Temp\\New Document.docx\"")]
    [InlineData("notepad.exe \"C:\\Temp\\New Text Document.txt\"", "notepad.exe",
        "\"C:\\Temp\\New Text Document.txt\"")]
    public void SplitCommandLine_SeparatesExecutableAndArguments(string commandLine,
        string expectedFileName, string expectedArguments)
    {
        (string fileName, string arguments) = ShellNewMenu.SplitCommandLine(commandLine);

        Assert.Equal(expectedFileName, fileName);
        Assert.Equal(expectedArguments, arguments);
    }

    [Fact]
    public void SplitCommandLine_RejectsAnUnmatchedQuote()
    {
        Assert.Throws<ArgumentException>(() =>
            ShellNewMenu.SplitCommandLine("\"C:\\Program Files\\Broken.exe /n"));
    }
}
