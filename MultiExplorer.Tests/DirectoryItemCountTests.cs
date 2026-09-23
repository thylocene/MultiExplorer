using System.Threading;
using System.Windows.Forms;

namespace MultiExplorer.Tests;

public sealed class DirectoryItemCountTests
{
    [Fact]
    public void CountDirectoryItems_SeparatesFilesAndFoldersWithoutRecursing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"MultiExplorer-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "first.txt"), "first");
            File.WriteAllText(Path.Combine(root, "second.txt"), "second");
            string child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            File.WriteAllText(Path.Combine(child, "nested.txt"), "nested");

            DirectoryItemCount? count = ExplorerHost.CountDirectoryItems(
                root,
                showHiddenItems: true,
                showProtectedSystemItems: true,
                CancellationToken.None);

            Assert.Equal(new DirectoryItemCount(FileCount: 2, FolderCount: 1), count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0, "0 files, 0 folders")]
    [InlineData(1, 0, "1 file, 0 folders")]
    [InlineData(0, 1, "0 files, 1 folder")]
    [InlineData(10, 3, "10 files, 3 folders")]
    public void FormatItemCountStatus_PluralizesEachItemType(
        int files, int folders, string expected)
    {
        Assert.Equal(expected, ExplorerHost.FormatItemCountStatus(files, folders));
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\folder", true)]
    [InlineData(@"\\?\UNC\server\share\folder", true)]
    [InlineData(@"C:\local\folder", false)]
    public void IsNetworkPath_RecognizesUncAndLocalPaths(string path, bool expected)
    {
        Assert.Equal(expected, ExplorerHost.IsNetworkPath(path));
    }

    [Fact]
    public void CountDirectoryItems_DoesNotAccessNetworkPaths()
    {
        DirectoryItemCount? count = ExplorerHost.CountDirectoryItems(
            @"\\unreachable.invalid\share",
            showHiddenItems: true,
            showProtectedSystemItems: true,
            CancellationToken.None);

        Assert.Null(count);
    }

    [Fact]
    public async Task ActiveMonitoring_RecountsAfterLocalFolderChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), $"MultiExplorer-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var countObserved = new TaskCompletionSource<DirectoryItemCount>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var threadExited = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            System.Windows.Forms.Timer? timeoutTimer = null;
            try
            {
                using var host = new ExplorerHost(root);
                host.CreateControl();
                bool initialCountObserved = false;
                host.ItemCountChanged += (_, count) =>
                {
                    if (!initialCountObserved)
                    {
                        initialCountObserved = true;
                        File.WriteAllText(Path.Combine(root, "created.txt"), "created");
                        return;
                    }

                    if (count.FileCount == 1 && count.FolderCount == 0)
                    {
                        countObserved.TrySetResult(count);
                        Application.ExitThread();
                    }
                };

                timeoutTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                timeoutTimer.Tick += (_, _) =>
                {
                    timeoutTimer.Stop();
                    countObserved.TrySetException(
                        new TimeoutException("The filesystem watcher did not trigger a recount."));
                    Application.ExitThread();
                };
                timeoutTimer.Start();

                host.SetItemCountMonitoringActive(true);
                Application.Run();
                host.SetItemCountMonitoringActive(false);
            }
            catch (Exception ex)
            {
                countObserved.TrySetException(ex);
            }
            finally
            {
                timeoutTimer?.Dispose();
                threadExited.TrySetResult(true);
            }
        })
        {
            IsBackground = true,
            Name = "MultiExplorer.ItemCountWatcherTest",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            DirectoryItemCount count = await countObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new DirectoryItemCount(FileCount: 1, FolderCount: 0), count);
            await threadExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
