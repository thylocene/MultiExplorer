namespace MultiExplorer.Tests;

public class HotkeyDialogTests
{
    [Theory]
    [InlineData(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x4D, "Win + Ctrl + Alt + M")]
    [InlineData(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x4D, "Ctrl + Alt + M")]
    [InlineData(NativeMethods.MOD_WIN, 0x4D, "Win + M")]
    [InlineData(0, 0x4D, "M")]           // no modifiers — key name only
    [InlineData(0, 0, "(none)")]         // no modifiers and unrecognised vk
    public void Describe_ReturnsExpectedString(int modifiers, int vk, string expected)
    {
        Assert.Equal(expected, HotkeyDialog.Describe(modifiers, vk));
    }

    [Theory]
    [InlineData(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x4D, "Win + Ctrl + Alt + M")]
    [InlineData(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x71, "Ctrl + Alt + F2")]
    public void Describe_FunctionKeys_ReturnsExpectedString(int modifiers, int vk, string expected)
    {
        Assert.Equal(expected, HotkeyDialog.Describe(modifiers, vk));
    }
}
