namespace MultiExplorer.Tests;

public sealed class MainFormStatusTests
{
    [Fact]
    public void Warning_RemainsVisibleForThirtySeconds()
    {
        Assert.Equal(30_000, MainForm.WarningStatusDurationMilliseconds);
    }

    [Theory]
    [InlineData(LogSeverity.Warn, LogSeverity.Warn, true)]
    [InlineData(LogSeverity.Warn, LogSeverity.Error, true)]
    [InlineData(LogSeverity.Error, LogSeverity.Warn, false)]
    [InlineData(LogSeverity.Error, LogSeverity.Error, true)]
    public void ActiveError_IsNotReplacedByAWarning(
        LogSeverity current, LogSeverity incoming, bool expected)
    {
        Assert.Equal(expected, MainForm.ShouldReplaceStatus(current, incoming));
    }

    [Fact]
    public void EmptyStatus_AcceptsAWarning()
    {
        Assert.True(MainForm.ShouldReplaceStatus(null, LogSeverity.Warn));
    }
}
