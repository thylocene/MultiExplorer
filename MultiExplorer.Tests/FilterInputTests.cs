namespace MultiExplorer.Tests;

public sealed class FilterInputTests
{
    [Fact]
    public async Task SortFoldersWithFilesByName_AppliesToDetailsAndFilter()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            $"MultiExplorer-name-sort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "mix-b-folder"));
        await File.WriteAllTextAsync(Path.Combine(folder, "mix-a.txt"), "test");

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));

                var details = GetField<ManagedDetailsListView>(panel,
                    "_managedDetailsListView");
                Assert.True(details.Visible);
                Assert.Equal(["mix-b-folder", "mix-a"],
                    details.Items.Cast<ListViewItem>()
                        .Select(static item => item.Text));

                panel.SetSortFoldersWithFilesByName(true);
                Assert.Equal(["mix-a", "mix-b-folder"],
                    details.Items.Cast<ListViewItem>()
                        .Select(static item => item.Text));

                TextBox filterTextBox = GetField<TextBox>(panel,
                    "_filterTextBox");
                ListView filterList = GetField<ListView>(panel,
                    "_filterListView");
                filterTextBox.Text = "mix";
                await WaitUntilAsync(() => filterList.VirtualListSize == 2);
                Assert.Equal(["mix-a.txt", "mix-b-folder"],
                    GetFilterNames(filterList));
                filterList.Items[0].Selected = true;

                panel.SetSortFoldersWithFilesByName(false);
                Assert.Equal(["mix-b-folder", "mix-a.txt"],
                    GetFilterNames(filterList));
                Assert.Equal(Path.Combine(folder, "mix-a.txt"),
                    GetSelectedFilterPath(filterList));
            });
        }
        finally
        {
            DirectorySnapshotCache.Invalidate(folder);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkTypeHeaderClick_SkipsSortOnlyForFolderOnlyView(
        bool includeFile)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            $"MultiExplorer-network-type-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "z-folder"));
        Directory.CreateDirectory(Path.Combine(folder, "a-folder"));
        if (includeFile)
            await File.WriteAllTextAsync(Path.Combine(folder, "report.txt"), "test");

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));

                ExplorerHost host = GetField<List<ExplorerHost>>(panel, "_hosts")[0];
                Assert.NotNull(await host.GetCurrentShellSortColumnAsync());
                BrowserThread thread = GetField<BrowserThread>(host, "_browserThread");
                await WaitUntilAsync(() => thread.Invoke(() =>
                {
                    var browser = GetField<NativeMethods.IExplorerBrowser>(
                        host, "_browser");
                    Guid id = typeof(NativeMethods.IFolderView2).GUID;
                    if (browser.GetCurrentView(ref id, out IntPtr pointer) < 0
                        || pointer == IntPtr.Zero)
                        return false;
                    try
                    {
                        var view = (NativeMethods.IFolderView2)System.Runtime
                            .InteropServices.Marshal.GetObjectForIUnknown(pointer);
                        return view.ItemCount(0, out int count) >= 0
                            && count == (includeFile ? 3 : 2);
                    }
                    finally { System.Runtime.InteropServices.Marshal.Release(pointer); }
                }));
                Assert.Equal(!includeFile,
                    thread.Invoke(host.CurrentShellViewContainsOnlyFolders));

                IntPtr fileView = IntPtr.Zero;
                NativeMethods.EnumChildWindows(host.Handle, (window, _) =>
                {
                    var name = new System.Text.StringBuilder(64);
                    var parent = new System.Text.StringBuilder(64);
                    NativeMethods.GetClassName(window, name, name.Capacity);
                    NativeMethods.GetClassName(NativeMethods.GetParent(window),
                        parent, parent.Capacity);
                    if (name.ToString() == "DirectUIHWND"
                        && parent.ToString() == "SHELLDLL_DefView")
                        fileView = window;
                    return true;
                }, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, fileView);
                Assert.True(NativeMethods.GetWindowRect(fileView, out var bounds));

                NativeMethods.POINT typePoint = default;
                bool foundType = false;
                for (int x = 0; x < bounds.Right - bounds.Left; x += 8)
                {
                    var candidate = new NativeMethods.POINT(
                        bounds.Left + x, bounds.Top + 10);
                    if (!ExplorerHost.IsTypeColumnHeaderAt(fileView, candidate))
                        continue;
                    typePoint = candidate;
                    foundType = true;
                    break;
                }
                Assert.True(foundType, "The native Type header was not found.");
                Assert.True(NativeMethods.ScreenToClient(fileView, ref typePoint));
                int packedPoint = (typePoint.y << 16) | (typePoint.x & 0xFFFF);

                bool suppressed = thread.Invoke(() =>
                {
                    var flags = System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic;
                    var pathField = typeof(ExplorerHost).GetField("_currentPath", flags)!;
                    var statusField = typeof(ExplorerHost).GetField(
                        "_networkPathStatus", flags)!;
                    object? previousPath = pathField.GetValue(host);
                    object? previousStatus = statusField.GetValue(host);
                    try
                    {
                        pathField.SetValue(host, @"\\test\share");
                        statusField.SetValue(host, NetworkPathStatus.Available);
                        Message click = Message.Create(fileView, 0x0201,
                            IntPtr.Zero, new IntPtr(packedPoint));
                        bool handled = host.PreFilterMessage(ref click);
                        if (handled)
                        {
                            Message release = Message.Create(fileView, 0x0202,
                                IntPtr.Zero, new IntPtr(packedPoint));
                            Assert.True(host.PreFilterMessage(ref release));
                        }
                        return handled;
                    }
                    finally
                    {
                        pathField.SetValue(host, previousPath);
                        statusField.SetValue(host, previousStatus);
                    }
                });
                Assert.Equal(!includeFile, suppressed);
            });
        }
        finally
        {
            DirectorySnapshotCache.Invalidate(folder);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(Keys.Space, Keys.Control, true)]
    [InlineData(Keys.Space, Keys.None, false)]
    [InlineData(Keys.Space, Keys.Control | Keys.Shift, false)]
    [InlineData(Keys.Enter, Keys.Control, false)]
    public void FilterQuickLookShortcut_RequiresCtrlSpaceOnly(
        Keys keyCode, Keys modifiers, bool expected)
    {
        Assert.Equal(expected,
            PanelView.IsFilterQuickLookShortcut(keyCode, modifiers));
    }

    [Fact]
    public void CtrlSpace_ClosesTheActiveFilterAndSuppressesTheSpaceCharacter()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView();
                var managedList = GetField<ManagedDetailsListView>(panel,
                    "_managedDetailsListView");
                var filterTextBox = GetField<TextBox>(panel, "_filterTextBox");
                var filterListView = GetField<ListView>(panel, "_filterListView");

                typeof(ManagedDetailsListView)
                    .GetMethod("OnKeyPress",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(managedList, [new KeyPressEventArgs('r')]);

                var keyDown = new KeyEventArgs(Keys.Control | Keys.Space);
                typeof(PanelView)
                    .GetMethod("OnFilterTextBoxKeyDown",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(panel, [filterTextBox, keyDown]);

                Assert.Empty(filterTextBox.Text);
                Assert.False(filterListView.Visible);
                Assert.True(keyDown.Handled);
                Assert.True(keyDown.SuppressKeyPress);
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
    public void ManagedDetailsTyping_UpdatesThePanelFilterText()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView();
                var managedList = GetField<ManagedDetailsListView>(panel,
                    "_managedDetailsListView");
                var filterTextBox = GetField<TextBox>(panel, "_filterTextBox");
                var keyPress = new KeyPressEventArgs('r');

                typeof(ManagedDetailsListView)
                    .GetMethod("OnKeyPress",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(managedList, [keyPress]);

                Assert.Equal("r", filterTextBox.Text);
                Assert.True(keyPress.Handled);
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
    public void ClickingTheManagedFileList_ActivatesItsPaneAgain()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView();
                ManagedDetailsListView list =
                    GetField<ManagedDetailsListView>(panel,
                        "_managedDetailsListView");
                int activations = 0;
                panel.FocusReceived += (_, _) => activations++;
                typeof(Control).GetMethod("OnMouseDown",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(list, [new MouseEventArgs(
                        MouseButtons.Left, 1, 10, 10, 0)]);
                Assert.Equal(1, activations);
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
    public async Task FilteredResults_ShowIconsAndF2RenamesFilesAndFolders()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.FilterRename.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string originalFile = Path.Combine(folder, "sample.txt");
        string renamedFile = Path.Combine(folder, "sample-new.txt");
        string originalDirectory = Path.Combine(folder, "sample-folder");
        string renamedDirectory = Path.Combine(folder, "sample-new-folder");
        await File.WriteAllTextAsync(originalFile, "test");
        Directory.CreateDirectory(originalDirectory);

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
                TextBox filterTextBox = GetField<TextBox>(panel,
                    "_filterTextBox");
                ListView filterList = GetField<ListView>(panel,
                    "_filterListView");
                filterTextBox.Text = "sample";

                await WaitUntilAsync(() => filterList.VirtualListSize == 2);
                Assert.NotNull(filterList.SmallImageList);
                for (int index = 0; index < filterList.VirtualListSize; index++)
                    Assert.True(filterList.Items[index].ImageIndex >= 0);
                Assert.True(filterList.SmallImageList.Images.Count >= 2);

                int fileIndex = FindFilterItem(filterList, "sample.txt");
                filterList.Items[fileIndex].Selected = true;
                filterList.Items[fileIndex].Focused = true;
                var fileRenameKey = new KeyEventArgs(Keys.F2);
                InvokeFilterKey(panel, "OnFilterListKeyDown", filterList,
                    fileRenameKey);
                Assert.True(fileRenameKey.SuppressKeyPress);
                TextBox editor = GetField<TextBox>(panel,
                    "_filterRenameEditor");
                Assert.Equal("sample.txt", editor.Text);
                editor.Text = "sample-new.txt";
                InvokeFilterKey(panel, "OnFilterRenameEditorKeyDown", editor,
                    new KeyEventArgs(Keys.Enter));
                await WaitUntilAsync(() => File.Exists(renamedFile)
                    && !File.Exists(originalFile)
                    && FindFilterItemOrNegative(filterList,
                        "sample-new.txt") >= 0);

                filterList.SelectedIndices.Clear();
                int directoryIndex = FindFilterItem(filterList,
                    "sample-folder");
                filterList.Items[directoryIndex].Selected = true;
                filterList.Items[directoryIndex].Focused = true;
                var folderRenameKey = new KeyEventArgs(Keys.F2);
                InvokeFilterKey(panel, "OnFilterTextBoxKeyDown", filterTextBox,
                    folderRenameKey);
                Assert.True(folderRenameKey.SuppressKeyPress);
                editor = GetField<TextBox>(panel, "_filterRenameEditor");
                Assert.Equal("sample-folder", editor.Text);
                editor.Text = "sample-new-folder";
                InvokeFilterKey(panel, "OnFilterRenameEditorKeyDown", editor,
                    new KeyEventArgs(Keys.Enter));
                await WaitUntilAsync(() => Directory.Exists(renamedDirectory)
                    && !Directory.Exists(originalDirectory)
                    && FindFilterItemOrNegative(filterList,
                        "sample-new-folder") >= 0);
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task OppositePaneCommands_UseAllSelectedFilteredFilesAndFolders()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OppositePane.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "transfer-file.txt");
        string child = Path.Combine(folder, "transfer-folder");
        await File.WriteAllTextAsync(file, "test");
        Directory.CreateDirectory(child);

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));

                var requests = new List<OppositePaneTransferRequest>();
                panel.TransferToOtherPaneRequested += (_, request) =>
                    requests.Add(request);
                TextBox filterTextBox = GetField<TextBox>(panel,
                    "_filterTextBox");
                ListView filterList = GetField<ListView>(panel,
                    "_filterListView");
                filterTextBox.Text = "transfer";
                await WaitUntilAsync(() => filterList.VirtualListSize == 2);
                filterList.Items[0].Selected = true;
                filterList.Items[1].Selected = true;

                panel.ExecuteCommand(CommandBar.Cmd.CopyToOtherPane);
                panel.ExecuteCommand(CommandBar.Cmd.MoveToOtherPane);

                Assert.Equal([FileOperationKind.Copy, FileOperationKind.Move],
                    requests.Select(static request => request.Kind));
                foreach (OppositePaneTransferRequest request in requests)
                    Assert.Equal([file, child], request.Paths
                        .OrderBy(static path => path,
                            StringComparer.OrdinalIgnoreCase));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task EscapeWithSelectedFilteredResult_ClearsListSafely()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.FilterEscape.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        for (int index = 0; index < 3; index++)
            await File.WriteAllTextAsync(
                Path.Combine(folder, $"match-{index}.txt"), "test");

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
                TextBox filterTextBox = GetField<TextBox>(panel,
                    "_filterTextBox");
                ListView filterList = GetField<ListView>(panel,
                    "_filterListView");

                for (int cycle = 0; cycle < 12; cycle++)
                {
                    filterTextBox.Text = "match";
                    await WaitUntilAsync(() => filterList.VirtualListSize == 3);
                    filterList.Items[2].Selected = true;
                    filterList.Items[2].Focused = true;
                    InvokeFilterKey(panel,
                        cycle % 2 == 0
                            ? "OnFilterListKeyDown"
                            : "OnFilterTextBoxKeyDown",
                        cycle % 2 == 0 ? filterList : filterTextBox,
                        new KeyEventArgs(Keys.Escape));
                    Assert.False(filterList.Visible);
                    Assert.Equal(0, filterList.VirtualListSize);
                    await Task.Delay(20);
                }

                filterTextBox.Text = "match";
                await WaitUntilAsync(() => filterList.VirtualListSize == 3);
                filterList.Items[2].Selected = true;
                filterList.Items[2].Focused = true;
                filterTextBox.Text = "match-0";
                await WaitUntilAsync(() => filterList.VirtualListSize == 1);
                Assert.Equal("match-0.txt", filterList.Items[0].Text);
                InvokeFilterKey(panel, "OnFilterTextBoxKeyDown", filterTextBox,
                    new KeyEventArgs(Keys.Escape));
                Assert.Equal(0, filterList.VirtualListSize);
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task FilteredResults_SortByHeadersAndKeepSelectedPath()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.FilterSort.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "find-folder-a"));
        Directory.CreateDirectory(Path.Combine(folder, "find-folder-z"));
        string fileA = Path.Combine(folder, "find-a.txt");
        string fileB = Path.Combine(folder, "find-b.log");
        string fileC = Path.Combine(folder, "find-c.txt");
        await File.WriteAllBytesAsync(fileA, new byte[20]);
        await File.WriteAllBytesAsync(fileB, new byte[3]);
        await File.WriteAllBytesAsync(fileC, new byte[100]);
        File.SetLastWriteTime(fileA, new DateTime(2022, 1, 1));
        File.SetLastWriteTime(fileB, new DateTime(2020, 1, 1));
        File.SetLastWriteTime(fileC, new DateTime(2021, 1, 1));

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
                TextBox filterTextBox = GetField<TextBox>(panel,
                    "_filterTextBox");
                ListView filterList = GetField<ListView>(panel,
                    "_filterListView");
                filterTextBox.Text = "find";
                await WaitUntilAsync(() => filterList.VirtualListSize == 5);

                Assert.Equal(["find-folder-a", "find-folder-z", "find-a.txt",
                    "find-b.log", "find-c.txt"], GetFilterNames(filterList));
                filterList.Items[FindFilterItem(filterList, "find-a.txt")].Selected = true;

                ClickFilterColumn(panel, filterList, "Size");
                Assert.Equal(["find-b.log", "find-a.txt", "find-c.txt"],
                    GetFilterNames(filterList).Skip(2));
                Assert.Equal(fileA, GetSelectedFilterPath(filterList));

                ClickFilterColumn(panel, filterList, "Size");
                Assert.Equal(["find-c.txt", "find-a.txt", "find-b.log"],
                    GetFilterNames(filterList).Skip(2));
                Assert.Equal(fileA, GetSelectedFilterPath(filterList));

                ClickFilterColumn(panel, filterList, "Type");
                Assert.Equal(["find-b.log", "find-c.txt", "find-a.txt"],
                    GetFilterNames(filterList).Skip(2));
                ClickFilterColumn(panel, filterList, "Date modified");
                Assert.Equal(["find-b.log", "find-c.txt", "find-a.txt"],
                    GetFilterNames(filterList).Skip(2));
                ClickFilterColumn(panel, filterList, "Date modified");
                Assert.Equal(["find-a.txt", "find-c.txt", "find-b.log"],
                    GetFilterNames(filterList).Skip(2));

                ClickFilterColumn(panel, filterList, "Name");
                Assert.Equal(["find-a.txt", "find-b.log", "find-c.txt"],
                    GetFilterNames(filterList).Skip(2));
                ClickFilterColumn(panel, filterList, "Name");
                Assert.Equal(["find-folder-z", "find-folder-a", "find-c.txt",
                    "find-b.log", "find-a.txt"], GetFilterNames(filterList));

                ClickFilterColumn(panel, filterList, "Type");
                Assert.Equal(["find-folder-z", "find-folder-a"],
                    GetFilterNames(filterList).Take(2));
                ClickFilterColumn(panel, filterList, "Type");
                Assert.Equal(["find-folder-z", "find-folder-a"],
                    GetFilterNames(filterList).Take(2));
                ClickFilterColumn(panel, filterList, "Name");
                ClickFilterColumn(panel, filterList, "Name");

                filterTextBox.Text = "find-c";
                await WaitUntilAsync(() => filterList.VirtualListSize == 1);
                filterTextBox.Text = "find";
                await WaitUntilAsync(() => filterList.VirtualListSize == 5);
                Assert.Equal(["find-folder-z", "find-folder-a", "find-c.txt",
                    "find-b.log", "find-a.txt"], GetFilterNames(filterList));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task FilteredResults_ShowAddedDetailsColumnsAndSortByThem()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.FilterColumns.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string older = Path.Combine(folder, "extra-older.txt");
        string newer = Path.Combine(folder, "extra-newer.log");
        await File.WriteAllTextAsync(older, "old");
        await File.WriteAllTextAsync(newer, "new");
        File.SetCreationTime(older, new DateTime(2020, 1, 1));
        File.SetCreationTime(newer, new DateTime(2022, 1, 1));

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));

                TextBox filterTextBox = GetField<TextBox>(panel,
                    "_filterTextBox");
                ListView filterList = GetField<ListView>(panel,
                    "_filterListView");
                var managedList = GetField<ManagedDetailsListView>(panel,
                    "_managedDetailsListView");
                filterTextBox.Text = "extra";
                await WaitUntilAsync(() => filterList.VirtualListSize == 2);
                Assert.Equal(managedList.Columns.Cast<ColumnHeader>()
                        .Select(static column => column.Text),
                    filterList.Columns.Cast<ColumnHeader>()
                        .Select(static column => column.Text));

                typeof(ManagedDetailsListView)
                    .GetMethod("ApplyColumnSelection",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(managedList,
                        [new HashSet<string>(["Name", "DateModified", "Type",
                            "Size", "DateCreated", "Attributes", "Extension",
                            "FullPath"])]);
                await WaitUntilAsync(() => filterList.Columns.Count == 8
                    && filterList.VirtualListSize == 2);

                Assert.Equal(["Name", "Date modified", "Type", "Size",
                    "Date created", "Attributes", "Extension", "Full path"],
                    filterList.Columns.Cast<ColumnHeader>()
                        .Select(static column => column.Text));
                Assert.Equal(managedList.Columns.Cast<ColumnHeader>()
                        .Select(static column => column.Text),
                    filterList.Columns.Cast<ColumnHeader>()
                        .Select(static column => column.Text));
                for (int index = 0; index < filterList.Columns.Count; index++)
                    Assert.Equal(managedList.Columns[index].Width,
                        filterList.Columns[index].Width);
                ListViewItem row = filterList.Items[FindFilterItem(filterList,
                    "extra-older.txt")];
                Assert.Equal(File.GetCreationTime(older).ToString("g"),
                    row.SubItems[FindFilterColumn(filterList, "Date created")].Text);
                Assert.Equal(File.GetAttributes(older).ToString(),
                    row.SubItems[FindFilterColumn(filterList, "Attributes")].Text);
                Assert.Equal(".txt",
                    row.SubItems[FindFilterColumn(filterList, "Extension")].Text);
                Assert.Equal(older,
                    row.SubItems[FindFilterColumn(filterList, "Full path")].Text);

                ClickFilterColumn(panel, filterList, "Date created");
                Assert.Equal(["extra-older.txt", "extra-newer.log"],
                    GetFilterNames(filterList));
                ClickFilterColumn(panel, filterList, "Date created");
                Assert.Equal(["extra-newer.log", "extra-older.txt"],
                    GetFilterNames(filterList));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DetailsColumnPicker_DateCreatedAppearsInFilteredResults()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.FilterPicker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "picker-match.txt");
        await File.WriteAllTextAsync(file, "test");
        File.SetCreationTime(file, new DateTime(2021, 2, 3));

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));

                var managedList = GetField<ManagedDetailsListView>(panel,
                    "_managedDetailsListView");
                await WaitUntilAsync(() => managedList.IsHandleCreated
                    && managedList.Visible);
                managedList.OpenColumnChooser();
                ToolStripDropDown? popup = null;
                await WaitUntilAsync(() => (popup = typeof(ManagedDetailsListView)
                    .GetField("_columnChooserPopup",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(managedList) as ToolStripDropDown) is not null);
                CheckedListBox choices = GetField<CheckedListBox>(popup!,
                    "_columnList");
                int dateCreatedIndex = choices.Items
                    .Cast<ManagedDetailsListView.ColumnDefinition>()
                    .Select((column, index) => (column, index))
                    .First(entry => entry.column.Id == "DateCreated").index;
                var shellColumn = choices.Items
                    .Cast<ManagedDetailsListView.ColumnDefinition>()
                    .Select((column, index) => (column, index))
                    .First(entry => entry.column.Text == "Authors"
                        && entry.column.PropertyKey is not null);
                choices.SetItemChecked(dateCreatedIndex, true);
                choices.SetItemChecked(shellColumn.index, true);
                await WaitUntilAsync(() => managedList.Columns
                    .Cast<ColumnHeader>()
                    .Any(static column => column.Text == "Date created")
                    && managedList.Columns.Cast<ColumnHeader>()
                        .Any(column => column.Text == shellColumn.column.Text));
                popup!.Close();

                typeof(ManagedDetailsListView)
                    .GetMethod("ApplyColumnSelection",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(managedList,
                        [new HashSet<string>(["Name", "DateModified", "Type",
                            "Size", "DateCreated", "Attributes", "Extension",
                            "FullPath", shellColumn.column.Id])]);
                await WaitUntilAsync(() => managedList.Columns.Count == 9);
                NativeMethods.SendMessageI(managedList.Handle,
                    NativeMethods.LVM_SCROLL, new IntPtr(250), IntPtr.Zero);
                int managedOrigin = GetHorizontalOrigin(managedList);
                Assert.True(managedOrigin > 0);

                TextBox filterTextBox = GetField<TextBox>(panel,
                    "_filterTextBox");
                ListView filterList = GetField<ListView>(panel,
                    "_filterListView");
                filterTextBox.Text = "picker-match";
                await WaitUntilAsync(() => filterList.VirtualListSize == 1);
                Assert.True(GetHorizontalOrigin(filterList) > 0);
                Assert.Equal(managedList.Columns.Cast<ColumnHeader>()
                        .Select(static column => column.Text),
                    filterList.Columns.Cast<ColumnHeader>()
                        .Select(static column => column.Text));
                Assert.Equal(filterList.Columns.Count,
                    filterList.Items[0].SubItems.Count);
                FindFilterColumn(filterList, shellColumn.column.Text);
                int columnIndex = FindFilterColumn(filterList, "Date created");
                Assert.Equal(File.GetCreationTime(file).ToString("g"),
                    filterList.Items[0].SubItems[columnIndex].Text);
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task NativeDetailsColumns_AppearInFilteredResultsInTheSameOrder()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.NativeFilterColumns.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "native-match.txt");
        await File.WriteAllTextAsync(file, "test");

        try
        {
            await RunOnStaFormAsync(async form =>
            {
                using var panel = new PanelView { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                panel.InitialBrowserReady += (_, _) => ready.TrySetResult();
                panel.Launch([folder]);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));

                ExplorerHost host = GetField<List<ExplorerHost>>(panel,
                    "_hosts")[0];
                IReadOnlyList<ExplorerShellColumn> nativeColumns = [];
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    nativeColumns = await host.GetVisibleShellColumnsAsync();
                    if (nativeColumns.Count > 0) break;
                    await Task.Delay(50);
                }
                Assert.NotEmpty(nativeColumns);

                Assert.True(NativeMethods.PSGetPropertyKeyFromName(
                    "System.Author", out NativeMethods.PROPERTYKEY authorKey) >= 0);
                Assert.True(NativeMethods.PSGetPropertyKeyFromName(
                    "System.DateCreated",
                    out NativeMethods.PROPERTYKEY dateCreatedKey) >= 0);
                NativeMethods.PROPERTYKEY[] originalKeys = nativeColumns
                    .Select(static column => column.Key).ToArray();
                NativeMethods.PROPERTYKEY[] withAuthors = originalKeys
                    .Where(key => key.fmtid != authorKey.fmtid
                        || key.pid != authorKey.pid)
                    .Where(key => key.fmtid != dateCreatedKey.fmtid
                        || key.pid != dateCreatedKey.pid)
                    .Append(dateCreatedKey)
                    .Append(authorKey).ToArray();
                await SetNativeColumnsAsync(host, withAuthors);

                try
                {
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        nativeColumns = await host.GetVisibleShellColumnsAsync();
                        if (nativeColumns.Any(static column =>
                                column.DisplayName == "Authors")) break;
                        await Task.Delay(50);
                    }
                    Assert.Equal(["Date created", "Authors"],
                        nativeColumns.TakeLast(2)
                            .Select(static column => column.DisplayName));

                    typeof(PanelView)
                        .GetField("_managedDetailsOverlaySuppressed",
                            System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic)!
                        .SetValue(panel, true);
                    GetField<ManagedDetailsListView>(panel,
                        "_managedDetailsListView").Visible = false;

                    TextBox filterTextBox = GetField<TextBox>(panel,
                        "_filterTextBox");
                    ListView filterList = GetField<ListView>(panel,
                        "_filterListView");
                    filterTextBox.Text = "native-match";
                    await WaitUntilAsync(() => filterList.VirtualListSize == 1);
                    Assert.Equal(nativeColumns.Select(static column =>
                            column.DisplayName),
                        filterList.Columns.Cast<ColumnHeader>()
                            .Select(static column => column.Text));
                    Assert.Equal(filterList.Columns.Count,
                        filterList.Items[0].SubItems.Count);
                    Assert.Equal(File.GetCreationTime(file).ToString("g"),
                        filterList.Items[0].SubItems[
                            FindFilterColumn(filterList, "Date created")].Text);
                }
                finally
                {
                    await SetNativeColumnsAsync(host, originalKeys);
                }
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string[] GetFilterNames(ListView list) =>
        Enumerable.Range(0, list.VirtualListSize)
            .Select(index => list.Items[index].Text)
            .ToArray();

    private static int GetHorizontalOrigin(ListView list) =>
        NativeMethods.GetScrollPos(list.Handle, NativeMethods.SB_HORZ);

    private static async Task SetNativeColumnsAsync(ExplorerHost host,
        NativeMethods.PROPERTYKEY[] keys)
    {
        BrowserThread thread = GetField<BrowserThread>(host, "_browserThread");
        await thread.InvokeAsync(() =>
        {
            NativeMethods.IExplorerBrowser browser =
                GetField<NativeMethods.IExplorerBrowser>(host, "_browser");
            Guid managerId = typeof(NativeMethods.IColumnManager).GUID;
            int result = browser.GetCurrentView(ref managerId, out IntPtr pointer);
            Assert.True(result >= 0 && pointer != IntPtr.Zero);
            try
            {
                var manager = (NativeMethods.IColumnManager)
                    System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(
                        pointer);
                Assert.True(manager.SetColumns(keys, (uint)keys.Length) >= 0);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(pointer);
            }
        });
    }

    private static string? GetSelectedFilterPath(ListView list)
    {
        int index = Assert.Single(list.SelectedIndices.Cast<int>());
        return list.Items[index].Tag as string;
    }

    private static int FindFilterColumn(ListView list, string title) =>
        list.Columns.Cast<ColumnHeader>()
            .Select((header, index) => (header, index))
            .First(entry => entry.header.Text == title).index;

    private static void ClickFilterColumn(
        PanelView panel, ListView list, string title) =>
        typeof(PanelView)
            .GetMethod("OnFilterListColumnClick",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(panel, [list,
                new ColumnClickEventArgs(FindFilterColumn(list, title))]);

    private static int FindFilterItem(ListView list, string name)
    {
        int index = FindFilterItemOrNegative(list, name);
        Assert.True(index >= 0, $"Filtered item '{name}' was not found.");
        return index;
    }

    private static int FindFilterItemOrNegative(ListView list, string name)
    {
        for (int index = 0; index < list.VirtualListSize; index++)
        {
            if (string.Equals(list.Items[index].Text, name,
                    StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static void InvokeFilterKey(
        PanelView panel, string methodName, Control sender, KeyEventArgs e) =>
        typeof(PanelView)
            .GetMethod(methodName, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(panel, [sender, e]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(40, timeout.Token);
        }
    }

    private static async Task RunOnStaFormAsync(Func<Form, Task> action)
    {
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var threadExited = new TaskCompletionSource(
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
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await action(form);
                        completed.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        completed.TrySetException(ex);
                    }
                    finally
                    {
                        form.Close();
                    }
                };
                Application.Run(form);
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
            finally
            {
                threadExited.TrySetResult();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await threadExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static T GetField<T>(object instance, string name) where T : class =>
        (T)(instance.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance)
            ?? throw new InvalidOperationException($"Field '{name}' was null."));
}
