using System.Text;

namespace MultiExplorer.Tests;

public sealed class CreateShortcutTests
{
    [Fact]
    public void CreateShellLink_UsesSuggestedNameAndAvoidsExistingFile()
    {
        using var folders = new TemporaryFolders();
        string target = Path.Combine(folders.Current, "testfile.txt");
        File.WriteAllText(target, "test");
        var service = new ShortcutCreationService();
        string name = ShortcutCreationService.SuggestName(target, ShortcutType.ShellLink);

        string first = service.Create(new ShortcutCreationRequest(target, name,
            ShortcutType.ShellLink, folders.Current));
        string second = service.Create(new ShortcutCreationRequest(target, name,
            ShortcutType.ShellLink, folders.Current));

        Assert.Equal("testfile.txt - Shortcut.lnk", Path.GetFileName(first));
        Assert.Equal("testfile.txt - Shortcut (2).lnk", Path.GetFileName(second));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void CreateHardLink_SharesFileContentsAndAvoidsExistingName()
    {
        using var folders = new TemporaryFolders();
        string target = Path.Combine(folders.Current, "testfile.txt");
        File.WriteAllText(target, "original");
        var service = new ShortcutCreationService();
        string name = ShortcutCreationService.SuggestName(target, ShortcutType.HardLink);

        string first = service.Create(new ShortcutCreationRequest(target, name,
            ShortcutType.HardLink, folders.Current));
        string second = service.Create(new ShortcutCreationRequest(target, name,
            ShortcutType.HardLink, folders.Current));
        File.WriteAllText(first, "changed");

        Assert.Equal("testfile - Hard link.txt", Path.GetFileName(first));
        Assert.Equal("testfile - Hard link (2).txt", Path.GetFileName(second));
        Assert.Equal("changed", File.ReadAllText(target));
        Assert.Equal("changed", File.ReadAllText(second));
    }

    [Fact]
    public void CreateHardLink_RejectsFolderTarget()
    {
        using var folders = new TemporaryFolders();
        var service = new ShortcutCreationService();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            service.Create(new ShortcutCreationRequest(folders.Current,
                "Folder - Hard link", ShortcutType.HardLink, folders.Other)));

        Assert.Contains("only target a file", error.Message);
    }

    [Fact]
    public void CreateSymbolicLinks_TargetBothFilesAndFolders()
    {
        using var folders = new TemporaryFolders();
        string fileTarget = Path.Combine(folders.Current, "example.txt");
        File.WriteAllText(fileTarget, "linked content");
        var service = new ShortcutCreationService();

        string fileLink = service.Create(new ShortcutCreationRequest(fileTarget,
            "example - Symlink.txt", ShortcutType.SymbolicLink, folders.Other));
        string folderLink = service.Create(new ShortcutCreationRequest(folders.Current,
            "Current - Symlink", ShortcutType.SymbolicLink, folders.Other));

        Assert.Equal(fileTarget, new FileInfo(fileLink).LinkTarget);
        Assert.Equal(folders.Current, new DirectoryInfo(folderLink).LinkTarget);
        Assert.Equal("linked content", File.ReadAllText(fileLink));
    }

    [Fact]
    public void DestinationChoices_ResolveToExpectedFolders()
    {
        using var folders = new TemporaryFolders();
        Assert.Equal(folders.Current,
            ShortcutCreationService.ResolveDestination(ShortcutLocation.CurrentFolder,
                folders.Current, folders.Other, folders.Desktop));
        Assert.Equal(folders.Root,
            ShortcutCreationService.ResolveDestination(ShortcutLocation.ParentFolder,
                folders.Current, folders.Other, folders.Desktop));
        Assert.Equal(folders.Desktop,
            ShortcutCreationService.ResolveDestination(ShortcutLocation.Desktop,
                folders.Current, folders.Other, folders.Desktop));
        Assert.Equal(folders.Other,
            ShortcutCreationService.ResolveDestination(ShortcutLocation.OtherPane,
                folders.Current, folders.Other, folders.Desktop));
    }

    [Fact]
    public void FileContextMenu_ReplacesNativeShortcutEntryWithOneApplicationEntry()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        try
        {
            Assert.True(NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 1, "Open"));
            Assert.True(NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 2,
                "Create shortcut"));
            Assert.True(NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 3,
                "Create shortcut..."));

            Assert.True(PanelView.AddCreateShortcutMenuCommand(menu));

            var labels = new List<string>();
            for (int index = 0; index < NativeMethods.GetMenuItemCount(menu); index++)
            {
                var label = new StringBuilder(128);
                NativeMethods.GetMenuString(menu, (uint)index, label,
                    label.Capacity, NativeMethods.MF_BYPOSITION);
                labels.Add(label.ToString());
            }
            Assert.Equal(["Open", "Create shortcut..."], labels);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(2.5f)]
    public void Dialog_IsDpiAwareAndUsesRoundedButtons(float scale)
    {
        using var folders = new TemporaryFolders();
        string target = Path.Combine(folders.Current, "testfile.txt");
        File.WriteAllText(target, "test");
        RunOnSta(() =>
        {
            using var form = new CreateShortcutDialog(target, folders.Current,
                folders.Other, new ShortcutCreationService(), folders.Desktop);

            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.Equal(new SizeF(96f, 96f), form.AutoScaleDimensions);
            TextBox targetBox = Find<TextBox>(form, "shortcutTarget");
            TextBox nameBox = Find<TextBox>(form, "shortcutName");
            TextBox destinationBox = Find<TextBox>(form, "shortcutDestination");
            ComboBox typeBox = Find<ComboBox>(form, "shortcutType");
            ComboBox locationBox = Find<ComboBox>(form, "shortcutLocation");
            RoundedButton create = Find<RoundedButton>(form, "createShortcut");
            RoundedButton cancel = Find<RoundedButton>(form, "cancelShortcut");
            Size originalButtonSize = create.Size;

            Assert.Equal(target, targetBox.Text);
            Assert.Equal("testfile.txt - Shortcut", nameBox.Text);
            Assert.Equal(folders.Current, destinationBox.Text);
            Assert.Equal(3, typeBox.Items.Count);
            Assert.Equal(4, locationBox.Items.Count);
            locationBox.SelectedIndex = (int)ShortcutLocation.OtherPane;
            Assert.Equal(folders.Other, destinationBox.Text);
            typeBox.SelectedIndex = (int)ShortcutType.HardLink;
            Assert.Equal("testfile - Hard link.txt", nameBox.Text);

            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();

            if (scale > 1f)
            {
                Assert.True(create.Width > originalButtonSize.Width);
                Assert.True(create.Height > originalButtonSize.Height);
            }
            AssertControlsFit(form);
            Assert.False(create.Bounds.IntersectsWith(cancel.Bounds));
        });
    }

    [Fact]
    public void ViewMenu_ShowAndCompareEntriesHaveIcons()
    {
        RunOnSta(() =>
        {
            using var bar = new CommandBar();
            ToolStripDropDownButton view = bar.Items
                .OfType<ToolStripDropDownButton>()
                .Single(item => item.Text == "View");
            Assert.NotNull(view.DropDownItems.OfType<ToolStripMenuItem>()
                .Single(item => item.Text == "Show").Image);
            Assert.NotNull(view.DropDownItems.OfType<ToolStripMenuItem>()
                .Single(item => item.Text == "Compare panes...").Image);
        });
    }

    private static T Find<T>(Control parent, string name) where T : Control =>
        Assert.IsType<T>(parent.Controls.Find(name, true).Single());

    private static void AssertControlsFit(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            Assert.True(parent.ClientRectangle.Contains(child.Bounds),
                $"{child.GetType().Name} {child.Name} bounds {child.Bounds} exceed "
                + $"{parent.GetType().Name} {parent.Name} client area {parent.ClientRectangle}.");
            AssertControlsFit(child);
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private sealed class TemporaryFolders : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.Tests", Guid.NewGuid().ToString("N"));
        internal string Current => Path.Combine(Root, "Current");
        internal string Other => Path.Combine(Root, "Other");
        internal string Desktop => Path.Combine(Root, "Desktop");

        internal TemporaryFolders()
        {
            Directory.CreateDirectory(Current);
            Directory.CreateDirectory(Other);
            Directory.CreateDirectory(Desktop);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
