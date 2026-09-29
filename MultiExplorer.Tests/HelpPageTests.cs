using System.Net;
using System.Text.RegularExpressions;

namespace MultiExplorer.Tests;

public sealed class HelpPageTests
{
    [Fact]
    public async Task ResolveHelpPagePathAsync_PrefersInstalledHelpFile()
    {
        string testRoot = CreateTestDirectory();
        string applicationDirectory = Path.Combine(testRoot, "app");
        string recoveryDirectory = Path.Combine(testRoot, "recovery");
        Directory.CreateDirectory(applicationDirectory);
        string installedPath = Path.Combine(applicationDirectory, PanelView.HelpFileName);
        await File.WriteAllTextAsync(installedPath, "installed help");

        try
        {
            string? resolvedPath = await PanelView.ResolveHelpPagePathAsync(
                applicationDirectory, recoveryDirectory);

            Assert.Equal(installedPath, resolvedPath);
            Assert.False(Directory.Exists(recoveryDirectory));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveHelpPagePathAsync_RecoversMissingFileFromEmbeddedCopy()
    {
        string testRoot = CreateTestDirectory();
        string applicationDirectory = Path.Combine(testRoot, "missing-app");
        string recoveryDirectory = Path.Combine(testRoot, "recovery");

        try
        {
            string? resolvedPath = await PanelView.ResolveHelpPagePathAsync(
                applicationDirectory, recoveryDirectory);

            Assert.Equal(
                Path.Combine(recoveryDirectory, PanelView.HelpFileName),
                resolvedPath);
            Assert.True(File.Exists(resolvedPath));
            string contents = await File.ReadAllTextAsync(resolvedPath);
            Assert.Contains("<title>MultiExplorer Help</title>", contents);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task KeyboardShortcutsSection_ListsEverySupportedShortcut()
    {
        string helpPath = Path.Combine(
            AppContext.BaseDirectory, PanelView.HelpFileName);
        string help = await File.ReadAllTextAsync(helpPath);
        int sectionStart = help.IndexOf(
            "<section id=\"shortcuts\">", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "Keyboard shortcuts section was not found.");
        int sectionEnd = help.IndexOf(
            "</section>", sectionStart, StringComparison.Ordinal);
        Assert.True(sectionEnd > sectionStart,
            "Keyboard shortcuts section was not terminated.");

        string section = help[sectionStart..sectionEnd];
        MatchCollection rowMatches = Regex.Matches(
            section, "<tr><td>(.*?)</td><td>(.*?)</td></tr>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        (string Shortcut, string Action)[] rows = rowMatches
            .Select(static match => (
                NormalizeHelpText(match.Groups[1].Value),
                NormalizeHelpText(match.Groups[2].Value)))
            .ToArray();

        (string Shortcut, string Action)[] expected =
        [
            ("Win + Shift + E", "system tray"),
            ("F1", "Help"),
            ("F2", "Rename"),
            ("F4", "address bar"),
            ("F5", "Refresh"),
            ("Ctrl + O", "Explorer options"),
            ("Ctrl + L", "log"),
            ("Ctrl + Q", "Exit"),
            ("Ctrl + Shift + N", "new folder"),
            ("Ctrl + Shift + C", "paths"),
            ("Ctrl + N", "new tab"),
            ("Ctrl + T", "new tab"),
            ("Ctrl + X", "Cut"),
            ("Ctrl + C", "Copy"),
            ("Ctrl + V", "Paste"),
            ("Ctrl + A", "Select all"),
            ("Enter", "Open"),
            ("Del", "Delete"),
            ("Shift + Del", "Permanently delete"),
            ("Alt + Enter", "properties"),
            ("Backspace", "parent folder"),
            ("Esc", "clear the current filter"),
            ("Space", "QuickLook"),
            ("Ctrl + Space", "Close filtering"),
            ("Ctrl while dragging", "Copy"),
            ("Shift while dragging", "Move"),
        ];

        foreach ((string shortcut, string action) in expected)
        {
            Assert.Contains(rows, row =>
                row.Shortcut.Contains(shortcut, StringComparison.Ordinal)
                && row.Action.Contains(action,
                    StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(rows, row => row.Shortcut == "Alt + Down");
    }

    private static string NormalizeHelpText(string markup)
    {
        string text = Regex.Replace(markup, "<[^>]+>", " ",
            RegexOptions.CultureInvariant);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ",
            RegexOptions.CultureInvariant).Trim();
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "MultiExplorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
