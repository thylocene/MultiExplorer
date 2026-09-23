using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace MultiExplorer;

internal sealed class OperationProgressForm : Form
{
    private const int WindowClientWidth = 560;
    private const int ExpandedRowHeight = 326;
    private const int MaximumVisibleRows = 3;

    private readonly FlowLayoutPanel _rowsPanel;
    private readonly Dictionary<Guid, OperationProgressRow> _rows = [];
    private readonly HashSet<Guid> _dismissedOperations = [];
    private OperationWindowPlacement? _placement;

    internal event EventHandler<Guid>? CancellationRequested;
    internal event EventHandler<Guid>? PauseRequested;
    internal event EventHandler<Guid>? ResumeRequested;

    internal OperationProgressForm()
    {
        Text = "0% complete";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        _rowsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = SystemColors.Window,
            Padding = Padding.Empty,
        };
        _rowsPanel.Resize += (_, _) => ResizeRows();

        Controls.Add(_rowsPanel);
        ClientSize = new Size(WindowClientWidth, ExpandedRowHeight);
    }

    protected override bool ShowWithoutActivation => true;

    internal void UpdateOperations(
        IReadOnlyList<FileOperationState> operations,
        OperationWindowPlacement? placement)
    {
        ArgumentNullException.ThrowIfNull(operations);
        int previousRowCount = _rows.Count;
        bool placementChanged = placement is not null
            && !ReferenceEquals(_placement, placement);
        if (placement is not null)
            _placement = placement;

        FileOperationState[] orderedOperations = operations
            .OrderBy(static state => state.CreatedUtc)
            .ThenBy(static state => state.Id)
            .ToArray();
        HashSet<Guid> activeIds = operations.Select(static state => state.Id).ToHashSet();
        foreach (Guid removedId in _rows.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            OperationProgressRow row = _rows[removedId];
            _rows.Remove(removedId);
            _rowsPanel.Controls.Remove(row);
            row.Dispose();
            _dismissedOperations.Remove(removedId);
        }

        bool addedOperation = false;
        for (int index = 0; index < orderedOperations.Length; index++)
        {
            FileOperationState state = orderedOperations[index];
            if (!_rows.TryGetValue(state.Id, out OperationProgressRow? row))
            {
                row = new OperationProgressRow(state.Id);
                row.CancellationRequested += OnRowCancellationRequested;
                row.PauseRequested += OnRowPauseRequested;
                row.ResumeRequested += OnRowResumeRequested;
                row.DetailsVisibilityChanged += OnRowDetailsVisibilityChanged;
                _rows.Add(state.Id, row);
                _rowsPanel.Controls.Add(row);
                addedOperation = true;
            }

            row.UpdateState(state);
            _rowsPanel.Controls.SetChildIndex(row, index);
        }

        UpdateWindowTitle(orderedOperations);
        if (operations.Count == 0)
        {
            Hide();
            _dismissedOperations.Clear();
            _placement = null;
            return;
        }

        if (_rows.Count != previousRowCount || placementChanged)
            UpdateWindowSizeAndPosition(reposition: true);
        if (addedOperation)
            _dismissedOperations.Clear();
        if (!Visible && operations.Any(state => !_dismissedOperations.Contains(state.Id)))
            Show();
    }

    private void UpdateWindowTitle(IReadOnlyList<FileOperationState> operations)
    {
        if (operations.Count == 0)
        {
            Text = "File operations";
            return;
        }

        int percentage = (int)Math.Round(operations.Average(
            static state => OperationProgressMath.CalculatePercentage(state)));
        Text = operations.All(static state => state.Status == FileOperationStatus.Paused)
            ? $"Paused - {percentage}% complete"
            : $"{percentage}% complete";
    }

    private void OnRowCancellationRequested(object? sender, EventArgs e)
    {
        if (sender is OperationProgressRow row)
            CancellationRequested?.Invoke(this, row.OperationId);
    }

    private void OnRowPauseRequested(object? sender, EventArgs e)
    {
        if (sender is OperationProgressRow row)
            PauseRequested?.Invoke(this, row.OperationId);
    }

    private void OnRowResumeRequested(object? sender, EventArgs e)
    {
        if (sender is OperationProgressRow row)
            ResumeRequested?.Invoke(this, row.OperationId);
    }

    private void OnRowDetailsVisibilityChanged(object? sender, EventArgs e) =>
        UpdateWindowSizeAndPosition(reposition: false);

    private void UpdateWindowSizeAndPosition(bool reposition)
    {
        OperationProgressRow[] orderedRows = _rowsPanel.Controls
            .OfType<OperationProgressRow>()
            .Take(MaximumVisibleRows)
            .ToArray();
        int clientHeight = orderedRows.Length == 0
            ? LogicalToDeviceUnits(ExpandedRowHeight)
            : orderedRows.Sum(static row => row.Height);
        int nonClientHeight = Math.Max(0, Height - ClientSize.Height);
        if (_placement is { WorkAreaHeight: > 0 })
        {
            int maximumClientHeight = Math.Max(
                LogicalToDeviceUnits(124),
                _placement.WorkAreaHeight - nonClientHeight - LogicalToDeviceUnits(8));
            clientHeight = Math.Min(clientHeight, maximumClientHeight);
        }
        ClientSize = new Size(LogicalToDeviceUnits(WindowClientWidth), clientHeight);
        ResizeRows();

        if (reposition && _placement is not null
            && _placement.TryCalculateLocation(Width, Height, out int x, out int y))
            Location = new Point(x, y);
    }

    private void ResizeRows()
    {
        bool requiresScrollBar = _rows.Values.Sum(static row => row.Height)
            > _rowsPanel.ClientSize.Height;
        int availableWidth = Math.Max(1, _rowsPanel.ClientSize.Width
            - (requiresScrollBar ? SystemInformation.VerticalScrollBarWidth : 0));
        foreach (OperationProgressRow row in _rows.Values)
            row.Width = availableWidth;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _dismissedOperations.UnionWith(_rows.Keys);
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }
}

internal sealed class OperationProgressRow : UserControl
{
    private const int ExpandedHeight = 326;
    private const int CollapsedHeight = 124;

    private readonly LinkLabel _summary;
    private readonly Label _percentage;
    private readonly OperationPerformanceGraph _performanceGraph;
    private readonly Label _currentItem;
    private readonly Label _timeRemaining;
    private readonly Label _itemsRemaining;
    private readonly OperationCommandButton _pause;
    private readonly OperationCommandButton _cancel;
    private readonly Panel _detailsSeparator;
    private readonly OperationDetailsToggle _detailsToggle;
    private readonly ToolTip _toolTip = new();
    private long _lastMeasurementTick;
    private long _lastProgressTick;
    private double _lastMeasurement;
    private ProgressMeasurement _measurementKind;
    private FileOperationStatus _lastStatus;
    private int _maximumDisplayedPercentage;
    private bool _wasPaused;
    private bool _expanded = true;

    internal Guid OperationId { get; }
    internal event EventHandler? CancellationRequested;
    internal event EventHandler? PauseRequested;
    internal event EventHandler? ResumeRequested;
    internal event EventHandler? DetailsVisibilityChanged;

    internal OperationProgressRow(Guid operationId)
    {
        OperationId = operationId;
        AutoScaleMode = AutoScaleMode.None;
        Height = ExpandedHeight;
        Margin = Padding.Empty;
        Padding = new Padding(34, 0, 34, 0);
        BackColor = SystemColors.Window;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _summary = new LinkLabel
        {
            AutoEllipsis = true,
            LinkBehavior = LinkBehavior.NeverUnderline,
            LinkColor = Color.FromArgb(0, 102, 204),
            ActiveLinkColor = Color.FromArgb(0, 82, 164),
            VisitedLinkColor = Color.FromArgb(0, 102, 204),
            UseMnemonic = false,
            Location = new Point(34, 14),
            Height = 21,
            TabStop = false,
        };
        _percentage = new Label
        {
            AutoEllipsis = true,
            UseMnemonic = false,
            Font = new Font("Segoe UI", 13F, FontStyle.Regular, GraphicsUnit.Point),
            Location = new Point(34, 40),
            Height = 31,
        };
        _performanceGraph = new OperationPerformanceGraph
        {
            Location = new Point(34, 77),
            Height = 101,
        };
        _currentItem = CreateDetailLabel(193);
        _timeRemaining = CreateDetailLabel(216);
        _itemsRemaining = CreateDetailLabel(239);

        _pause = new OperationCommandButton
        {
            Command = OperationCommandIcon.Pause,
            Size = new Size(30, 30),
            AccessibleName = "Pause operation",
        };
        _pause.Click += (_, _) =>
        {
            if (_lastStatus == FileOperationStatus.Paused)
                ResumeRequested?.Invoke(this, EventArgs.Empty);
            else
                PauseRequested?.Invoke(this, EventArgs.Empty);
        };
        _cancel = new OperationCommandButton
        {
            Command = OperationCommandIcon.Cancel,
            Size = new Size(30, 30),
            AccessibleName = "Cancel operation",
        };
        _cancel.Click += (_, _) => CancellationRequested?.Invoke(this, EventArgs.Empty);
        _toolTip.SetToolTip(_pause, "Pause operation");
        _toolTip.SetToolTip(_cancel, "Cancel operation");

        _detailsSeparator = new Panel
        {
            Location = new Point(0, 273),
            Height = 1,
            BackColor = Color.FromArgb(225, 225, 225),
        };
        _detailsToggle = new OperationDetailsToggle
        {
            Expanded = true,
            Location = new Point(34, 274),
            Height = 51,
        };
        _detailsToggle.Click += (_, _) => SetExpanded(!_expanded);

        Controls.AddRange([
            _summary,
            _percentage,
            _performanceGraph,
            _currentItem,
            _timeRemaining,
            _itemsRemaining,
            _pause,
            _cancel,
            _detailsSeparator,
            _detailsToggle,
        ]);
        Resize += (_, _) => LayoutControls();
        LayoutControls();
    }

    private static Label CreateDetailLabel(int top) => new()
    {
        AutoEllipsis = true,
        UseMnemonic = false,
        Location = new Point(34, top),
        Height = 21,
    };

    internal void UpdateState(FileOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        UpdateSummary(state);

        int percentage = OperationProgressMath.CalculatePercentage(state);
        _maximumDisplayedPercentage = Math.Max(_maximumDisplayedPercentage, percentage);
        percentage = _maximumDisplayedPercentage;
        _percentage.Text = state.Status switch
        {
            FileOperationStatus.Paused => $"Paused - {percentage}% complete",
            FileOperationStatus.Cancelling => $"Cancelling - {percentage}% complete",
            _ => $"{percentage}% complete",
        };
        _performanceGraph.SetCompletionPercentage(percentage);

        string currentItem = string.IsNullOrWhiteSpace(state.CurrentItem)
            ? "Preparing…"
            : Path.GetFileName(state.CurrentItem);
        _currentItem.Text = $"Name: {currentItem}";
        _toolTip.SetToolTip(_currentItem, state.CurrentItem);

        UpdatePerformance(state);
        UpdateRemainingDetails(state);

        _cancel.Enabled = state.Status is FileOperationStatus.Queued
            or FileOperationStatus.Running or FileOperationStatus.Paused;
        _pause.Enabled = state.Status is FileOperationStatus.Running
            or FileOperationStatus.Paused;
        bool paused = state.Status == FileOperationStatus.Paused;
        _pause.Command = paused
            ? OperationCommandIcon.Resume
            : OperationCommandIcon.Pause;
        _pause.AccessibleName = paused ? "Resume operation" : "Pause operation";
        _toolTip.SetToolTip(_pause, paused ? "Resume operation" : "Pause operation");
        _lastStatus = state.Status;
    }

    private void UpdateSummary(FileOperationState state)
    {
        string action = state.Kind switch
        {
            FileOperationKind.Copy => "Copying",
            FileOperationKind.Move => "Moving",
            FileOperationKind.Delete => "Deleting",
            FileOperationKind.DeletePermanently => "Permanently deleting",
            _ => "Processing",
        };
        string source = state.SourceDisplayName ?? "selected location";
        string count = state.TotalItems.ToString("N0", CultureInfo.CurrentCulture);
        string itemWord = state.TotalItems == 1 ? "item" : "items";
        string prefix = $"{action} {count} {itemWord} from ";
        string destination = state.DestinationDisplayName ?? string.Empty;
        string suffix = state.Kind is FileOperationKind.Copy or FileOperationKind.Move
            && destination.Length > 0
                ? $" to {destination}"
                : string.Empty;

        _summary.Text = prefix + source + suffix;
        _summary.Links.Clear();
        _summary.Links.Add(prefix.Length, source.Length);
        if (suffix.Length > 0)
        {
            int destinationStart = prefix.Length + source.Length + 4;
            _summary.Links.Add(destinationStart, destination.Length);
        }
    }

    private void UpdatePerformance(FileOperationState state)
    {
        ProgressMeasurement measurementKind = state.TotalBytes > 0
            && state.Kind is FileOperationKind.Copy or FileOperationKind.Move
                ? ProgressMeasurement.Bytes
                : ProgressMeasurement.Items;
        double measurement = measurementKind == ProgressMeasurement.Bytes
            ? state.BytesCompleted
            : state.CompletedItems;

        long now = Environment.TickCount64;
        if (state.Status == FileOperationStatus.Paused)
        {
            _wasPaused = true;
            _lastMeasurementTick = now;
            _lastProgressTick = now;
            _lastMeasurement = Math.Max(_lastMeasurement, measurement);
            _performanceGraph.SetPaused(true);
            return;
        }

        if (_wasPaused)
        {
            _wasPaused = false;
            _lastMeasurementTick = now;
            _lastProgressTick = now;
            _lastMeasurement = Math.Max(_lastMeasurement, measurement);
            _measurementKind = measurementKind;
            _performanceGraph.SetPaused(false);
            _performanceGraph.SetRate(0);
            _performanceGraph.SetRateText("Speed: Calculating…");
            return;
        }

        _performanceGraph.SetPaused(false);
        if (_lastMeasurementTick == 0 || measurementKind != _measurementKind)
        {
            _lastMeasurementTick = now;
            _lastProgressTick = now;
            _lastMeasurement = measurement;
            _measurementKind = measurementKind;
            _performanceGraph.SetRate(0);
            _performanceGraph.SetRateText("Speed: Calculating…");
            return;
        }

        measurement = Math.Max(measurement, _lastMeasurement);
        if (measurement <= _lastMeasurement)
        {
            if (now - _lastProgressTick >= 750)
            {
                _performanceGraph.SetRate(0);
                _performanceGraph.SetRateText(measurementKind == ProgressMeasurement.Bytes
                    ? "Speed: 0 B/s"
                    : "Speed: 0 items/s");
            }
            return;
        }

        long elapsedMilliseconds = Math.Max(1, now - _lastMeasurementTick);
        double rate = (measurement - _lastMeasurement) * 1000 / elapsedMilliseconds;
        _performanceGraph.SetRate(rate);
        _performanceGraph.SetRateText(measurementKind == ProgressMeasurement.Bytes
            ? $"Speed: {FormatByteRate(rate)}"
            : $"Speed: {rate:0} items/s");
        _lastMeasurementTick = now;
        _lastProgressTick = now;
        _lastMeasurement = measurement;
    }

    private void UpdateRemainingDetails(FileOperationState state)
    {
        int completedItems = state.TotalItems > 0
            ? Math.Min(state.CompletedItems, state.TotalItems)
            : state.CompletedItems;
        int remainingItems = Math.Max(0, state.TotalItems - completedItems);
        ulong remainingBytes = state.TotalBytes > state.BytesCompleted
            ? state.TotalBytes - state.BytesCompleted
            : 0;

        _itemsRemaining.Text = $"Items remaining: "
            + remainingItems.ToString("N0", CultureInfo.CurrentCulture)
            + (state.TotalBytes > 0 ? $" ({FormatByteSize(remainingBytes)})" : string.Empty);

        if (state.Status == FileOperationStatus.Paused
            && !string.IsNullOrEmpty(_timeRemaining.Text)
            && !_timeRemaining.Text.EndsWith("Calculating…", StringComparison.Ordinal))
            return;

        double rate = _performanceGraph.DisplayedRate;
        double remainingWork = _measurementKind == ProgressMeasurement.Bytes
            ? remainingBytes
            : remainingItems;
        _timeRemaining.Text = rate <= 0.01 || remainingWork <= 0
            ? remainingWork <= 0
                ? "Time remaining: About 0 seconds"
                : "Time remaining: Calculating…"
            : $"Time remaining: {FormatRemainingTime(remainingWork / rate)}";
    }

    private static string FormatRemainingTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return "Calculating…";
        if (seconds < 60)
            return $"About {Math.Max(1, (int)Math.Ceiling(seconds))} seconds";
        if (seconds < 3600)
            return $"About {Math.Max(1, (int)Math.Ceiling(seconds / 60))} minutes";

        int hours = Math.Max(1, (int)Math.Floor(seconds / 3600));
        int minutes = (int)Math.Ceiling(seconds % 3600 / 60);
        return minutes == 0
            ? $"About {hours} {(hours == 1 ? "hour" : "hours")}" 
            : $"About {hours} {(hours == 1 ? "hour" : "hours")} and {minutes} minutes";
    }

    private static string FormatByteRate(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        int unitIndex = 0;
        while (bytesPerSecond >= 1024 && unitIndex < units.Length - 1)
        {
            bytesPerSecond /= 1024;
            unitIndex++;
        }

        string format = bytesPerSecond >= 10 ? "0" : "0.0";
        return $"{bytesPerSecond.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

    private static string FormatByteSize(ulong bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        string format = unitIndex == 0 || value >= 10 ? "0" : "0.0";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
            return;

        _expanded = expanded;
        _performanceGraph.Visible = expanded;
        _currentItem.Visible = expanded;
        _timeRemaining.Visible = expanded;
        _itemsRemaining.Visible = expanded;
        _detailsToggle.Expanded = expanded;
        LayoutControls();
        DetailsVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LayoutControls()
    {
        int horizontalInset = ScaleLogical(34);
        Padding = new Padding(horizontalInset, 0, horizontalInset, 0);
        int contentWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        int buttonSize = ScaleLogical(30);
        int buttonTop = ScaleLogical(38);
        _cancel.SetBounds(
            ClientSize.Width - Padding.Right - buttonSize,
            buttonTop,
            buttonSize,
            buttonSize);
        _pause.SetBounds(
            _cancel.Left - buttonSize - ScaleLogical(8),
            buttonTop,
            buttonSize,
            buttonSize);
        _summary.SetBounds(
            Padding.Left, ScaleLogical(14), contentWidth, ScaleLogical(21));
        _percentage.SetBounds(
            Padding.Left,
            ScaleLogical(40),
            Math.Max(1, _pause.Left - Padding.Left - ScaleLogical(8)),
            ScaleLogical(31));
        _performanceGraph.SetBounds(
            Padding.Left, ScaleLogical(77), contentWidth, ScaleLogical(101));
        _currentItem.SetBounds(
            Padding.Left, ScaleLogical(193), contentWidth, ScaleLogical(21));
        _timeRemaining.SetBounds(
            Padding.Left, ScaleLogical(216), contentWidth, ScaleLogical(21));
        _itemsRemaining.SetBounds(
            Padding.Left, ScaleLogical(239), contentWidth, ScaleLogical(21));

        int separatorTop = ScaleLogical(_expanded ? 273 : 75);
        _detailsSeparator.SetBounds(
            0, separatorTop, ClientSize.Width, Math.Max(1, ScaleLogical(1)));
        _detailsToggle.SetBounds(
            Padding.Left,
            ScaleLogical(_expanded ? 274 : 76),
            contentWidth,
            ScaleLogical(_expanded ? 51 : 47));
        Height = ScaleLogical(_expanded ? ExpandedHeight : CollapsedHeight);
    }

    private int ScaleLogical(int value) =>
        (int)Math.Round(value * DeviceDpi / 96D);

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutControls();
        DetailsVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private enum ProgressMeasurement
    {
        Items,
        Bytes,
    }
}

internal enum OperationCommandIcon
{
    Pause,
    Resume,
    Cancel,
}

internal sealed class OperationCommandButton : Control
{
    private OperationCommandIcon _command;
    private bool _hovered;

    internal OperationCommandIcon Command
    {
        get => _command;
        set
        {
            if (_command == value)
                return;
            _command = value;
            Invalidate();
        }
    }

    internal OperationCommandButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.Selectable
                 | ControlStyles.UserPaint, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_hovered && Enabled)
            e.Graphics.FillRectangle(SystemBrushes.ControlLight, ClientRectangle);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = Enabled ? SystemColors.ControlText : SystemColors.GrayText;
        float scale = DeviceDpi / 96F;
        float centerX = ClientSize.Width / 2F;
        float centerY = ClientSize.Height / 2F;
        using var pen = new Pen(color, Math.Max(1.5F, 1.5F * scale))
        {
            StartCap = LineCap.Square,
            EndCap = LineCap.Square,
        };
        using var brush = new SolidBrush(color);

        switch (Command)
        {
            case OperationCommandIcon.Pause:
                float barWidth = Math.Max(2F, 2.5F * scale);
                float barHeight = 12F * scale;
                float gap = 3F * scale;
                e.Graphics.FillRectangle(brush,
                    centerX - gap - barWidth, centerY - barHeight / 2, barWidth, barHeight);
                e.Graphics.FillRectangle(brush,
                    centerX + gap, centerY - barHeight / 2, barWidth, barHeight);
                break;
            case OperationCommandIcon.Resume:
                float halfHeight = 7F * scale;
                PointF[] triangle =
                [
                    new(centerX - 4F * scale, centerY - halfHeight),
                    new(centerX - 4F * scale, centerY + halfHeight),
                    new(centerX + 7F * scale, centerY),
                ];
                e.Graphics.FillPolygon(brush, triangle);
                break;
            case OperationCommandIcon.Cancel:
                float radius = 5.5F * scale;
                e.Graphics.DrawLine(pen,
                    centerX - radius, centerY - radius, centerX + radius, centerY + radius);
                e.Graphics.DrawLine(pen,
                    centerX + radius, centerY - radius, centerX - radius, centerY + radius);
                break;
        }

        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
            Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }
}

internal sealed class OperationDetailsToggle : Control
{
    private bool _expanded;
    private bool _hovered;

    internal bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
                return;
            _expanded = value;
            AccessibleName = value ? "Fewer details" : "More details";
            Invalidate();
        }
    }

    internal OperationDetailsToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.Selectable
                 | ControlStyles.UserPaint, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_hovered)
            e.Graphics.FillRectangle(SystemBrushes.ControlLight, ClientRectangle);

        float scale = DeviceDpi / 96F;
        float left = 7F * scale;
        float centerY = ClientSize.Height / 2F;
        float halfWidth = 5F * scale;
        float halfHeight = 3F * scale;
        PointF[] chevron = Expanded
            ?
            [
                new(left, centerY + halfHeight),
                new(left + halfWidth, centerY - halfHeight),
                new(left + halfWidth * 2, centerY + halfHeight),
            ]
            :
            [
                new(left, centerY - halfHeight),
                new(left + halfWidth, centerY + halfHeight),
                new(left + halfWidth * 2, centerY - halfHeight),
            ];
        using var pen = new Pen(SystemColors.GrayText, Math.Max(1F, scale));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, chevron);

        int textLeft = (int)Math.Round(29F * scale);
        TextRenderer.DrawText(
            e.Graphics,
            Expanded ? "Fewer details" : "More details",
            Font,
            new Rectangle(textLeft, 0, Math.Max(1, Width - textLeft), Height),
            SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
            Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }
}

internal sealed class OperationPerformanceGraph : Control
{
    private const int MaximumSamples = 240;
    private const int AnimationIntervalMilliseconds = 33;
    private const int HistorySmoothingRadius = 6;
    private const double AnimationResponseMilliseconds = 420;
    private const float HistoryCurveTension = 0.15F;
    private readonly List<double> _samples = [];
    private readonly System.Windows.Forms.Timer _animationTimer;
    private double _displayMaximum = 1;
    private double _targetValue;
    private double _displayedValue;
    private string _rateText = "Speed: Calculating…";
    private long _lastAnimationTick;
    private int _completionPercentage;
    private bool _paused;

    internal double DisplayedRate => _displayedValue;

    internal OperationPerformanceGraph()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        TabStop = false;
        _animationTimer = new System.Windows.Forms.Timer
        {
            Interval = AnimationIntervalMilliseconds,
        };
        _animationTimer.Tick += OnAnimationTick;
        _animationTimer.Start();
    }

    internal void SetCompletionPercentage(int percentage)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        if (_completionPercentage == percentage)
            return;
        _completionPercentage = percentage;
        Invalidate();
    }

    internal void SetRateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(_rateText, text, StringComparison.Ordinal))
            return;
        _rateText = text;
        Invalidate();
    }

    internal void SetRate(double value) =>
        _targetValue = Math.Max(0, value);

    internal void SetPaused(bool paused)
    {
        if (_paused == paused)
            return;
        _paused = paused;
        _lastAnimationTick = Environment.TickCount64;
        Invalidate();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        if (_lastAnimationTick == 0)
        {
            _lastAnimationTick = now;
            return;
        }

        long elapsedMilliseconds = Math.Clamp(now - _lastAnimationTick, 1, 250);
        _lastAnimationTick = now;
        if (_paused || !Visible)
            return;

        double interpolation = 1 - Math.Exp(
            -elapsedMilliseconds / AnimationResponseMilliseconds);
        _displayedValue += (_targetValue - _displayedValue) * interpolation;
        if (Math.Abs(_displayedValue - _targetValue) < 0.01)
            _displayedValue = _targetValue;

        _samples.Add(_displayedValue);
        if (_samples.Count > MaximumSamples)
            _samples.RemoveAt(0);
        double sampleMaximum = _samples.Count == 0 ? 1 : _samples.Max();
        _displayMaximum = Math.Max(
            1, Math.Max(sampleMaximum, _displayMaximum * 0.995));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle borderBounds = ClientRectangle;
        if (borderBounds.Width <= 2 || borderBounds.Height <= 2)
            return;
        borderBounds.Width--;
        borderBounds.Height--;
        Rectangle graphBounds = Rectangle.Inflate(borderBounds, -1, -1);

        Color gridColor = _paused
            ? Color.FromArgb(229, 220, 169)
            : Color.FromArgb(205, 232, 210);
        Color completionColor = _paused
            ? Color.FromArgb(247, 235, 153)
            : Color.FromArgb(183, 231, 173);
        Color graphColor = _paused
            ? Color.FromArgb(184, 157, 0)
            : Color.FromArgb(0, 181, 42);

        int completionWidth = (int)Math.Round(
            graphBounds.Width * _completionPercentage / 100D);
        if (completionWidth > 0)
        {
            using var completionBrush = new SolidBrush(completionColor);
            e.Graphics.FillRectangle(completionBrush,
                graphBounds.Left, graphBounds.Top, completionWidth, graphBounds.Height);
        }

        using (var gridPen = new Pen(gridColor, 1F))
        {
            for (int line = 1; line < 10; line++)
            {
                int x = graphBounds.Left + graphBounds.Width * line / 10;
                e.Graphics.DrawLine(gridPen, x, graphBounds.Top, x, graphBounds.Bottom);
            }
            for (int line = 1; line < 6; line++)
            {
                int y = graphBounds.Top + graphBounds.Height * line / 6;
                e.Graphics.DrawLine(gridPen, graphBounds.Left, y, graphBounds.Right, y);
            }
        }

        DrawHistory(e.Graphics, graphBounds, graphColor);
        if (!_paused)
            DrawCurrentRateGuide(e.Graphics, graphBounds);

        using var borderPen = new Pen(Color.FromArgb(190, 190, 190), 1F);
        e.Graphics.DrawRectangle(borderPen, borderBounds);
    }

    private void DrawHistory(Graphics graphics, Rectangle bounds, Color graphColor)
    {
        if (_samples.Count == 0)
            return;

        GraphicsState state = graphics.Save();
        graphics.SetClip(bounds);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float spacing = MaximumSamples <= 1
            ? bounds.Width
            : (float)bounds.Width / (MaximumSamples - 1);
        double[] smoothedSamples = OperationProgressMath.SmoothSamples(
            _samples,
            HistorySmoothingRadius);
        var points = new PointF[smoothedSamples.Length];
        for (int index = 0; index < smoothedSamples.Length; index++)
        {
            float x = bounds.Left + index * spacing;
            float normalized = (float)Math.Clamp(
                smoothedSamples[index] / _displayMaximum, 0, 1);
            float y = bounds.Bottom - normalized * Math.Max(1, bounds.Height - 1);
            points[index] = new PointF(x, y);
        }

        if (points.Length == 1)
        {
            using var pointBrush = new SolidBrush(graphColor);
            graphics.FillRectangle(pointBrush, points[0].X, points[0].Y, 1, 1);
        }
        else
        {
            using var fillBrush = new SolidBrush(graphColor);
            if (points.Length == 2)
            {
                graphics.FillPolygon(fillBrush,
                [
                    points[0],
                    points[1],
                    new PointF(points[1].X, bounds.Bottom),
                    new PointF(points[0].X, bounds.Bottom),
                ]);
            }
            else
            {
                using var path = new GraphicsPath();
                path.AddLine(points[0].X, bounds.Bottom, points[0].X, points[0].Y);
                path.AddCurve(points, HistoryCurveTension);
                path.AddLine(points[^1].X, points[^1].Y, points[^1].X, bounds.Bottom);
                path.CloseFigure();
                graphics.FillPath(fillBrush, path);
            }
        }
        graphics.Restore(state);
    }

    private void DrawCurrentRateGuide(Graphics graphics, Rectangle bounds)
    {
        float normalized = (float)Math.Clamp(
            _displayedValue / _displayMaximum, 0, 1);
        int lineY = (int)Math.Round(
            bounds.Bottom - normalized * Math.Max(1, bounds.Height - 1));
        using var guidePen = new Pen(Color.FromArgb(70, 70, 70), 1F);
        graphics.DrawLine(guidePen, bounds.Left, lineY, bounds.Right, lineY);

        Size textSize = TextRenderer.MeasureText(
            _rateText, Font, Size.Empty, TextFormatFlags.NoPadding);
        int horizontalPadding = Math.Max(3, (int)Math.Round(DeviceDpi / 96F * 4));
        int captionWidth = Math.Min(
            bounds.Width,
            Math.Max(textSize.Width + horizontalPadding * 2,
                (int)Math.Round(DeviceDpi / 96F * 160)));
        int captionHeight = Math.Min(bounds.Height, textSize.Height + 2);
        int captionTop = Math.Clamp(
            lineY - captionHeight, bounds.Top, bounds.Bottom - captionHeight);
        var captionBounds = new Rectangle(
            bounds.Right - captionWidth, captionTop, captionWidth, captionHeight);
        TextRenderer.DrawText(
            graphics,
            _rateText,
            Font,
            new Rectangle(
                captionBounds.Left,
                captionBounds.Top,
                Math.Max(1, captionBounds.Width - horizontalPadding),
                captionBounds.Height),
            SystemColors.ControlText,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _animationTimer.Tick -= OnAnimationTick;
            _animationTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
