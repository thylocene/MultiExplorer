namespace MultiExplorer.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultHotkey_IsWinCtrlAltM()
    {
        var settings = new AppSettings();
        Assert.Equal(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                     settings.ShowWindowModifiers);
        Assert.Equal(NativeMethods.VK_M, settings.ShowWindowVk);
    }

    [Fact]
    public void DefaultMinimizeToTray_IsTrue()
    {
        var settings = new AppSettings();
        Assert.True(settings.MinimizeToTray);
    }

    [Fact]
    public void DefaultQuickLookEnabled_IsFalse()
    {
        var settings = new AppSettings();
        Assert.False(settings.QuickLookEnabled);
    }

    [Fact]
    public void DefaultStartWithWindows_IsFalse()
    {
        Assert.False(new AppSettings().StartWithWindows);
    }

    [Fact]
    public void DefaultApplicationTheme_IsLight()
    {
        Assert.Equal("Light", new AppSettings().ApplicationTheme);
    }

    [Fact]
    public void DefaultPathHistories_AreEmptyAndIndependent()
    {
        var settings = new AppSettings();
        Assert.Empty(settings.LeftPathHistory);
        Assert.Empty(settings.RightPathHistory);
        Assert.NotSame(settings.LeftPathHistory, settings.RightPathHistory);
    }
}
