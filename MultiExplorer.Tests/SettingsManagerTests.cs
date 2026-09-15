using System.IO;
using System.Text.Json;

namespace MultiExplorer.Tests;

public class SettingsManagerTests : IDisposable
{
    private readonly string _tempFile;

    public SettingsManagerTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new AppSettings
        {
            WindowX              = 100,
            WindowY              = 200,
            WindowWidth          = 1280,
            WindowHeight         = 800,
            WindowState          = "Maximized",
            SplitterDistance     = 640,
            SplitterRatio        = 0.5,
            SplitterPositionVersion = AppSettings.CurrentSplitterPositionVersion,
            LeftPanelPath        = @"C:\Foo",
            RightPanelPath       = @"D:\Bar",
            MinimizeToTray       = false,
            StartWithWindows     = true,
            QuickLookEnabled     = false,
            ApplicationTheme     = "Dark",
            ShowWindowModifiers  = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
            ShowWindowVk         = 0x48, // H
        };

        string json = JsonSerializer.Serialize(original);
        File.WriteAllText(_tempFile, json);
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_tempFile))!;

        Assert.Equal(original.WindowX,             loaded.WindowX);
        Assert.Equal(original.WindowY,             loaded.WindowY);
        Assert.Equal(original.WindowWidth,         loaded.WindowWidth);
        Assert.Equal(original.WindowHeight,        loaded.WindowHeight);
        Assert.Equal(original.WindowState,         loaded.WindowState);
        Assert.Equal(original.SplitterDistance,    loaded.SplitterDistance);
        Assert.Equal(original.SplitterRatio,       loaded.SplitterRatio);
        Assert.Equal(original.SplitterPositionVersion, loaded.SplitterPositionVersion);
        Assert.Equal(original.LeftPanelPath,       loaded.LeftPanelPath);
        Assert.Equal(original.RightPanelPath,      loaded.RightPanelPath);
        Assert.Equal(original.MinimizeToTray,      loaded.MinimizeToTray);
        Assert.Equal(original.StartWithWindows,    loaded.StartWithWindows);
        Assert.Equal(original.QuickLookEnabled,    loaded.QuickLookEnabled);
        Assert.Equal(original.ApplicationTheme,    loaded.ApplicationTheme);
        Assert.Equal(original.ShowWindowModifiers, loaded.ShowWindowModifiers);
        Assert.Equal(original.ShowWindowVk,        loaded.ShowWindowVk);
    }

    [Fact]
    public void WriteAtomically_ReplacesExistingSettingsWithoutLeavingTemporaryFiles()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.Settings.Tests", Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(testDirectory, "settings.json");
        Directory.CreateDirectory(testDirectory);
        File.WriteAllText(settingsPath, "{\"version\":1}");

        try
        {
            SettingsManager.WriteAtomically(settingsPath, "{\"version\":2}");

            Assert.Equal("{\"version\":2}", File.ReadAllText(settingsPath));
            Assert.Empty(Directory.EnumerateFiles(testDirectory, "settings.json.*.tmp"));
        }
        finally
        {
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }
}
