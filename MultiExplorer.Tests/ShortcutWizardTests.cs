namespace MultiExplorer.Tests;

public sealed class ShortcutWizardTests
{
    [Fact]
    public void CreateShortcut_SavesAShellLink()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MultiExplorer.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            Assert.True(File.Exists(target));

            ShortcutWizard.CreateShortcut(directory, "Notepad", target);

            Assert.True(File.Exists(Path.Combine(directory, "Notepad.lnk")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
