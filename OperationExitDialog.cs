using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

internal enum OperationExitChoice
{
    StayOpen,
    ExitAndContinue,
    WaitInTray,
    CancelAndExit,
}

internal sealed class OperationExitDialog : Form
{
    internal OperationExitChoice Choice { get; private set; } = OperationExitChoice.StayOpen;

    internal OperationExitDialog(int operationCount, string description)
    {
        SuspendLayout();

        Text = "File operations in progress";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);

        // These are 96-DPI design units. Auto-scaling is enabled after the
        // complete control tree is assembled so WinForms scales the form and
        // all of its children in one pass when the handle is created.
        ClientSize = new Size(760, 380);
        MinimumSize = new Size(640, 340);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(20),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var message = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = $"{operationCount} file operation(s) are still running. " +
                   "Choose how MultiExplorer should close.",
            Margin = new Padding(0, 0, 0, 16),
        };
        var details = new TextBox
        {
            Name = "operationDetails",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = description,
            Margin = new Padding(0, 0, 0, 16),
        };
        var buttons = new TableLayoutPanel
        {
            Name = "operationActions",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        for (int column = 0; column < buttons.ColumnCount; column++)
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        buttons.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Button stayOpen = MakeButton("Stay open", OperationExitChoice.StayOpen);
        buttons.Controls.Add(stayOpen, 0, 0);
        buttons.Controls.Add(MakeButton("Cancel then exit", OperationExitChoice.CancelAndExit), 1, 0);
        buttons.Controls.Add(MakeButton("Wait then exit", OperationExitChoice.WaitInTray), 0, 1);
        buttons.Controls.Add(MakeButton("Exit, keep running", OperationExitChoice.ExitAndContinue), 1, 1);

        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(details, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        // Do not give the percentage-sized details row a minimum height. On a
        // high-DPI display that can make the row overflow the client area and
        // push this auto-sized action row below the bottom of the dialog. The
        // form-level MinimumSize provides the normal details area; if space is
        // unexpectedly tight, the textbox is the part that must shrink.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;

        CancelButton = stayOpen;
        ThemeManager.ApplyTo(this);
        Shown += (_, _) => ThemeManager.ApplyNativeWindow(Handle);
        ResumeLayout(performLayout: true);
    }

    private Button MakeButton(string text, OperationExitChoice choice)
    {
        var button = new RoundedButton
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(220, 40),
            Margin = new Padding(6),
        };
        button.Click += (_, _) =>
        {
            Choice = choice;
            DialogResult = choice == OperationExitChoice.StayOpen
                ? DialogResult.Cancel : DialogResult.OK;
            Close();
        };
        return button;
    }
}
