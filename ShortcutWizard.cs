using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// Creates a Shell shortcut using the same two-step interaction as Explorer's
/// New &gt; Shortcut command. No file is created until the user chooses Finish.
/// </summary>
internal sealed class ShortcutWizard : Form
{
    private readonly string _destinationFolder;
    private readonly Label _prompt;
    private readonly Label _instruction;
    private readonly TextBox _valueTextBox;
    private readonly Button _browseButton;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private bool _namingShortcut;
    private string _targetPath = string.Empty;

    private ShortcutWizard(string destinationFolder)
    {
        _destinationFolder = destinationFolder;
        Text = "Create Shortcut";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 220);
        Font = SystemFonts.MessageBoxFont;

        _prompt = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(22, 16, 22, 0),
        };
        _instruction = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(22, 12, 22, 0),
        };
        _valueTextBox = new TextBox
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(22, 104),
            Width = 372,
        };
        _browseButton = new Button
        {
            Text = "Browse...",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(402, 102),
            Size = new Size(96, 26),
        };
        _browseButton.Click += OnBrowseClicked;

        _backButton = new Button
        {
            Text = "< Back",
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(238, 174),
            Size = new Size(80, 28),
            Enabled = false,
        };
        _backButton.Click += (_, _) => ShowTargetPage();
        _nextButton = new Button
        {
            Text = "Next >",
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(326, 174),
            Size = new Size(80, 28),
        };
        _nextButton.Click += (_, _) => Advance();
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(414, 174),
            Size = new Size(80, 28),
        };

        Controls.Add(_prompt);
        Controls.Add(_instruction);
        Controls.Add(_valueTextBox);
        Controls.Add(_browseButton);
        Controls.Add(_backButton);
        Controls.Add(_nextButton);
        Controls.Add(cancelButton);
        AcceptButton = _nextButton;
        CancelButton = cancelButton;
        ShowTargetPage();
        ThemeManager.ApplyTo(this);
    }

    internal static bool ShowForFolder(IWin32Window owner, string destinationFolder)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        using var wizard = new ShortcutWizard(destinationFolder);
        return wizard.ShowDialog(owner) == DialogResult.OK;
    }

    private void ShowTargetPage()
    {
        _namingShortcut = false;
        _prompt.Text = "What item would you like to create a shortcut for?";
        _instruction.Text = "Type the location of the item, or select Browse to find it.";
        _valueTextBox.Text = _targetPath;
        _browseButton.Visible = true;
        _backButton.Enabled = false;
        _nextButton.Text = "Next >";
        _valueTextBox.Focus();
    }

    private void ShowNamePage()
    {
        _namingShortcut = true;
        _prompt.Text = "What would you like to name the shortcut?";
        _instruction.Text = "Type a name for this shortcut.";
        _valueTextBox.Text = SuggestedShortcutName(_targetPath);
        _valueTextBox.SelectAll();
        _browseButton.Visible = false;
        _backButton.Enabled = true;
        _nextButton.Text = "Finish";
        _valueTextBox.Focus();
    }

    private void Advance()
    {
        if (!_namingShortcut)
        {
            string target = _valueTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(this, "Enter the location of the item for the shortcut.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _targetPath = target;
            ShowNamePage();
            return;
        }

        string name = _valueTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "Enter a valid shortcut name.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            CreateShortcut(_destinationFolder, name, _targetPath);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(ShortcutWizard), "Could not create the shortcut.");
            MessageBox.Show(this, "The shortcut could not be created.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select the item for the shortcut",
            CheckFileExists = true,
            CheckPathExists = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _valueTextBox.Text = dialog.FileName;
    }

    private static string SuggestedShortcutName(string targetPath)
    {
        string trimmed = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "New Shortcut" : name;
    }

    internal static void CreateShortcut(string destinationFolder, string name, string targetPath)
    {
        string shortcutPath = GetUniqueShortcutPath(destinationFolder, name);
        Type shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"),
            throwOnError: true)!;
        object shellLinkObject = Activator.CreateInstance(shellLinkType)
            ?? throw new InvalidOperationException("Windows could not create a Shell shortcut.");
        try
        {
            var shellLink = (NativeMethods.IShellLinkW)shellLinkObject;
            ThrowOnFailure(shellLink.SetPath(targetPath), "set the shortcut target");
            ThrowOnFailure(shellLink.SetDescription(name), "set the shortcut description");
            string? workingDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                ThrowOnFailure(shellLink.SetWorkingDirectory(workingDirectory),
                    "set the shortcut working directory");

            var persistFile = (IPersistFile)shellLinkObject;
            persistFile.Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLinkObject);
        }
    }

    private static string GetUniqueShortcutPath(string folder, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string candidate = Path.Combine(folder, stem + ".lnk");
        for (int suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(folder, $"{stem} ({suffix}).lnk");
        return candidate;
    }

    private static void ThrowOnFailure(int hResult, string operation)
    {
        if (hResult < 0)
            Marshal.ThrowExceptionForHR(hResult, new IntPtr(-1));
    }
}
