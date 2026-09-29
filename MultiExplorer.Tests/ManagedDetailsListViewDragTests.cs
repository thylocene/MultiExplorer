using System.Windows.Forms;

namespace MultiExplorer.Tests;

public sealed class ManagedDetailsListViewDragTests
{
    [Theory]
    [InlineData(DragDropEffects.Copy, "Copy to %1", NativeMethods.DROPIMAGETYPE.Copy)]
    [InlineData(DragDropEffects.Move, "Move to %1", NativeMethods.DROPIMAGETYPE.Move)]
    public void CreateDropDescription_UsesMatchingTextAndGlyph(
        DragDropEffects effect,
        string expectedMessage,
        NativeMethods.DROPIMAGETYPE expectedImage)
    {
        NativeMethods.DROPDESCRIPTION description =
            ManagedDetailsListView.CreateDropDescription(effect, "test");

        Assert.Equal(expectedImage, description.type);
        Assert.Equal(expectedMessage, description.szMessage);
        Assert.Equal("test", description.szInsert);
    }

    [Fact]
    public void ChooseDropEffect_ControlRequestsCopy()
    {
        DragDropEffects effect = ManagedDetailsListView.ChooseDropEffect(
            DragDropEffects.Copy | DragDropEffects.Move,
            keyState: 0x0008,
            [@"C:\source\report.txt"],
            @"C:\destination");

        Assert.Equal(DragDropEffects.Copy, effect);
    }

    [Fact]
    public void ChooseDropEffect_ShiftRequestsMoveAcrossVolumes()
    {
        DragDropEffects effect = ManagedDetailsListView.ChooseDropEffect(
            DragDropEffects.Copy | DragDropEffects.Move,
            keyState: 0x0004,
            [@"C:\source\report.txt"],
            @"D:\destination");

        Assert.Equal(DragDropEffects.Move, effect);
    }

    [Fact]
    public void ChooseDropEffect_DefaultsToMoveOnSameVolume()
    {
        DragDropEffects effect = ManagedDetailsListView.ChooseDropEffect(
            DragDropEffects.Copy | DragDropEffects.Move,
            keyState: 0,
            [@"C:\source\report.txt"],
            @"C:\destination");

        Assert.Equal(DragDropEffects.Move, effect);
    }

    [Fact]
    public void ChooseDropEffect_DefaultsToCopyAcrossVolumes()
    {
        DragDropEffects effect = ManagedDetailsListView.ChooseDropEffect(
            DragDropEffects.Copy | DragDropEffects.Move,
            keyState: 0,
            [@"C:\source\report.txt"],
            @"D:\destination");

        Assert.Equal(DragDropEffects.Copy, effect);
    }

    [Theory]
    [InlineData(@"\\server\share\source\report.txt", @"C:\destination")]
    [InlineData(@"C:\source\report.txt", @"\\server\share\destination")]
    [InlineData(@"\\server1\share\report.txt", @"\\server2\share\destination")]
    public void ChooseDropEffect_DefaultsToMoveWhenNetworkPathIsInvolved(
        string sourcePath, string destinationPath)
    {
        DragDropEffects effect = ManagedDetailsListView.ChooseDropEffect(
            DragDropEffects.Copy | DragDropEffects.Move,
            keyState: 0,
            [sourcePath],
            destinationPath);

        Assert.Equal(DragDropEffects.Move, effect);
    }

    [Fact]
    public void ChooseDropEffect_ControlCopiesFromNetworkPath()
    {
        DragDropEffects effect = ManagedDetailsListView.ChooseDropEffect(
            DragDropEffects.Copy | DragDropEffects.Move,
            keyState: 0x0008,
            [@"\\server\share\report.txt"],
            @"C:\destination");

        Assert.Equal(DragDropEffects.Copy, effect);
    }

    [Theory]
    [InlineData(DragDropEffects.Copy, "Copy to test")]
    [InlineData(DragDropEffects.Move, "Move to test")]
    public void DragFeedbackMessage_IdentifiesTheOperationAndDestination(
        DragDropEffects effect, string expectedMessage)
    {
        Assert.Equal(expectedMessage, DragDropFeedbackWindow.GetMessage(effect, "test"));
    }

    [Fact]
    public void IsRedundantMove_RejectsItemsAlreadyInTheDestination()
    {
        string destination = Path.Combine(Path.GetTempPath(), "MultiExplorer", "test");
        string[] sources =
        [
            Path.Combine(destination, "one.txt"),
            Path.Combine(destination, "two.txt"),
        ];

        Assert.True(ManagedDetailsListView.IsRedundantMove(sources, destination));
    }

    [Fact]
    public void IsRedundantMove_AllowsMovingItemsToAnotherFolder()
    {
        string sourceFolder = Path.Combine(Path.GetTempPath(), "MultiExplorer", "source");
        string destination = Path.Combine(Path.GetTempPath(), "MultiExplorer", "test");

        Assert.False(ManagedDetailsListView.IsRedundantMove(
            [Path.Combine(sourceFolder, "one.txt")], destination));
    }
}
