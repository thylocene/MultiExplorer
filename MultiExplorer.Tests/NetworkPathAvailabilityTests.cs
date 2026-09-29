namespace MultiExplorer.Tests;

public sealed class NetworkPathAvailabilityTests
{
    [Fact]
    public void ExplorerHost_PreservesUnavailableNetworkPath()
    {
        const string path = @"\\127.0.0.1\missing-share\folder";
        using var host = new ExplorerHost(path);

        Assert.Equal(path, host.GetCurrentPath());
        Assert.Equal(NetworkPathStatus.NotNetwork, host.NetworkStatus);
    }

    [Fact]
    public void RecreatedAvailableNetworkTab_StartsANewAvailabilityCheck()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var host = new ExplorerHost(
                    @"\\127.0.0.1\missing-share\folder")
                {
                    Size = new Size(900, 600),
                };
                _ = host.Handle;
                typeof(ExplorerHost).GetField("_networkPathStatus",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(host, NetworkPathStatus.Available);
                var observed = new List<NetworkPathStatus>();
                host.NetworkPathStatusChanged += (_, _) =>
                    observed.Add(host.NetworkStatus);

                host.LaunchExplorer();

                Assert.Contains(NetworkPathStatus.Checking, observed);
                Assert.Equal(@"\\127.0.0.1\missing-share\folder",
                    host.GetCurrentPath());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void NetworkThemeAndNavigationChanges_DoNotDiscardAvailableView()
    {
        const string path = @"\\127.0.0.1\missing-share\folder";
        using var host = new ExplorerHost(path);
        typeof(ExplorerHost).GetField("_networkPathStatus",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(host, NetworkPathStatus.Available);

        host.ToggleNavPane();
        host.RecreateForTheme();

        Assert.False(host.IsNavPaneVisible);
        Assert.Equal(NetworkPathStatus.Available, host.NetworkStatus);
        Assert.Equal(path, host.GetCurrentPath());
    }

    [Fact]
    public void RefreshingAvailableNetworkFolder_DoesNotRestartAvailabilityProbe()
    {
        const string path = @"\\127.0.0.1\missing-share\folder";
        using var host = new ExplorerHost(path);
        typeof(ExplorerHost).GetField("_networkPathStatus",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(host, NetworkPathStatus.Available);

        host.RefreshShellView();

        Assert.Equal(NetworkPathStatus.Available, host.NetworkStatus);
    }

    [Fact]
    public void OneSlowNetworkCheck_DoesNotHideAnAvailableView()
    {
        const string path = @"\\127.0.0.1\missing-share\folder";
        using var host = new ExplorerHost(path);
        typeof(ExplorerHost).GetField("_networkPathStatus",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(host, NetworkPathStatus.Available);

        host.ApplyNetworkAvailabilityObservation(path, 0, available: false);
        Assert.Equal(NetworkPathStatus.Available, host.NetworkStatus);
        host.ApplyNetworkAvailabilityObservation(path, 0, available: true);
        host.ApplyNetworkAvailabilityObservation(path, 0, available: false);
        Assert.Equal(NetworkPathStatus.Available, host.NetworkStatus);
        host.ApplyNetworkAvailabilityObservation(path, 0, available: false);
        Assert.Equal(NetworkPathStatus.Unavailable, host.NetworkStatus);
    }

    [Fact]
    public async Task NavigationPaneRebuild_HidesTreeAndKeepsShellItems()
    {
        var finished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var threadExited = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-30000, -30000),
                    Size = new Size(900, 600),
                };
                using var host = new ExplorerHost(AppContext.BaseDirectory)
                {
                    Dock = DockStyle.Fill,
                };
                form.Controls.Add(host);
                using var timeout = new System.Windows.Forms.Timer { Interval = 12000 };
                bool rebuilding = false;
                int before = 0;
                timeout.Tick += (_, _) =>
                {
                    var fields = System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic;
                    bool browserExists = typeof(ExplorerHost)
                        .GetField("_browser", fields)!.GetValue(host) is not null;
                    bool browserThreadExists = typeof(ExplorerHost)
                        .GetField("_browserThread", fields)!.GetValue(host) is not null;
                    var findDescendant = typeof(ExplorerHost)
                        .GetMethod("FindDescendant", System.Reflection.BindingFlags.Static
                            | System.Reflection.BindingFlags.NonPublic)!;
                    IntPtr view = (IntPtr)findDescendant.Invoke(null,
                        [host.Handle, "SHELLDLL_DefView"])!;
                    IntPtr tree = (IntPtr)findDescendant.Invoke(null,
                        [host.Handle, "SysTreeView32"])!;
                    finished.TrySetException(new TimeoutException(
                        $"The Shell view did not complete the navigation pane toggle "
                        + $"(rebuilding {rebuilding}, browser {browserExists}, "
                        + $"thread {browserThreadExists}, view {view != IntPtr.Zero}, "
                        + $"tree {tree != IntPtr.Zero}, path {host.GetCurrentPath()})."));
                    form.Close();
                };

                host.InitialNavigationCompleted += async (_, _) =>
                {
                    try
                    {
                        var fields = System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic;
                        object? browserObject = typeof(ExplorerHost)
                            .GetField("_browser", fields)!.GetValue(host);
                        Assert.NotNull(browserObject);
                        var browser = (NativeMethods.IExplorerBrowser)browserObject;
                        var browserThread = Assert.IsType<BrowserThread>(
                            typeof(ExplorerHost).GetField("_browserThread", fields)!.GetValue(host));
                        await browserThread.InvokeAsync(() =>
                            before = GetShellItemCount(browser));
                        Assert.True(before > 0);
                        host.NavigationChanged += async (_, _) =>
                        {
                            try
                            {
                                object? rebuiltBrowserObject = typeof(ExplorerHost)
                                    .GetField("_browser", fields)!.GetValue(host);
                                Assert.NotNull(rebuiltBrowserObject);
                                var rebuiltBrowser = (NativeMethods.IExplorerBrowser)
                                    rebuiltBrowserObject;
                                var rebuiltThread = Assert.IsType<BrowserThread>(
                                    typeof(ExplorerHost).GetField("_browserThread", fields)!
                                        .GetValue(host));
                                int after = 0;
                                await rebuiltThread.InvokeAsync(() =>
                                    after = GetShellItemCount(rebuiltBrowser));
                                IntPtr tree = (IntPtr)typeof(ExplorerHost)
                                    .GetMethod("FindDescendant",
                                        System.Reflection.BindingFlags.Static
                                        | System.Reflection.BindingFlags.NonPublic)!
                                    .Invoke(null, [host.Handle, "SysTreeView32"])!;
                                Assert.True(tree == IntPtr.Zero
                                    || !NativeMethods.IsWindowVisible(tree));
                                Assert.Equal(before, after);
                                Assert.False(host.IsNavPaneVisible);
                                finished.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                finished.TrySetException(ex);
                            }
                            finally
                            {
                                form.Close();
                            }
                        };
                        rebuilding = true;
                        host.ToggleNavPane();
                    }
                    catch (Exception ex)
                    {
                        finished.TrySetException(ex);
                        form.Close();
                    }
                };

                form.Shown += (_, _) => host.LaunchExplorer();
                timeout.Start();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                finished.TrySetException(ex);
            }
            finally
            {
                threadExited.TrySetResult(true);
            }
        })
        {
            IsBackground = true,
            Name = "MultiExplorer.NetworkNavigationPaneTest",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await threadExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int GetShellItemCount(NativeMethods.IExplorerBrowser browser)
    {
        var viewId = typeof(NativeMethods.IFolderView2).GUID;
        Assert.True(browser.GetCurrentView(ref viewId, out IntPtr viewPointer) >= 0);
        Assert.NotEqual(IntPtr.Zero, viewPointer);
        try
        {
            var view = (NativeMethods.IFolderView2)
                System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(viewPointer);
            Assert.True(view.ItemCount(0, out int count) >= 0);
            return count;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(viewPointer);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAsync_ReturnsPromptDirectoryResult(bool exists)
    {
        bool result = await NetworkPathAvailability.CheckAsync(
            @"\\server\share", directoryExists: _ => exists);

        Assert.Equal(exists, result);
    }

    [Fact]
    public async Task CheckAsync_StopsWaitingWhenNetworkProviderStalls()
    {
        using var release = new ManualResetEventSlim();
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            bool available = await NetworkPathAvailability.CheckAsync(
                @"\\server\share", directoryExists: _ =>
                {
                    release.Wait();
                    return true;
                });

            Assert.False(available);
            Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task UnavailableStartupTab_KeepsPathAndRetriesOnlyOnRefresh()
    {
        const string path = @"\\127.0.0.1\missing-share\folder";
        var finished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var threadExited = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-30000, -30000),
                    Size = new Size(900, 600),
                };
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);

                using var timeout = new System.Windows.Forms.Timer
                    { Interval = 10000 };
                timeout.Tick += (_, _) =>
                {
                    finished.TrySetException(new TimeoutException(
                        "The unavailable network tab did not complete startup and F5 retry."));
                    form.Close();
                };

                panel.InitialBrowserReady += (_, _) =>
                {
                    try
                    {
                        ExplorerHost host = panel.GetTabHost(0);
                        Assert.Equal(path, panel.CurrentPath());
                        Assert.Equal(NetworkPathStatus.Unavailable, host.NetworkStatus);
                        Panel overlay = Assert.IsType<Panel>(
                            panel.Controls.Find("networkUnavailableOverlay", true).Single());
                        Label message = Assert.IsType<Label>(
                            panel.Controls.Find("networkUnavailableMessage", true).Single());
                        Assert.True(overlay.Visible);
                        Assert.Contains("Press F5", message.Text);

                        panel.PollPath();
                        Assert.Equal(NetworkPathStatus.Unavailable, host.NetworkStatus);
                        host.NavigateTo(path);
                        Assert.Equal(NetworkPathStatus.Unavailable, host.NetworkStatus);
                        host.NetworkPathStatusChanged += (_, _) =>
                        {
                            if (host.NetworkStatus != NetworkPathStatus.Unavailable) return;
                            try
                            {
                                Assert.Contains("Press F5", message.Text);
                                finished.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                finished.TrySetException(ex);
                            }
                            form.Close();
                        };
                        panel.ExecuteCommand(CommandBar.Cmd.Refresh);
                        Assert.Equal(NetworkPathStatus.Checking, host.NetworkStatus);
                        Assert.True(overlay.Visible);
                        Assert.Contains("Retrying network location", message.Text);
                        Assert.Contains(path, message.Text);
                    }
                    catch (Exception ex)
                    {
                        finished.TrySetException(ex);
                        form.Close();
                    }
                };

                form.Shown += (_, _) => panel.Launch([path]);
                timeout.Start();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                finished.TrySetException(ex);
            }
            finally
            {
                threadExited.TrySetResult(true);
            }
        })
        {
            IsBackground = true,
            Name = "MultiExplorer.UnavailableNetworkStartupTest",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await threadExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
