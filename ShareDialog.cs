namespace MultiExplorer;

internal sealed class ShareDialog : Form
{
    private readonly string[] _files;
    private readonly FileSharingService _sharingService;
    private readonly Action<Control, IReadOnlyList<string>> _showSendTo;
    private readonly Func<IReadOnlyList<string>, string, bool> _saveCopies;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ToolTip _toolTip = new();
    private readonly TableLayoutPanel _actions;
    private readonly Label _status;
    private readonly Button _closeButton;

    internal ShareDialog(IReadOnlyList<string> files,
        FileSharingService sharingService,
        Action<Control, IReadOnlyList<string>> showSendTo,
        Func<IReadOnlyList<string>, string, bool> saveCopies)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sharingService);
        ArgumentNullException.ThrowIfNull(showSendTo);
        ArgumentNullException.ThrowIfNull(saveCopies);

        _files = FileSharingService.GetShareableFiles(files);
        if (_files.Length == 0)
            throw new ArgumentException("At least one existing file is required.",
                nameof(files));

        _sharingService = sharingService;
        _showSendTo = showSendTo;
        _saveCopies = saveCopies;

        SuspendLayout();
        Text = "Share from MultiExplorer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(620, 500);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(24, 20, 24, 20),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Name = "shareHeading",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Regular),
            Text = _files.Length == 1 ? "Share 1 file" : $"Share {_files.Length} files",
            Margin = new Padding(0, 0, 0, 4),
        };
        var introduction = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "Choose how you want to share the selected files.",
            Margin = new Padding(0, 0, 0, 14),
        };

        var fileList = new ListView
        {
            Name = "shareFileList",
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            OwnerDraw = true,
            MultiSelect = false,
            HideSelection = false,
            Margin = new Padding(0, 0, 0, 18),
        };
        fileList.Columns.Add("Name", 280);
        fileList.Columns.Add("Folder", 360);
        fileList.Resize += (_, _) => SizeFileListColumns(fileList);
        fileList.DrawColumnHeader += DrawFileListColumnHeader;
        fileList.DrawItem += static (_, _) => { };
        fileList.DrawSubItem += DrawFileListSubItem;
        foreach (string file in _files)
        {
            var item = new ListViewItem(Path.GetFileName(file));
            item.SubItems.Add(Path.GetDirectoryName(file) ?? string.Empty);
            fileList.Items.Add(item);
        }

        var actionHeading = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "Share using",
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };

        _actions = new TableLayoutPanel
        {
            Name = "shareActions",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty,
        };
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int row = 0; row < _actions.RowCount; row++)
            _actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Button email = CreateActionButton("shareEmail", "Email with attachments",
            "Create a new message in your default desktop email application.");
        email.Click += EmailClicked;
        Button sendTo = CreateActionButton("shareSendTo", "Send to…",
            "Use destinations and programs from the Windows Send to menu.");
        sendTo.Click += (_, _) => _showSendTo(sendTo, _files);
        Button copyFiles = CreateActionButton("shareCopyFiles", "Copy files",
            "Copy the files so you can paste them into an email or another application.");
        copyFiles.Click += CopyFilesClicked;
        Button copyPaths = CreateActionButton("shareCopyPaths", "Copy file paths",
            "Copy the full paths as text.");
        copyPaths.Click += CopyPathsClicked;
        Button saveCopiesButton = CreateActionButton("shareSaveCopies", "Save copies…",
            "Copy the files to another folder, drive or cloud-synchronised location.");
        saveCopiesButton.Click += SaveCopiesClicked;

        _actions.Controls.Add(email, 0, 0);
        _actions.Controls.Add(sendTo, 1, 0);
        _actions.Controls.Add(copyFiles, 0, 1);
        _actions.Controls.Add(copyPaths, 1, 1);
        _actions.Controls.Add(saveCopiesButton, 0, 2);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 14, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status = new Label
        {
            Name = "shareStatus",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ThemeManager.MutedText,
            Margin = new Padding(0, 0, 12, 0),
        };
        _closeButton = new RoundedButton
        {
            Name = "shareClose",
            Text = "Close",
            AutoSize = true,
            MinimumSize = new Size(110, 38),
            DialogResult = DialogResult.Cancel,
            Margin = Padding.Empty,
        };
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_closeButton, 1, 0);

        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(introduction, 0, 1);
        root.Controls.Add(fileList, 0, 2);
        root.Controls.Add(actionHeading, 0, 3);
        root.Controls.Add(_actions, 0, 4);
        root.Controls.Add(footer, 0, 5);
        Controls.Add(root);

        CancelButton = _closeButton;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ThemeManager.ApplyTo(this);
        _status.ForeColor = ThemeManager.MutedText;
        Shown += (_, _) =>
        {
            SizeFileListColumns(fileList);
            ThemeManager.ApplyNativeWindow(Handle);
        };
        ResumeLayout(performLayout: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private Button CreateActionButton(string name, string text, string description)
    {
        var button = new RoundedButton
        {
            Name = name,
            Text = text,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(250, 46),
            Margin = new Padding(5),
            AccessibleDescription = description,
        };
        _toolTip.SetToolTip(button, description);
        return button;
    }

    private static void DrawFileListColumnHeader(object? sender,
        DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(ThemeManager.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        using var border = new Pen(ThemeManager.Border);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1,
            e.Bounds.Right, e.Bounds.Bottom - 1);
        e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top,
            e.Bounds.Right - 1, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty,
            SystemFonts.MessageBoxFont, Rectangle.Inflate(e.Bounds, -8, 0),
            ThemeManager.Text, TextFormatFlags.Left
                               | TextFormatFlags.VerticalCenter
                               | TextFormatFlags.SingleLine
                               | TextFormatFlags.EndEllipsis);
    }

    private static void SizeFileListColumns(ListView list)
    {
        if (list.Columns.Count < 2 || list.ClientSize.Width <= 0) return;
        int availableWidth = Math.Max(2, list.ClientSize.Width - 2);
        int nameWidth = Math.Max(160, (int)Math.Round(availableWidth * 0.42));
        list.Columns[0].Width = Math.Min(nameWidth, availableWidth - 1);
        list.Columns[1].Width = Math.Max(1,
            availableWidth - list.Columns[0].Width);
    }

    private static void DrawFileListSubItem(object? sender,
        DrawListViewSubItemEventArgs e)
    {
        ListViewItem? item = e.Item;
        bool selected = item?.Selected == true;
        Color backgroundColor = selected
            ? ThemeManager.ActiveSelection : ThemeManager.Window;
        Color textColor = selected ? Color.White : ThemeManager.Text;
        using var background = new SolidBrush(backgroundColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty,
            item?.Font ?? SystemFonts.MessageBoxFont,
            Rectangle.Inflate(e.Bounds, -6, 0), textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                 | TextFormatFlags.SingleLine
                                 | TextFormatFlags.EndEllipsis
                                 | TextFormatFlags.NoPrefix);
    }

    private async void EmailClicked(object? sender, EventArgs e)
    {
        SetBusy(true, "Opening your email application…");
        try
        {
            EmailShareResult result = await _sharingService.ComposeEmailAsync(
                Handle, _files, _lifetimeCancellation.Token);
            if (IsDisposed) return;

            switch (result.Status)
            {
                case EmailShareStatus.Completed:
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
                case EmailShareStatus.Cancelled:
                    SetStatus("Email sharing was cancelled.");
                    break;
                case EmailShareStatus.Unavailable:
                    ShowEmailUnavailable();
                    break;
                default:
                    ShowEmailFailure(result.ErrorCode);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private async void CopyFilesClicked(object? sender, EventArgs e)
    {
        await CopyToClipboardAsync(copyPaths: false);
    }

    private async void CopyPathsClicked(object? sender, EventArgs e)
    {
        await CopyToClipboardAsync(copyPaths: true);
    }

    private async Task CopyToClipboardAsync(bool copyPaths)
    {
        SetBusy(true, copyPaths ? "Copying file paths…" : "Copying files…");
        try
        {
            bool copied = copyPaths
                ? await _sharingService.CopyPathsAsync(_files,
                    _lifetimeCancellation.Token)
                : await _sharingService.CopyFilesAsync(_files,
                    _lifetimeCancellation.Token);
            if (IsDisposed) return;
            SetStatus(copied
                ? copyPaths
                    ? "File paths copied. You can now paste them into another application."
                    : "Files copied. You can now paste them into another application."
                : "MultiExplorer could not copy the selected files.");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SaveCopiesClicked(object? sender, EventArgs e)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose where MultiExplorer should copy the selected files.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Path.GetDirectoryName(_files[0]) ?? string.Empty,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        if (_saveCopies(_files, picker.SelectedPath))
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        SetStatus("MultiExplorer could not start the copy operation.");
    }

    private void ShowEmailUnavailable()
    {
        MessageBox.Show(this,
            "Your default email application does not support creating a message " +
            "with file attachments from another program. You can use Copy files " +
            "and paste them into the message instead.",
            "Email is not available", MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        SetStatus("The default email application cannot accept file attachments.");
    }

    private void ShowEmailFailure(int errorCode)
    {
        string details = errorCode == 0 ? string.Empty : $" (error {errorCode})";
        MessageBox.Show(this,
            "MultiExplorer could not create the email message" + details + ". " +
            "You can use Copy files and paste them into the message instead.",
            "Email could not be opened", MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        SetStatus("The email message could not be created.");
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _actions.Enabled = !busy;
        _closeButton.Enabled = !busy;
        UseWaitCursor = busy;
        if (message is not null) SetStatus(message);
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
        _status.ForeColor = ThemeManager.MutedText;
    }
}
