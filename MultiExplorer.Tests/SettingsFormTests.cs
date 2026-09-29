namespace MultiExplorer.Tests;

public sealed class SettingsFormTests
{
    [Fact]
    public void SettingsForm_IsDpiAwareAndPreservesInitialValues()
    {
        RunOnSta(() =>
        {
            var state = new SettingsDialogState(
                ApplicationTheme.Dark,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT,
                0x4B,
                StartWithWindows: true,
                MinimizeToTray: false,
                QuickLookEnabled: true,
                ConfirmFileAndFolderDeletions: false,
                OpenExplorerWhenTabDroppedOutside: true,
                SortFoldersWithFilesByName: true);
            using var form = new SettingsForm(
                state,
                () => { });

            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.Equal(new SizeF(96F, 96F), form.AutoScaleDimensions);
            Assert.Equal(ApplicationTheme.Dark, form.SelectedTheme);
            Assert.Equal(state.HotkeyModifiers, form.SelectedHotkeyModifiers);
            Assert.Equal(state.HotkeyVirtualKey, form.SelectedHotkeyVirtualKey);
            Assert.True(form.StartWithWindows);
            Assert.False(form.MinimizeToTray);
            Assert.True(form.QuickLookEnabled);
            Assert.False(form.ConfirmFileAndFolderDeletions);
            Assert.True(form.OpenExplorerWhenTabDroppedOutside);
            Assert.True(form.SortFoldersWithFilesByName);

            RoundedButton openExplorer = Assert.IsType<RoundedButton>(
                form.Controls.Find("openExplorerOptions", true).Single());
            RoundedButton changeHotkey = Assert.IsType<RoundedButton>(
                form.Controls.Find("changeHotkey", true).Single());
            RoundedButton save = Assert.IsType<RoundedButton>(
                form.Controls.Find("saveSettings", true).Single());
            RoundedButton cancel = Assert.IsType<RoundedButton>(
                form.Controls.Find("cancelSettings", true).Single());
            Panel body = Assert.IsType<Panel>(
                form.Controls.Find("settingsBody", true).Single());
            TableLayoutPanel content = Assert.IsType<TableLayoutPanel>(
                form.Controls.Find("settingsContent", true).Single());
            form.FitHeightToContent();

            Assert.True(content.Height <= body.ClientSize.Height,
                $"Settings content height {content.Height} exceeds the "
                + $"available body height {body.ClientSize.Height}.");
            Assert.InRange(body.ClientSize.Height - content.Height, 0, 1);
            Size originalButtonSize = openExplorer.Size;
            form.Scale(new SizeF(1.5F, 1.5F));
            form.FitHeightToContent();

            Assert.True(openExplorer.Width > originalButtonSize.Width);
            Assert.True(openExplorer.Height > originalButtonSize.Height);
            Assert.True(content.Height <= body.ClientSize.Height,
                $"Scaled settings content height {content.Height} exceeds "
                + $"the available body height {body.ClientSize.Height}.");
            Assert.InRange(body.ClientSize.Height - content.Height, 0, 1);
            AssertFullyVisible(form, save);
            AssertFullyVisible(form, cancel);
            Assert.NotNull(changeHotkey);
        });
    }

    [Theory]
    [InlineData((int)FileOperationKind.Delete, 1F)]
    [InlineData((int)FileOperationKind.Delete, 1.5F)]
    [InlineData((int)FileOperationKind.Delete, 2F)]
    [InlineData((int)FileOperationKind.Delete, 2.5F)]
    [InlineData((int)FileOperationKind.DeletePermanently, 1F)]
    [InlineData((int)FileOperationKind.DeletePermanently, 1.5F)]
    [InlineData((int)FileOperationKind.DeletePermanently, 2F)]
    [InlineData((int)FileOperationKind.DeletePermanently, 2.5F)]
    public void DeleteConfirmationDialog_IsDpiAware(
        int operationValue,
        float scale)
    {
        RunOnSta(() =>
        {
            using var form = new DeleteConfirmationDialog(
                (FileOperationKind)operationValue,
                [@"C:\Data\A long report filename that exercises the DPI-aware dialog layout.txt"]);

            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.Equal(new SizeF(96F, 96F), form.AutoScaleDimensions);
            Assert.True(form.AutoSize);
            RoundedButton confirm = Assert.IsType<RoundedButton>(
                form.Controls.Find("confirmDelete", true).Single());
            RoundedButton cancel = Assert.IsType<RoundedButton>(
                form.Controls.Find("cancelDelete", true).Single());
            Label question = Assert.IsType<Label>(
                form.Controls.Find("deleteQuestion", true).Single());
            Label details = Assert.IsType<Label>(
                form.Controls.Find("deleteDetails", true).Single());
            Size originalButtonSize = confirm.Size;

            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();

            if (scale > 1F)
            {
                Assert.True(confirm.Width > originalButtonSize.Width);
                Assert.True(confirm.Height > originalButtonSize.Height);
            }
            Assert.True(question.Bottom <= details.Top,
                $"Question {question.Bounds} overlaps details {details.Bounds}.");
            AssertFullyVisible(form, question);
            AssertFullyVisible(form, details);
            AssertFullyVisible(form, confirm);
            AssertFullyVisible(form, cancel);
            Assert.False(confirm.Bounds.IntersectsWith(cancel.Bounds),
                $"Confirm button {confirm.Bounds} overlaps cancel button {cancel.Bounds}.");
        });
    }

    [Fact]
    public void OperationManager_DoesNotStartRejectedDeletion()
    {
        RunOnSta(() =>
        {
            int confirmationRequests = 0;
            FileOperationKind? requestedOperation = null;
            IReadOnlyList<string>? requestedPaths = null;
            using var manager = new OperationManager(
                () => null,
                (operation, paths) =>
                {
                    confirmationRequests++;
                    requestedOperation = operation;
                    requestedPaths = paths;
                    return false;
                });

            Guid? operationId = manager.Start(
                FileOperationKind.Delete,
                [@"C:\Data\report.txt"]);

            Assert.Null(operationId);
            Assert.Equal(1, confirmationRequests);
            Assert.Equal(FileOperationKind.Delete, requestedOperation);
            Assert.Equal([@"C:\Data\report.txt"], requestedPaths);
        });
    }

    [Theory]
    [InlineData((int)FileOperationKind.Delete, 1,
        "Move this item to the Recycle Bin?")]
    [InlineData((int)FileOperationKind.Delete, 3,
        "Move these 3 items to the Recycle Bin?")]
    [InlineData((int)FileOperationKind.DeletePermanently, 1,
        "Permanently delete this item?")]
    [InlineData((int)FileOperationKind.DeletePermanently, 4,
        "Permanently delete these 4 items?")]
    public void DeleteConfirmation_UsesOperationSpecificQuestion(
        int operationValue,
        int itemCount,
        string expected)
    {
        Assert.Equal(expected,
            DeleteConfirmationDialog.CreateQuestion(
                (FileOperationKind)operationValue, itemCount));
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    private static void AssertFullyVisible(Control ancestor, Control control)
    {
        Point location = control.Location;
        for (Control? parent = control.Parent;
             parent is not null && parent != ancestor;
             parent = parent.Parent)
        {
            location.Offset(parent.Location);
        }

        var bounds = new Rectangle(location, control.Size);
        Assert.True(ancestor.ClientRectangle.Contains(bounds),
            $"{control.Name} bounds {bounds} exceed the dialog client area "
            + $"{ancestor.ClientRectangle}.");
    }
}
