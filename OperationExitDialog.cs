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
        Text = "File operations in progress";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 300);
        MinimumSize = new Size(560, 280);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
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
            Margin = new Padding(0, 0, 0, 12),
        };
        var details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = description,
            Margin = new Padding(0, 0, 0, 12),
            MinimumSize = new Size(0, 100),
        };
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        for (int column = 0; column < buttons.ColumnCount; column++)
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        buttons.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Button stayOpen = MakeButton("Stay open", OperationExitChoice.StayOpen);
        buttons.Controls.Add(stayOpen, 0, 0);
        buttons.Controls.Add(MakeButton("Cancel then exit", OperationExitChoice.CancelAndExit), 1, 0);
        buttons.Controls.Add(MakeButton("Wait then exit", OperationExitChoice.WaitInTray), 2, 0);
        buttons.Controls.Add(MakeButton("Exit, keep running", OperationExitChoice.ExitAndContinue), 3, 0);

        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(details, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = stayOpen;
        ThemeManager.ApplyTo(this);
    }

    private Button MakeButton(string text, OperationExitChoice choice)
    {
        var button = new RoundedButton
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(110, 32),
            Margin = new Padding(4, 0, 4, 0),
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
