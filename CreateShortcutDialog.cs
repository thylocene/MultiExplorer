namespace MultiExplorer;

internal sealed class CreateShortcutDialog : Form
{
    private readonly ShortcutCreationService _creationService;
    private readonly string _currentFolder;
    private readonly string _otherPaneFolder;
    private readonly string _desktopFolder;
    private readonly TextBox _targetBox;
    private readonly TextBox _nameBox;
    private readonly ComboBox _typeBox;
    private readonly ComboBox _locationBox;
    private readonly TextBox _destinationBox;
    private readonly Label _guidance;
    private readonly RoundedButton _createButton;
    private string _suggestedName;

    internal string? CreatedPath { get; private set; }

    internal CreateShortcutDialog(string targetPath, string currentFolder,
        string otherPaneFolder, ShortcutCreationService creationService,
        string? desktopFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFolder);
        ArgumentNullException.ThrowIfNull(otherPaneFolder);
        ArgumentNullException.ThrowIfNull(creationService);

        _currentFolder = currentFolder;
        _otherPaneFolder = otherPaneFolder;
        _desktopFolder = desktopFolder
            ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _creationService = creationService;
        _suggestedName = ShortcutCreationService.SuggestName(targetPath,
            ShortcutType.ShellLink);

        SuspendLayout();
        Text = "Create shortcut";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimumSize = new Size(650, 420);
        ClientSize = new Size(740, 440);
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22),
            ColumnCount = 2,
            RowCount = 8,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 6; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _targetBox = new TextBox
        {
            Name = "shortcutTarget",
            Text = targetPath,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14),
        };
        _nameBox = new TextBox
        {
            Name = "shortcutName",
            Text = _suggestedName,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14),
        };
        _typeBox = new ComboBox
        {
            Name = "shortcutType",
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 0, 0, 14),
        };
        _typeBox.Items.AddRange(["Shortcut (.lnk)", "Hard link", "Symlink"]);
        _typeBox.SelectedIndex = (int)ShortcutType.ShellLink;
        _locationBox = new ComboBox
        {
            Name = "shortcutLocation",
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 0, 0, 14),
        };
        _locationBox.Items.AddRange(
            ["Current folder", "Parent folder", "Desktop", "Other browser pane"]);
        _locationBox.SelectedIndex = (int)ShortcutLocation.CurrentFolder;
        _destinationBox = new TextBox
        {
            Name = "shortcutDestination",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            TabStop = false,
            Margin = new Padding(0, 0, 0, 14),
        };
        _guidance = new Label
        {
            Name = "shortcutGuidance",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
        };

        AddField(layout, 0, "Target:", _targetBox);
        AddField(layout, 1, "Name:", _nameBox);
        AddField(layout, 2, "Type:", _typeBox);
        AddField(layout, 3, "Created in:", _locationBox);
        AddField(layout, 4, "Destination:", _destinationBox);
        layout.Controls.Add(_guidance, 1, 5);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        _createButton = new RoundedButton
        {
            Name = "createShortcut",
            Text = "Create",
            AutoSize = true,
            MinimumSize = new Size(115, 38),
        };
        var cancelButton = new RoundedButton
        {
            Name = "cancelShortcut",
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(115, 38),
        };
        _createButton.Click += (_, _) => CreateShortcut();
        buttons.Controls.Add(_createButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons, 0, 7);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = _createButton;
        CancelButton = cancelButton;

        _targetBox.TextChanged += (_, _) => UpdateSuggestionAndValidation();
        _typeBox.SelectedIndexChanged += (_, _) => UpdateSuggestionAndValidation();
        _nameBox.TextChanged += (_, _) => UpdateValidation();
        _locationBox.SelectedIndexChanged += (_, _) => UpdateValidation();

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ThemeManager.ApplyTo(this);
        UpdateValidation();
        Shown += (_, _) => ThemeManager.ApplyNativeWindow(Handle);
        ResumeLayout(performLayout: true);
    }

    private static void AddField(TableLayoutPanel layout, int row,
        string label, Control input)
    {
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 16, 14),
        };
        layout.Controls.Add(caption, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private ShortcutType SelectedType => (ShortcutType)_typeBox.SelectedIndex;

    private ShortcutLocation SelectedLocation =>
        (ShortcutLocation)_locationBox.SelectedIndex;

    private void UpdateSuggestionAndValidation()
    {
        string suggestion = ShortcutCreationService.SuggestName(
            _targetBox.Text, SelectedType);
        if (_nameBox.Text == _suggestedName)
            _nameBox.Text = suggestion;
        _suggestedName = suggestion;
        UpdateValidation();
    }

    private void UpdateValidation()
    {
        string? destination = ShortcutCreationService.ResolveDestination(
            SelectedLocation, _currentFolder, _otherPaneFolder, _desktopFolder);
        _destinationBox.Text = destination ?? "No parent folder is available";

        string target = _targetBox.Text.Trim();
        bool isFile = File.Exists(target);
        bool isDirectory = Directory.Exists(target);
        string? error = !isFile && !isDirectory
            ? "Choose an existing file or folder as the target."
            : SelectedType == ShortcutType.HardLink && !isFile
                ? "Hard links can only target files on the same volume."
                : destination is null || !Directory.Exists(destination)
                    ? "The selected destination folder is unavailable."
                    : !ShortcutCreationService.IsValidName(_nameBox.Text,
                        SelectedType)
                        ? "Enter a valid shortcut name."
                        : null;
        _createButton.Enabled = error is null;
        _guidance.Text = error ?? SelectedType switch
        {
            ShortcutType.HardLink =>
                "Hard links require the target and destination to be on the same volume.",
            ShortcutType.SymbolicLink =>
                "Creating a symlink may require Windows Developer Mode or permission.",
            _ => "A .lnk shortcut points to the target without copying it.",
        };
    }

    private void CreateShortcut()
    {
        if (!_createButton.Enabled) return;
        try
        {
            string? destination = ShortcutCreationService.ResolveDestination(
                SelectedLocation, _currentFolder, _otherPaneFolder, _desktopFolder);
            if (destination is null)
                throw new DirectoryNotFoundException(
                    "The selected destination folder is unavailable.");
            CreatedPath = _creationService.Create(new ShortcutCreationRequest(
                _targetBox.Text, _nameBox.Text, SelectedType, destination));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidOperationException
                                   or NotSupportedException
                                   or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn(ex, nameof(CreateShortcutDialog),
                "Could not create the requested link.");
            _guidance.Text = ex.Message;
        }
    }
}
