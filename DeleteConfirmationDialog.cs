using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

internal sealed class DeleteConfirmationDialog : Form
{
    internal DeleteConfirmationDialog(
        FileOperationKind operation,
        IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (operation is not (FileOperationKind.Delete
            or FileOperationKind.DeletePermanently))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Text = operation == FileOperationKind.DeletePermanently
            ? "Confirm permanent deletion"
            : "Confirm deletion";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var question = new Label
        {
            Name = "deleteQuestion",
            Text = CreateQuestion(operation, paths.Count),
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(question, 0, 0);

        var details = new Label
        {
            Name = "deleteDetails",
            Text = CreateDetails(operation, paths),
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 16),
        };
        layout.Controls.Add(details, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        var cancel = new RoundedButton
        {
            Name = "cancelDelete",
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = false,
            Size = new Size(104, 32),
            Margin = new Padding(8, 3, 0, 3),
        };
        var confirm = new RoundedButton
        {
            Name = "confirmDelete",
            Text = operation == FileOperationKind.DeletePermanently
                ? "Delete permanently"
                : "Delete",
            DialogResult = DialogResult.OK,
            AutoSize = false,
            Size = operation == FileOperationKind.DeletePermanently
                ? new Size(150, 32)
                : new Size(104, 32),
            Margin = new Padding(0, 3, 0, 3),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(confirm);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
        AcceptButton = confirm;
        CancelButton = cancel;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(560, 0);

        ThemeManager.ApplyTo(this);
        Shown += (_, _) => ThemeManager.ApplyNativeWindow(Handle);
    }

    internal static string CreateQuestion(
        FileOperationKind operation,
        int itemCount)
    {
        string target = itemCount == 1 ? "this item" : $"these {itemCount:N0} items";
        return operation == FileOperationKind.DeletePermanently
            ? $"Permanently delete {target}?"
            : $"Move {target} to the Recycle Bin?";
    }

    private static string CreateDetails(
        FileOperationKind operation,
        IReadOnlyList<string> paths)
    {
        string warning = operation == FileOperationKind.DeletePermanently
            ? "This action cannot be undone."
            : "You can restore the item from the Recycle Bin.";
        if (paths.Count != 1)
            return warning;

        string path = Path.TrimEndingDirectorySeparator(paths[0]);
        string name = Path.GetFileName(path);
        return (string.IsNullOrWhiteSpace(name) ? path : name)
            + Environment.NewLine + warning;
    }
}
