namespace MultiExplorer.Tests;

public sealed class ShareDialogTests
{
    [Fact]
    public async Task Dialog_ListsFilesAndProvidesAllSharingActions()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "MultiExplorer.Tests", $"share-dialog-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "document.txt");
        await File.WriteAllTextAsync(file, "test");

        try
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var dialog = new ShareDialog([file],
                        new FileSharingService(), static (_, _) => { },
                        static (_, _) => true);
                    dialog.PerformLayout();

                    Assert.Equal("Share 1 file",
                        FindControl<Label>(dialog, "shareHeading").Text);
                    Assert.Single(FindControl<ListView>(dialog,
                        "shareFileList").Items.Cast<ListViewItem>());
                    Assert.Equal("Email with attachments",
                        FindControl<Button>(dialog, "shareEmail").Text);
                    Assert.Equal("Send to…",
                        FindControl<Button>(dialog, "shareSendTo").Text);
                    Assert.Equal("Copy files",
                        FindControl<Button>(dialog, "shareCopyFiles").Text);
                    Assert.Equal("Copy file paths",
                        FindControl<Button>(dialog, "shareCopyPaths").Text);
                    Assert.Equal("Save copies…",
                        FindControl<Button>(dialog, "shareSaveCopies").Text);
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
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static T FindControl<T>(Control root, string name) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child.Name == name && child is T match) return match;
            T? descendant = FindControlOrDefault<T>(child, name);
            if (descendant is not null) return descendant;
        }
        throw new InvalidOperationException($"Control '{name}' was not found.");
    }

    private static T? FindControlOrDefault<T>(Control root, string name)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child.Name == name && child is T match) return match;
            T? descendant = FindControlOrDefault<T>(child, name);
            if (descendant is not null) return descendant;
        }
        return null;
    }
}
