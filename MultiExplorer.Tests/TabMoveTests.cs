namespace MultiExplorer.Tests;

public sealed class TabMoveTests
{
    [Theory]
    [InlineData(true, 4, 0, 4)]
    [InlineData(true, 4, 2, 4)]
    [InlineData(false, 4, 0, 0)]
    [InlineData(false, 4, 2, 2)]
    [InlineData(false, 4, 8, 4)]
    public void TabInsertionIndex_AppendsForCopyAndUsesDropPositionForMove(
        bool samePane, int count, int insertion, int expected)
    {
        Assert.Equal(expected,
            PanelView.TabInsertionIndex(samePane, count, insertion));
    }

    [Theory]
    [InlineData(true, true, false, 500, 100, true)]
    [InlineData(false, true, false, 500, 100, false)]
    [InlineData(true, false, false, 500, 100, false)]
    [InlineData(true, true, true, 500, 100, false)]
    [InlineData(true, true, false, 100, 100, false)]
    public void TabDroppedOutside_OpensExplorerOnlyForEnabledCompletedOutsideDrop(
        bool enabled,
        bool dropCompleted,
        bool tabDropHandled,
        int dropX,
        int dropY,
        bool expected)
    {
        var ownerBounds = new Rectangle(0, 0, 400, 300);

        Assert.Equal(expected, TabBar.ShouldOpenExplorerAfterDrag(
            enabled, dropCompleted, tabDropHandled, ownerBounds,
            new Point(dropX, dropY)));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void PaneDrop_IsHandledOnlyOnceForCompletedDrop(
        bool dropCompleted, bool tabDropHandled, bool overPane,
        bool expected)
    {
        Assert.Equal(expected, TabBar.ShouldHandlePaneDrop(
            dropCompleted, tabDropHandled, overPane));
    }

    [Theory]
    [InlineData(true, false, DragDropEffects.Copy)]
    [InlineData(true, true, DragDropEffects.Copy)]
    [InlineData(false, false, DragDropEffects.Move)]
    [InlineData(false, true, DragDropEffects.Copy)]
    public void PaneDrop_UsesControlToCopyAcrossPanes(
        bool samePane, bool controlPressed, DragDropEffects expected)
    {
        Assert.Equal(expected, TabBar.EffectForPaneDrop(samePane, controlPressed));
    }

    [Theory]
    [InlineData(true, false, false, true, false, (int)TabDragFeedbackState.DuplicateInCurrentPane)]
    [InlineData(false, true, false, true, false, (int)TabDragFeedbackState.OpenInFileExplorer)]
    [InlineData(false, true, false, false, true, (int)TabDragFeedbackState.Hidden)]
    [InlineData(false, false, true, true, false, (int)TabDragFeedbackState.MoveToOtherPane)]
    [InlineData(false, false, true, true, true, (int)TabDragFeedbackState.CopyToOtherPane)]
    [InlineData(false, false, false, true, true, (int)TabDragFeedbackState.Hidden)]
    public void DragFeedback_DescribesAvailableActionAtDropPosition(
        bool withinSourcePane,
        bool outsideOwner,
        bool overOtherPane,
        bool enabled,
        bool controlPressed,
        int expected)
    {
        Assert.Equal((TabDragFeedbackState)expected, TabBar.GetDragFeedbackState(
            withinSourcePane, outsideOwner, overOtherPane,
            enabled, controlPressed));
    }

    [Fact]
    public void DragFeedback_DescribesDuplicateInCurrentPane()
    {
        Assert.Equal("Duplicate in current pane",
            TabDragFeedbackWindow.GetActionText(
                TabDragFeedbackState.DuplicateInCurrentPane));
    }

    [Fact]
    public void DragFeedback_DescribesMoveToOtherPane()
    {
        Assert.Equal("Move tab to other pane",
            TabDragFeedbackWindow.GetActionText(
                TabDragFeedbackState.MoveToOtherPane));
    }

    [Fact]
    public void DragFeedback_DescribesCopyToOtherPane()
    {
        Assert.Equal("Copy tab to other pane",
            TabDragFeedbackWindow.GetActionText(
                TabDragFeedbackState.CopyToOtherPane));
    }

    [Fact]
    public async Task DropWithinPane_DuplicatesAtTheEnd()
    {
        await RunOnStaAsync(() =>
        {
            string first = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string second = Environment.SystemDirectory;
            using var pane = new PanelView();
            pane.Launch([first, second]);
            ExplorerHost original = pane.GetTabHost(0);

            pane.HandleTabDrop(pane, 0, 0);

            Assert.Equal([first, second, first], pane.GetAllPaths(),
                StringComparer.OrdinalIgnoreCase);
            Assert.NotSame(original, pane.GetTabHost(2));
        });
    }

    [Fact]
    public async Task DropAcrossPanes_MovesTabToRequestedPosition()
    {
        await RunOnStaAsync(() =>
        {
            string first = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string second = Environment.SystemDirectory;
            string existing = Path.GetPathRoot(first)!;
            using var source = new PanelView();
            using var target = new PanelView();
            source.Launch([first, second]);
            target.Launch([existing]);
            ExplorerHost movedHost = source.GetTabHost(1);

            source.HandleTabDrop(target, 1, 0);

            Assert.Equal([first], source.GetAllPaths(),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal([second, existing], target.GetAllPaths(),
                StringComparer.OrdinalIgnoreCase);
            Assert.Same(movedHost, target.GetTabHost(0));
            Assert.False(movedHost.IsDisposed);
        });
    }

    [Fact]
    public async Task ControlDropAcrossPanes_CopiesTabToRequestedPosition()
    {
        await RunOnStaAsync(() =>
        {
            string copied = Environment.SystemDirectory;
            string existing = Path.GetPathRoot(copied)!;
            using var source = new PanelView();
            using var target = new PanelView();
            source.Launch([copied]);
            target.Launch([existing]);
            ExplorerHost originalHost = source.GetTabHost(0);

            source.HandleTabDrop(target, 0, 0, copy: true);

            Assert.Equal([copied], source.GetAllPaths(),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal([copied, existing], target.GetAllPaths(),
                StringComparer.OrdinalIgnoreCase);
            Assert.Same(originalHost, source.GetTabHost(0));
            Assert.NotSame(originalHost, target.GetTabHost(0));
            Assert.False(originalHost.IsDisposed);
        });
    }

    [Fact]
    public async Task DropOnlyTabAcrossPanes_OpensDefaultTabInSource()
    {
        await RunOnStaAsync(() =>
        {
            string moved = Environment.SystemDirectory;
            string existing = Path.GetPathRoot(moved)!;
            using var source = new PanelView();
            using var target = new PanelView();
            source.Launch([moved]);
            target.Launch([existing]);
            ExplorerHost movedHost = source.GetTabHost(0);
            IntPtr originalHandle = movedHost.Handle;

            source.HandleTabDrop(target, 0, 1);

            Assert.Equal([@"C:\"], source.GetAllPaths(),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal([existing, moved], target.GetAllPaths(),
                StringComparer.OrdinalIgnoreCase);
            Assert.Same(movedHost, target.GetTabHost(1));
            Assert.Equal(originalHandle, movedHost.Handle);
        });
    }

    private static async Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
    }
}
