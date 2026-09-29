using System.ComponentModel;
using System.Text;

namespace MultiExplorer;

internal sealed class FolderCompareDialog : Form
{
    private const int MaximumDisplayedMatches = 1000;
    private const int RelativePathColumnIndex = 0;
    private const int SuggestedActionColumnIndex = 4;
    private const int ChosenActionColumnIndex = 5;
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly Func<FileOperationKind, IEnumerable<string>, string?, Guid?>
        _startOperation;
    private readonly DataGridView _grid;
    private readonly Label _status;
    private readonly RoundedButton _suggested;
    private readonly RoundedButton _skipAll;
    private readonly RoundedButton _rescan;
    private readonly RoundedButton _apply;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private FolderComparisonResult? _comparison;
    private CancellationTokenSource? _scanCancellation;
    private bool _updatingRows;

    internal FolderCompareDialog(string leftRoot, string rightRoot,
        Func<FileOperationKind, IEnumerable<string>, string?, Guid?>
            startOperation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightRoot);
        ArgumentNullException.ThrowIfNull(startOperation);
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        _startOperation = startOperation;

        SuspendLayout();
        Text = "Compare panes";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimumSize = new Size(850, 480);
        ClientSize = new Size(1100, 650);
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var paths = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = $"Left:  {_leftRoot}{Environment.NewLine}Right: {_rightRoot}",
            Margin = new Padding(0, 0, 0, 10),
        };
        var explanation = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "Suggestions copy missing items or files newer by over 2 seconds, even when sizes differ. Chosen actions start with suggestions. Includes subfolders; contents are not hashed; links cannot sync.",
            Margin = new Padding(0, 0, 0, 12),
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10),
        };
        _suggested = new RoundedButton
        {
            Name = "compareSelectSuggested",
            Text = "Restore suggestions",
            AutoSize = true,
            MinimumSize = new Size(190, 38),
        };
        _skipAll = new RoundedButton
        {
            Name = "compareSkipAll",
            Text = "Skip all",
            AutoSize = true,
            MinimumSize = new Size(110, 38),
        };
        _rescan = new RoundedButton
        {
            Name = "compareAgain",
            Text = "Compare again",
            AutoSize = true,
            MinimumSize = new Size(135, 38),
        };
        _suggested.Click += (_, _) => SelectSuggested();
        _skipAll.Click += (_, _) => SetAllActions(FolderSyncAction.Skip);
        _rescan.Click += async (_, _) => await ScanAsync();
        actions.Controls.AddRange([_suggested, _skipAll, _rescan]);

        _grid = new DataGridView
        {
            Name = "comparisonGrid",
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            RowHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EditMode = DataGridViewEditMode.EditOnEnter,
            Margin = new Padding(0, 0, 0, 12),
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Relative path", ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 26,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Difference", ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 14,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Left", ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 15,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Right", ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 15,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Suggested action", ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 15,
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Chosen action",
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 15,
            FlatStyle = FlatStyle.Flat,
        });
        _grid.CellPainting += OnGridCellPainting;
        _grid.Sorted += (_, _) => _grid.Invalidate();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == ChosenActionColumnIndex && !_updatingRows)
                UpdateStatus();
        };

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status = new Label
        {
            Name = "comparisonStatus",
            Dock = DockStyle.Fill,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Waiting to compare...",
            Margin = new Padding(0, 0, 10, 0),
        };
        _apply = new RoundedButton
        {
            Name = "compareReviewActions",
            Text = "Review actions...",
            AutoSize = true,
            MinimumSize = new Size(150, 38),
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 3, 0, 3),
        };
        var close = new RoundedButton
        {
            Name = "compareClose",
            Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel,
            MinimumSize = new Size(110, 38),
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 3, 0, 3),
        };
        _apply.Click += async (_, _) => await ApplyAsync();
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_apply, 1, 0);
        footer.Controls.Add(close, 2, 0);

        layout.Controls.Add(paths, 0, 0);
        layout.Controls.Add(explanation, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(_grid, 0, 3);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);
        CancelButton = close;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ThemeManager.ApplyTo(this);
        ApplyGridTheme();
        UpdateGridMetrics();
        SetBusy(true);
        Shown += async (_, _) =>
        {
            UpdateGridMetrics();
            ThemeManager.ApplyNativeWindow(Handle);
            await ScanAsync();
        };
        FormClosing += (_, _) =>
        {
            _scanCancellation?.Cancel();
            _lifetimeCancellation.Cancel();
        };
        Disposed += (_, _) => _lifetimeCancellation.Dispose();
        ResumeLayout(performLayout: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateGridMetrics();
    }

    private void UpdateGridMetrics()
    {
        // DataGridView rows and header heights are not child controls, so they
        // need explicit sizes when the dialog moves to a monitor with a new DPI.
        int inset = Math.Max(1, (int)Math.Round(12f * _grid.DeviceDpi / 96f));
        int rowHeight = _grid.Font.Height + inset;
        _grid.RowTemplate.Height = rowHeight;
        _grid.ColumnHeadersHeight = rowHeight + inset / 2;
        foreach (DataGridViewRow row in _grid.Rows)
            row.Height = rowHeight;
    }

    private async Task ScanAsync()
    {
        if (_scanCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _comparison = null;
        _grid.Rows.Clear();
        SetBusy(true);
        _status.Text = "Comparing folders...";
        try
        {
            FolderComparisonResult result = await FolderComparisonService.CompareAsync(
                _leftRoot, _rightRoot, cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            ShowComparisonResult(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsDisposed) _status.Text = "Comparison cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidOperationException)
        {
            AppLog.Debug(ex, nameof(FolderCompareDialog),
                "Could not compare both folders completely.");
            if (!IsDisposed)
                _status.Text = $"Comparison stopped: {ex.Message}";
        }
        finally
        {
            _scanCancellation = null;
            if (!IsDisposed) SetBusy(false);
        }
    }

    internal void ShowComparisonResult(FolderComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _comparison = result;
        _grid.Rows.Clear();
        _grid.SuspendLayout();
        _updatingRows = true;
        try
        {
            foreach (FolderDifference difference in result.Differences)
                AddDifferenceRow(difference);
            foreach ((FolderItemSnapshot left, FolderItemSnapshot right)
                     in FolderComparisonService.GetMatchingFiles(result,
                         MaximumDisplayedMatches))
                AddMatchingRow(left, right);
            if (_grid.Rows.Count > 1)
                _grid.Sort(_grid.Columns[RelativePathColumnIndex],
                    ListSortDirection.Ascending);
        }
        finally
        {
            _updatingRows = false;
            _grid.ResumeLayout();
        }
        UpdateStatus();
        SetBusy(false);
    }

    private void AddDifferenceRow(FolderDifference difference)
    {
        int index = _grid.Rows.Add(
            difference.RelativePath,
            DifferenceLabel(difference.Kind),
            ItemLabel(difference.Left),
            ItemLabel(difference.Right));
        DataGridViewRow row = _grid.Rows[index];
        row.Tag = difference;
        FolderSyncAction suggestedAction = difference.SuggestedAction;
        row.Cells[SuggestedActionColumnIndex].Value =
            ActionLabel(suggestedAction);
        var actionCell = (DataGridViewComboBoxCell)row.Cells[ChosenActionColumnIndex];
        foreach (FolderSyncAction action in Enum.GetValues<FolderSyncAction>())
        {
            if (difference.Allows(action))
                actionCell.Items.Add(ActionLabel(action));
        }
        actionCell.Value = ActionLabel(suggestedAction);
    }

    private void AddMatchingRow(FolderItemSnapshot left,
        FolderItemSnapshot right)
    {
        int index = _grid.Rows.Add(left.RelativePath, "Same size/time",
            ItemLabel(left), ItemLabel(right));
        DataGridViewRow row = _grid.Rows[index];
        row.ReadOnly = true;
        row.DefaultCellStyle.ForeColor = ThemeManager.MutedText;
    }

    private void SelectSuggested()
    {
        _updatingRows = true;
        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is FolderDifference difference)
                    row.Cells[ChosenActionColumnIndex].Value =
                        ActionLabel(difference.SuggestedAction);
            }
        }
        finally { _updatingRows = false; }
        UpdateStatus();
    }

    private void SetAllActions(FolderSyncAction action)
    {
        _updatingRows = true;
        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is FolderDifference)
                    row.Cells[ChosenActionColumnIndex].Value = ActionLabel(action);
            }
        }
        finally { _updatingRows = false; }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int selected = GetSelections().Count(selection =>
            selection.Action != FolderSyncAction.Skip);
        _status.Text = _comparison is { } comparison
            ? FormatComparisonStatus(comparison, selected,
                _grid.Rows.Count - comparison.Differences.Count)
            : "Comparison has not completed.";
        _apply.Enabled = _comparison is not null && selected > 0;
    }

    internal static string FormatComparisonStatus(
        FolderComparisonResult comparison, int selectedActions,
        int displayedMatches = -1)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentOutOfRangeException.ThrowIfNegative(selectedActions);

        int leftFiles = comparison.LeftItems.Values.Count(
            static item => !item.IsDirectory);
        int rightFiles = comparison.RightItems.Values.Count(
            static item => !item.IsDirectory);
        string matches = displayedMatches >= 0
            ? $"{displayedMatches:N0} matches shown; "
            : string.Empty;
        if (comparison.Differences.Count == 0)
            return leftFiles == rightFiles
                ? $"Compared {leftFiles:N0} files per pane; {matches}no differences."
                : $"Compared {leftFiles:N0} left / {rightFiles:N0} right files; {matches}no differences.";

        string differenceLabel = comparison.Differences.Count == 1
            ? "difference" : "differences";
        return $"Compared {leftFiles:N0} left / {rightFiles:N0} right files; "
            + $"{matches}{comparison.Differences.Count:N0} {differenceLabel}; {selectedActions:N0} selected.";
    }

    private IReadOnlyList<FolderSyncSelection> GetSelections()
    {
        return _grid.Rows.Cast<DataGridViewRow>()
            .Where(static row => row.Tag is FolderDifference)
            .Select(row => new FolderSyncSelection(
                (FolderDifference)row.Tag!,
                ParseAction(Convert.ToString(
                    row.Cells[ChosenActionColumnIndex].Value))))
            .ToArray();
    }

    private async Task ApplyAsync()
    {
        if (_comparison is null) return;
        _grid.EndEdit();
        IReadOnlyList<FolderSyncSelection> selections = GetSelections();
        IReadOnlyList<FolderSyncPlanItem> plan;
        try
        {
            plan = FolderComparisonService.BuildPlan(_comparison, selections);
        }
        catch (ArgumentException ex)
        {
            _status.Text = ex.Message;
            return;
        }
        if (plan.Count == 0) return;

        using (var review = new FolderSyncReviewDialog(plan))
        {
            if (review.ShowDialog(this) != DialogResult.OK) return;
        }

        int deleteCount = plan.Count(static item => item.Action is
            FolderSyncAction.DeleteLeft or FolderSyncAction.DeleteRight);
        if (deleteCount > 0
            && MessageBox.Show(this,
                $"Delete {deleteCount:N0} selected item(s)? Deletions on network shares may be permanent.",
                "Confirm folder comparison deletions",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        SetBusy(true);
        _status.Text = "Checking that the compared items have not changed...";
        try
        {
            await FolderComparisonService.ValidatePlanAsync(
                _comparison, selections, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException)
        {
            if (!IsDisposed)
            {
                _status.Text = ex.Message;
                SetBusy(false);
            }
            return;
        }
        if (IsDisposed || _lifetimeCancellation.IsCancellationRequested) return;

        string[] deletions = plan
            .Where(static item => item.Action is FolderSyncAction.DeleteLeft
                or FolderSyncAction.DeleteRight)
            .Select(static item => item.SourcePath)
            .ToArray();
        if (deletions.Length > 0
            && _startOperation(FileOperationKind.Delete, deletions, null) is null)
        {
            _status.Text = "Delete operation was not started; no copy operations were started.";
            SetBusy(false);
            return;
        }

        int started = deletions.Length > 0 ? 1 : 0;
        int failed = 0;
        foreach (IGrouping<string, FolderSyncPlanItem> group in plan
                     .Where(static item => item.DestinationDirectory is not null)
                     .GroupBy(static item => item.DestinationDirectory!,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (_startOperation(FileOperationKind.Copy,
                    group.Select(static item => item.SourcePath), group.Key) is null)
                failed++;
            else
                started++;
        }

        if (failed > 0)
        {
            _status.Text = $"Started {started} operation groups; {failed} could not start. Check the log, then compare again.";
            SetBusy(false);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetBusy(bool busy)
    {
        _suggested.Enabled = !busy && _comparison?.Differences.Count > 0;
        _skipAll.Enabled = !busy && _comparison?.Differences.Count > 0;
        _rescan.Enabled = !busy;
        _grid.Enabled = !busy;
        _apply.Enabled = !busy && _comparison is not null
            && GetSelections().Any(static selection =>
                selection.Action != FolderSyncAction.Skip);
    }

    private void ApplyGridTheme()
    {
        _grid.BackgroundColor = ThemeManager.Window;
        _grid.GridColor = ThemeManager.Border;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = ThemeManager.Surface;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = ThemeManager.Text;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = ThemeManager.Surface;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = ThemeManager.Text;
        _grid.DefaultCellStyle.BackColor = ThemeManager.Window;
        _grid.DefaultCellStyle.ForeColor = ThemeManager.Text;
        _grid.DefaultCellStyle.SelectionBackColor = ThemeManager.ActiveSelection;
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = ThemeManager.Surface;
    }

    private void OnGridCellPainting(object? sender,
        DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex != -1 || e.ColumnIndex < 0
            || e.Graphics is not { } graphics) return;

        Rectangle bounds = e.CellBounds;
        using (var background = new SolidBrush(ThemeManager.Surface))
            graphics.FillRectangle(background, bounds);
        using (var border = new Pen(ThemeManager.Border))
        {
            graphics.DrawLine(border, bounds.Left, bounds.Bottom - 1,
                bounds.Right - 1, bounds.Bottom - 1);
            graphics.DrawLine(border, bounds.Right - 1, bounds.Top,
                bounds.Right - 1, bounds.Bottom - 1);
        }

        int inset = Math.Max(4, (int)Math.Round(9f * _grid.DeviceDpi / 96f));
        int glyphWidth = Math.Max(7, (int)Math.Round(9f * _grid.DeviceDpi / 96f));
        int glyphHeight = Math.Max(5, (int)Math.Round(6f * _grid.DeviceDpi / 96f));
        SortOrder direction = _grid.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection;
        int glyphSpace = direction == SortOrder.None ? 0 : glyphWidth + inset;
        var textBounds = new Rectangle(bounds.Left + inset, bounds.Top,
            Math.Max(0, bounds.Width - inset * 2 - glyphSpace), bounds.Height);
        TextRenderer.DrawText(graphics,
            Convert.ToString(e.FormattedValue) ?? string.Empty,
            e.CellStyle?.Font ?? _grid.Font, textBounds, ThemeManager.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix);

        if (direction != SortOrder.None)
        {
            int centerX = bounds.Right - inset - glyphWidth / 2;
            int centerY = bounds.Top + bounds.Height / 2;
            int halfWidth = glyphWidth / 2;
            int halfHeight = glyphHeight / 2;
            Point[] triangle = direction == SortOrder.Ascending
                ? [new(centerX, centerY - halfHeight),
                    new(centerX - halfWidth, centerY + halfHeight),
                    new(centerX + halfWidth, centerY + halfHeight)]
                : [new(centerX - halfWidth, centerY - halfHeight),
                    new(centerX + halfWidth, centerY - halfHeight),
                    new(centerX, centerY + halfHeight)];
            using var glyph = new SolidBrush(ThemeManager.AccentText);
            graphics.FillPolygon(glyph, triangle);
        }

        e.Handled = true;
    }

    private static string DifferenceLabel(FolderDifferenceKind kind) => kind switch
    {
        FolderDifferenceKind.OnlyLeft => "Left only",
        FolderDifferenceKind.OnlyRight => "Right only",
        FolderDifferenceKind.LeftNewer => "Left newer",
        FolderDifferenceKind.RightNewer => "Right newer",
        FolderDifferenceKind.DifferentSize => "Different size",
        FolderDifferenceKind.TypeConflict => "File/folder conflict",
        FolderDifferenceKind.ReparsePoint => "Link (skipped)",
        _ => "Different",
    };

    private static string ItemLabel(FolderItemSnapshot? item) => item switch
    {
        null => "—",
        { IsReparsePoint: true } => "Link",
        { IsDirectory: true } => "Folder",
        _ => $"{item.Length:N0} B  ·  {item.LastWriteTimeUtc.ToLocalTime():g}",
    };

    private static string ActionLabel(FolderSyncAction action) => action switch
    {
        FolderSyncAction.Skip => "Skip",
        FolderSyncAction.CopyLeftToRight => "Copy left → right",
        FolderSyncAction.CopyRightToLeft => "Copy right → left",
        FolderSyncAction.DeleteLeft => "Delete left",
        FolderSyncAction.DeleteRight => "Delete right",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static FolderSyncAction ParseAction(string? label) => label switch
    {
        "Copy left → right" => FolderSyncAction.CopyLeftToRight,
        "Copy right → left" => FolderSyncAction.CopyRightToLeft,
        "Delete left" => FolderSyncAction.DeleteLeft,
        "Delete right" => FolderSyncAction.DeleteRight,
        _ => FolderSyncAction.Skip,
    };
}

internal sealed class FolderSyncReviewDialog : Form
{
    internal FolderSyncReviewDialog(IReadOnlyList<FolderSyncPlanItem> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SuspendLayout();
        Text = "Review folder changes";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimumSize = new Size(680, 400);
        ClientSize = new Size(900, 560);
        Font = SystemFonts.MessageBoxFont;

        int copies = plan.Count(static item => item.Action is
            FolderSyncAction.CopyLeftToRight or FolderSyncAction.CopyRightToLeft);
        int deletions = plan.Count - copies;
        var text = new StringBuilder()
            .Append(copies).Append(" copy item(s), ")
            .Append(deletions).AppendLine(" delete item(s)")
            .AppendLine();
        if (deletions > 0)
            text.AppendLine("Deletion on network shares may be permanent. Review every path below.")
                .AppendLine();
        foreach (FolderSyncPlanItem item in plan)
        {
            text.Append(item.Action switch
                {
                    FolderSyncAction.CopyLeftToRight => "COPY LEFT → RIGHT",
                    FolderSyncAction.CopyRightToLeft => "COPY RIGHT → LEFT",
                    FolderSyncAction.DeleteLeft => "DELETE LEFT",
                    FolderSyncAction.DeleteRight => "DELETE RIGHT",
                    _ => "SKIP",
                })
                .Append("  ").AppendLine(item.SourcePath);
            if (item.DestinationDirectory is not null)
                text.Append("    to ").AppendLine(item.TargetPath);
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(18),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var preview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = text.ToString(),
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0),
        };
        var start = new RoundedButton
        {
            Name = "reviewStartOperations",
            Text = "Start operations", AutoSize = true,
            MinimumSize = new Size(150, 38),
            DialogResult = DialogResult.OK,
        };
        var cancel = new RoundedButton
        {
            Name = "reviewCancel",
            Text = "Cancel", AutoSize = true,
            MinimumSize = new Size(110, 38),
            DialogResult = DialogResult.Cancel,
        };
        buttons.Controls.Add(start);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(preview, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
        AcceptButton = start;
        CancelButton = cancel;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ThemeManager.ApplyTo(this);
        Shown += (_, _) => ThemeManager.ApplyNativeWindow(Handle);
        ResumeLayout(performLayout: true);
    }
}
