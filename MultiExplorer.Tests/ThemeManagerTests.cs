namespace MultiExplorer.Tests;

public sealed class ThemeManagerTests
{
    [Fact]
    public void ActiveTabPalette_UsesLightLimeWithDarkText()
    {
        Assert.Equal(Color.FromArgb(224, 255, 192),
            ThemeManager.ActiveTabBackground);
        Assert.Equal(Color.FromArgb(25, 25, 25), ThemeManager.ActiveTabText);
    }

    [Fact]
    public void NativeThemeMarker_SuppressesSameThemeButAllowsThemeChange()
    {
        IntPtr window = NativeMethods.CreateWindowExW(0, "STATIC", null, 0,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);

        try
        {
            Assert.True(ThemeManager.MarkNativeThemeIfChanged(window, dark: true));
            Assert.False(ThemeManager.MarkNativeThemeIfChanged(window, dark: true));
            Assert.True(ThemeManager.MarkNativeThemeIfChanged(window, dark: false));
            Assert.False(ThemeManager.MarkNativeThemeIfChanged(window, dark: false));
        }
        finally
        {
            NativeMethods.DestroyWindow(window);
        }
    }
}
