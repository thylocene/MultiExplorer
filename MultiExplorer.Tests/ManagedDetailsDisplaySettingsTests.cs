namespace MultiExplorer.Tests;

public sealed class ManagedDetailsDisplaySettingsTests
{
    [Theory]
    [InlineData(false, 32)]
    [InlineData(true, 24)]
    public void GetMinimumRowHeight_UsesCompactSetting(bool compactMode, int expected)
    {
        Assert.Equal(expected, ManagedDetailsListView.GetMinimumRowHeight(compactMode));
    }

    [Theory]
    [InlineData("report.txt", false, true, "report.txt")]
    [InlineData("report.txt", false, false, "report")]
    [InlineData("archive.tar.gz", false, false, "archive.tar")]
    [InlineData("folder.with.dots", true, false, "folder.with.dots")]
    [InlineData("README", false, false, "README")]
    public void GetDisplayName_RespectsFileExtensionSetting(
        string name,
        bool isDirectory,
        bool showFileExtensions,
        string expected)
    {
        Assert.Equal(expected, ManagedDetailsListView.GetDisplayName(
            name, isDirectory, showFileExtensions));
    }

    [Theory]
    [InlineData("report.txt", "renamed", false, false, "renamed.txt")]
    [InlineData("report.txt", "renamed.csv", false, true, "renamed.csv")]
    [InlineData("folder.old", "folder.new", true, false, "folder.new")]
    public void BuildFileSystemName_PreservesHiddenFileExtension(
        string originalName,
        string editedName,
        bool isDirectory,
        bool showFileExtensions,
        string expected)
    {
        Assert.Equal(expected, ManagedDetailsListView.BuildFileSystemName(
            originalName, editedName, isDirectory, showFileExtensions));
    }

    [Theory]
    [InlineData("MyFile.ext", false, true, 6)]
    [InlineData("archive.tar.gz", false, true, 11)]
    [InlineData(".gitignore", false, true, 10)]
    [InlineData("README", false, true, 6)]
    [InlineData("folder.with.dots", true, true, 16)]
    [InlineData("archive.tar", false, false, 11)]
    public void GetInitialRenameSelectionLength_SelectsOnlyVisibleFileBaseName(
        string displayName,
        bool isDirectory,
        bool fileExtensionsVisible,
        int expected)
    {
        Assert.Equal(expected,
            ManagedDetailsListView.GetInitialRenameSelectionLength(
                displayName, isDirectory, fileExtensionsVisible));
    }

    [Theory]
    [InlineData('a', true)]
    [InlineData('7', true)]
    [InlineData('-', true)]
    [InlineData('é', true)]
    [InlineData(' ', false)]
    [InlineData('\t', false)]
    [InlineData('\u007F', false)]
    public void IsFilterCharacter_AcceptsTypedFileNameCharacters(
        char character, bool expected)
    {
        Assert.Equal(expected,
            ManagedDetailsListView.IsFilterCharacter(character));
    }

    [Fact]
    public void KeyPress_ForwardsTheFirstCharacterToThePanelFilter()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var list = new ManagedDetailsListView();
                char? forwarded = null;
                list.FilterCharInput += (_, character) => forwarded = character;
                var args = new KeyPressEventArgs('r');
                typeof(ManagedDetailsListView)
                    .GetMethod("OnKeyPress",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(list, [args]);

                Assert.Equal('r', forwarded);
                Assert.True(args.Handled);
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
}
