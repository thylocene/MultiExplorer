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
            keyState: 0x0008);

        Assert.Equal(DragDropEffects.Copy, effect);
    }

    [Fact]
    public void ChooseDropEffect_DefaultsToMove()
    {
        DragDropEffects effect = ManagedDetailsListView.ChooseDropEffect(
            DragDropEffects.Copy | DragDropEffects.Move,
            keyState: 0);

        Assert.Equal(DragDropEffects.Move, effect);
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
