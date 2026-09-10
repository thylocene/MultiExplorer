namespace MultiExplorer.Tests;

public sealed class ShellFileDropTargetTests
{
    private static readonly string Destination =
        Path.GetPathRoot(Environment.SystemDirectory)!;

    [Fact]
    public void ChooseEffect_ControlRequestsCopy()
    {
        uint effect = ShellFileDropTarget.ChooseEffect(
            NativeMethods.DROPEFFECT_COPY | NativeMethods.DROPEFFECT_MOVE,
            NativeMethods.MK_CONTROL,
            [Path.Combine(Destination, "source.txt")],
            Destination);

        Assert.Equal(NativeMethods.DROPEFFECT_COPY, effect);
    }

    [Fact]
    public void ChooseEffect_ShiftRequestsMove()
    {
        uint effect = ShellFileDropTarget.ChooseEffect(
            NativeMethods.DROPEFFECT_COPY | NativeMethods.DROPEFFECT_MOVE,
            NativeMethods.MK_SHIFT,
            [Path.Combine(Destination, "source.txt")],
            Destination);

        Assert.Equal(NativeMethods.DROPEFFECT_MOVE, effect);
    }

    [Fact]
    public void ChooseEffect_DefaultsToMoveOnSameVolume()
    {
        uint effect = ShellFileDropTarget.ChooseEffect(
            NativeMethods.DROPEFFECT_COPY | NativeMethods.DROPEFFECT_MOVE,
            0,
            [Path.Combine(Destination, "source.txt")],
            Destination);

        Assert.Equal(NativeMethods.DROPEFFECT_MOVE, effect);
    }

    [Fact]
    public void ChooseEffect_RejectsRightButtonDrag()
    {
        uint effect = ShellFileDropTarget.ChooseEffect(
            NativeMethods.DROPEFFECT_COPY | NativeMethods.DROPEFFECT_MOVE,
            NativeMethods.MK_RBUTTON,
            [Path.Combine(Destination, "source.txt")],
            Destination);

        Assert.Equal(NativeMethods.DROPEFFECT_NONE, effect);
    }

    [Fact]
    public void Drop_ExtractsMultipleCfHDropPathsAndSchedulesOperation()
    {
        Exception? failure = null;
        var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            NativeMethods.OleInitialize(IntPtr.Zero);
            try
            {
                string[] expected =
                [
                    Path.Combine(Destination, "one.txt"),
                    Path.Combine(Destination, "folder"),
                ];
                string[]? actual = null;
                string? actualDestination = null;
                bool? actualMove = null;

                var target = new ShellFileDropTarget(
                    () => Destination,
                    (paths, destination, move) =>
                    {
                        actual = paths;
                        actualDestination = destination;
                        actualMove = move;
                        return true;
                    });
                var data = new System.Windows.Forms.DataObject();
                data.SetData(System.Windows.Forms.DataFormats.FileDrop, expected);
                var comData = (System.Runtime.InteropServices.ComTypes.IDataObject)data;

                uint effect = NativeMethods.DROPEFFECT_COPY | NativeMethods.DROPEFFECT_MOVE;
                Assert.Equal(0, target.DragEnter(comData, NativeMethods.MK_CONTROL, default, ref effect));
                Assert.Equal(NativeMethods.DROPEFFECT_COPY, effect);
                Assert.Equal(0, target.Drop(comData, NativeMethods.MK_CONTROL, default, ref effect));

                Assert.Equal(expected, actual);
                Assert.Equal(Destination, actualDestination);
                Assert.False(actualMove);
                Assert.Equal(NativeMethods.DROPEFFECT_COPY, effect);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                NativeMethods.OleUninitialize();
                finished.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)), "OLE Drop did not return promptly.");
        thread.Join();
        if (failure != null) throw failure;
    }
}
