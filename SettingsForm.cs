using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

internal sealed record SettingsDialogState(
    ApplicationTheme Theme,
    int HotkeyModifiers,
    int HotkeyVirtualKey,
    bool StartWithWindows,
    bool MinimizeToTray,
    bool QuickLookEnabled,
    bool ConfirmFileAndFolderDeletions,
    bool OpenExplorerWhenTabDroppedOutside,
    bool SortFoldersWithFilesByName);

internal sealed class SettingsForm : Form
{
    private readonly ComboBox _theme;
    private readonly Label _hotkey;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _minimizeToTray;
    private readonly CheckBox _quickLook;
    private readonly CheckBox _confirmDeletions;
    private readonly CheckBox _openExplorerWhenTabDroppedOutside;
    private readonly CheckBox _sortFoldersWithFilesByName;
    private readonly Panel _body;
    private readonly TableLayoutPanel _content;
    private int _hotkeyModifiers;
    private int _hotkeyVirtualKey;

    internal ApplicationTheme SelectedTheme => _theme.SelectedIndex == 1
        ? ApplicationTheme.Dark
        : ApplicationTheme.Light;
    internal int SelectedHotkeyModifiers => _hotkeyModifiers;
    internal int SelectedHotkeyVirtualKey => _hotkeyVirtualKey;
    internal bool StartWithWindows => _startWithWindows.Checked;
    internal bool MinimizeToTray => _minimizeToTray.Checked;
    internal bool QuickLookEnabled => _quickLook.Checked;
    internal bool ConfirmFileAndFolderDeletions => _confirmDeletions.Checked;
    internal bool OpenExplorerWhenTabDroppedOutside =>
        _openExplorerWhenTabDroppedOutside.Checked;
    internal bool SortFoldersWithFilesByName => _sortFoldersWithFilesByName.Checked;

    internal SettingsForm(
        SettingsDialogState state,
        Action openExplorerOptions)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(openExplorerOptions);

        _hotkeyModifiers = state.HotkeyModifiers;
        _hotkeyVirtualKey = state.HotkeyVirtualKey;

        Text = "MultiExplorer Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9F);
        ClientSize = new Size(640, 525);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 1,
            RowCount = 2,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _body = new Panel
        {
            Name = "settingsBody",
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = Padding.Empty,
        };
        _content = new TableLayoutPanel
        {
            Name = "settingsContent",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Padding.Empty,
            ColumnCount = 1,
            RowCount = 6,
        };
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int row = 0; row < 6; row++)
            _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = "Settings",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 9),
        };
        _content.Controls.Add(heading, 0, 0);

        var explorerGroup = CreateGroup("Explorer", rowCount: 4, out TableLayoutPanel explorer);
        var explorerLabel = CreateSettingLabel(
            "Explorer options",
            "Open the Windows options for folders, search, and file visibility.");
        var explorerButton = CreateActionButton("Open...");
        explorerButton.Name = "openExplorerOptions";
        explorerButton.Click += (_, _) => openExplorerOptions();
        explorer.Controls.Add(explorerLabel, 0, 0);
        explorer.Controls.Add(explorerButton, 1, 0);
        _confirmDeletions = new CheckBox
        {
            Name = "confirmDeletions",
            Text = "Confirm file and folder deletions",
            Checked = state.ConfirmFileAndFolderDeletions,
            AutoSize = true,
            Margin = new Padding(0, 7, 0, 2),
        };
        explorer.SetColumnSpan(_confirmDeletions, 2);
        explorer.Controls.Add(_confirmDeletions, 0, 1);
        _openExplorerWhenTabDroppedOutside = CreateOption(
            "openExplorerWhenTabDroppedOutside",
            "Open tabs dropped outside MultiExplorer in Windows File Explorer",
            state.OpenExplorerWhenTabDroppedOutside);
        explorer.SetColumnSpan(_openExplorerWhenTabDroppedOutside, 2);
        explorer.Controls.Add(_openExplorerWhenTabDroppedOutside, 0, 2);
        _sortFoldersWithFilesByName = CreateOption(
            "sortFoldersWithFilesByName",
            "Sort files and folders together by name",
            state.SortFoldersWithFilesByName);
        explorer.SetColumnSpan(_sortFoldersWithFilesByName, 2);
        explorer.Controls.Add(_sortFoldersWithFilesByName, 0, 3);
        _content.Controls.Add(explorerGroup, 0, 1);

        var appearanceGroup = CreateGroup(
            "Appearance", rowCount: 1, out TableLayoutPanel appearance);
        appearance.Controls.Add(CreateSettingLabel(
            "Application theme",
            "Choose the colour theme used by MultiExplorer."), 0, 0);
        _theme = new ComboBox
        {
            Name = "applicationTheme",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(12, 4, 0, 4),
        };
        _theme.Items.AddRange(["Light", "Dark"]);
        _theme.SelectedIndex = state.Theme == ApplicationTheme.Dark ? 1 : 0;
        appearance.Controls.Add(_theme, 1, 0);
        _content.Controls.Add(appearanceGroup, 0, 2);

        var hotkeyGroup = CreateGroup(
            "Hotkey", rowCount: 1, out TableLayoutPanel hotkeyLayout);
        _hotkey = CreateSettingLabel(
            "Show or hide MultiExplorer",
            HotkeyDialog.Describe(_hotkeyModifiers, _hotkeyVirtualKey));
        _hotkey.Name = "hotkeyDescription";
        var hotkeyButton = CreateActionButton("Change...");
        hotkeyButton.Name = "changeHotkey";
        hotkeyButton.Click += OnChangeHotkey;
        hotkeyLayout.Controls.Add(_hotkey, 0, 0);
        hotkeyLayout.Controls.Add(hotkeyButton, 1, 0);
        _content.Controls.Add(hotkeyGroup, 0, 3);

        var startupGroup = CreateGroup(
            "Startup and window behaviour", rowCount: 2,
            out TableLayoutPanel startup);
        _startWithWindows = CreateOption(
            "startWithWindows",
            "Start MultiExplorer automatically when I sign in",
            state.StartWithWindows);
        _minimizeToTray = CreateOption(
            "minimizeToTray",
            "Minimize to System Tray when the main window is closed",
            state.MinimizeToTray);
        startup.SetColumnSpan(_startWithWindows, 2);
        startup.SetColumnSpan(_minimizeToTray, 2);
        startup.Controls.Add(_startWithWindows, 0, 0);
        startup.Controls.Add(_minimizeToTray, 0, 1);
        _content.Controls.Add(startupGroup, 0, 4);

        var integrationGroup = CreateGroup(
            "Integration", rowCount: 1, out TableLayoutPanel integration);
        _quickLook = CreateOption(
            "quickLookIntegration",
            "QuickLook integration (Space)",
            state.QuickLookEnabled);
        integration.SetColumnSpan(_quickLook, 2);
        integration.Controls.Add(_quickLook, 0, 0);
        _content.Controls.Add(integrationGroup, 0, 5);

        var buttons = new FlowLayoutPanel
        {
            Name = "settingsActions",
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 10, 0, 0),
        };
        var cancel = CreateActionButton("Cancel");
        cancel.Name = "cancelSettings";
        cancel.DialogResult = DialogResult.Cancel;
        var save = CreateActionButton("Save");
        save.Name = "saveSettings";
        save.DialogResult = DialogResult.OK;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        _body.Controls.Add(_content);
        root.Controls.Add(_body, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        // Apply DPI autoscaling only after the complete control tree exists so
        // every label, selector, group, and button scales as one layout.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(656, 0);

        ThemeManager.ApplyTo(this);
        Shown += (_, _) => ThemeManager.ApplyNativeWindow(Handle);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitHeightToContent();
    }

    internal void FitHeightToContent()
    {
        PerformLayout();

        int heightAdjustment = _content.Height - _body.ClientSize.Height;
        int nonClientHeight = Height - ClientSize.Height;
        int screenMargin = ScaleLogical(24);
        int maximumClientHeight = Math.Max(
            ScaleLogical(300),
            Screen.FromControl(this).WorkingArea.Height
                - nonClientHeight
                - screenMargin);
        int fittedHeight = Math.Min(
            ClientSize.Height + heightAdjustment,
            maximumClientHeight);

        if (fittedHeight != ClientSize.Height)
            ClientSize = new Size(ClientSize.Width, fittedHeight);

        PerformLayout();
    }

    private int ScaleLogical(int logicalPixels) =>
        (int)Math.Round(logicalPixels * DeviceDpi / 96F);

    private void OnChangeHotkey(object? sender, EventArgs e)
    {
        using var dialog = new HotkeyDialog(
            _hotkeyModifiers, _hotkeyVirtualKey);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _hotkeyModifiers = dialog.SelectedModifiers;
        _hotkeyVirtualKey = dialog.SelectedVk;
        _hotkey.Text = "Show or hide MultiExplorer" + Environment.NewLine
            + HotkeyDialog.Describe(_hotkeyModifiers, _hotkeyVirtualKey);
    }

    private static GroupBox CreateGroup(
        string title,
        int rowCount,
        out TableLayoutPanel layout)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 7, 12, 8),
            Margin = new Padding(0, 0, 0, 7),
        };
        layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rowCount,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int row = 0; row < rowCount; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.Controls.Add(layout);
        return group;
    }

    private static Label CreateSettingLabel(string title, string description) => new()
    {
        Text = title + Environment.NewLine + description,
        AutoSize = true,
        Margin = new Padding(0, 2, 8, 2),
        Anchor = AnchorStyles.Left | AnchorStyles.Top,
    };

    private static CheckBox CreateOption(
        string name,
        string text,
        bool isChecked) => new()
    {
        Name = name,
        Text = text,
        Checked = isChecked,
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 4),
    };

    private static RoundedButton CreateActionButton(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Size = new Size(104, 32),
        Margin = new Padding(8, 3, 0, 3),
        Anchor = AnchorStyles.Right | AnchorStyles.Top,
    };
}
