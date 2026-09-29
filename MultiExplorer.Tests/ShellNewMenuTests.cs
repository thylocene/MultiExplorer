namespace MultiExplorer.Tests;

public sealed class ShellNewMenuTests
{
    [Fact]
    public void NewTextDocument_ReturnsTheCreatedPathForInlineRename()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            $"MultiExplorer-shell-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var item = new ShellNewMenu.ShellNewItem("Text Document",
                ShellNewMenu.ShellNewKind.EmptyFile, ".txt", null, null, null);

            Assert.True(ShellNewMenu.TryExecute(item, folder,
                out string? firstPath));
            Assert.Equal(Path.Combine(folder, "New Text Document.txt"), firstPath);
            Assert.True(File.Exists(firstPath));

            Assert.True(ShellNewMenu.TryExecute(item, folder,
                out string? secondPath));
            Assert.Equal(Path.Combine(folder, "New Text Document (2).txt"),
                secondPath);
            Assert.True(File.Exists(secondPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

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
