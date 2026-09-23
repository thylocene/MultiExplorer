using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace MultiExplorer.Tests;

public sealed class VirtualListViewTests
{
    [Theory]
    [InlineData(511, false, false, false, false)]
    [InlineData(512, false, false, false, true)]
    [InlineData(10_000, true, false, false, false)]
    [InlineData(10_000, false, true, false, false)]
    [InlineData(10_000, false, false, true, false)]
    public void ShouldVirtualizeRows_UsesTheLargeUngroupedReadOnlyPath(
        int itemCount,
        bool grouping,
        bool checkBoxes,
        bool inlineRename,
        bool expected)
    {
        Assert.Equal(expected, ManagedDetailsListView.ShouldVirtualizeRows(
            itemCount, grouping, checkBoxes, inlineRename));
    }

    [Fact]
    public void VirtualListItemCache_BoundsItsManagedWorkingSet()
    {
        var cache = new VirtualListItemCache(capacity: 2);
        ListViewItem first = cache.GetOrCreate(0, static () => new ListViewItem("first"));

        cache.GetOrCreate(1, static () => new ListViewItem("second"));
        cache.GetOrCreate(2, static () => new ListViewItem("third"));
        ListViewItem recreated = cache.GetOrCreate(
            0, static () => new ListViewItem("first-again"));

        Assert.Equal(2, cache.Count);
        Assert.NotSame(first, recreated);
        Assert.Equal("first-again", recreated.Text);
    }

    [Fact]
    public async Task LargeDirectory_UsesVirtualRowsAndPreservesPathSelection()
    {
        const int fileCount = ManagedDetailsListView.VirtualizationThreshold + 8;
        string root = Path.Combine(
            Path.GetTempPath(), $"MultiExplorer-virtual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await Task.WhenAll(Enumerable.Range(0, fileCount).Select(index =>
                File.WriteAllTextAsync(
                    Path.Combine(root, $"file-{index:D4}.txt"), string.Empty)));

            var completed = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => RunVirtualListTest(root, fileCount, completed))
            {
                IsBackground = true,
                Name = "MultiExplorer.VirtualListViewTest",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            DirectorySnapshotCache.Invalidate(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PanelFilterList_UsesNativeVirtualMode()
    {
        await RunInStaAsync(() =>
        {
            using var panel = new PanelView();
            var field = typeof(PanelView).GetField(
                "_filterListView",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var list = Assert.IsAssignableFrom<ListView>(field?.GetValue(panel));
            Assert.True(list.VirtualMode);
        });
    }

    private static void RunVirtualListTest(
        string root,
        int fileCount,
        TaskCompletionSource<bool> completed)
    {
        using var form = new Form
        {
            ShowInTaskbar = false,
            ClientSize = new Size(800, 500),
        };
        using var list = new ManagedDetailsListView { Dock = DockStyle.Fill };
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        form.Controls.Add(list);

        DateTime timeoutUtc = DateTime.UtcNow.AddSeconds(10);
        DateTime? renameEditorSeenUtc = null;
        bool renameRequested = false;
        form.Shown += (_, _) => list.ShowDirectory(root, showHiddenItems: true);
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow >= timeoutUtc)
                    throw new TimeoutException("The virtual directory rows were not populated.");
                if (renameRequested)
                {
                    const uint LvmGetEditControl = 0x1018;
                    IntPtr editor = NativeMethods.SendMessageI(
                        list.Handle, LvmGetEditControl, IntPtr.Zero, IntPtr.Zero);
                    if (editor == IntPtr.Zero)
                    {
                        if (renameEditorSeenUtc is not null)
                        {
                            throw new InvalidOperationException(
                                "A duplicate rename attempt closed the label editor.");
                        }

                        return;
                    }

                    renameEditorSeenUtc ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - renameEditorSeenUtc < TimeSpan.FromMilliseconds(500))
                        return;

                    completed.TrySetResult(true);
                    form.Close();
                    return;
                }

                if (!list.VirtualMode || list.VirtualListSize != fileCount)
                    return;

                int lastIndex = fileCount - 1;
                ListViewItem item = list.Items[lastIndex];
                item.Selected = true;
                item.Focused = true;

                string expectedPath = Path.Combine(
                    root, $"file-{lastIndex:D4}.txt");
                Assert.Equal(expectedPath, list.SelectedPath, ignoreCase: true);

                list.SetSortDirection(ascending: false);
                Assert.True(list.VirtualMode);
                Assert.Equal(expectedPath, list.SelectedPath, ignoreCase: true);

                list.SelectAllItems();
                Assert.Equal(fileCount, list.SelectedPaths.Count);

                list.ApplyShellDisplaySettings(
                    compactMode: false,
                    showItemCheckBoxes: true,
                    showFileExtensions: true,
                    showHiddenItems: true);
                Assert.False(list.VirtualMode);
                Assert.Equal(fileCount, list.Items.Count);

                list.ApplyShellDisplaySettings(
                    compactMode: false,
                    showItemCheckBoxes: false,
                    showFileExtensions: true,
                    showHiddenItems: true);
                Assert.True(list.VirtualMode);

                list.CreateNewFolderAndBeginRename();
                renameRequested = true;
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
                form.Close();
            }
        };
        timer.Start();
        Application.Run(form);
    }

    private static Task RunInStaAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completed.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "MultiExplorer.VirtualFilterListTest",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }
}
