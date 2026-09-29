using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// Lightweight startup surface shown while the native Explorer panes and the
/// application-owned file lists prepare their initial content.
/// </summary>
internal sealed class StartupLoadingForm : Form
{
    private readonly Label _message;

    internal StartupLoadingForm(Icon? applicationIcon)
    {
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "MultiExplorer";
        Font = SystemFonts.MessageBoxFont ?? new Font(
            "Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        if (applicationIcon is not null)
            Icon = (Icon)applicationIcon.Clone();

        var title = new Label
        {
            Name = "startupLoadingTitle",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 14F, FontStyle.Bold,
                GraphicsUnit.Point),
            Margin = new Padding(0, 0, 0, 8),
            Text = "Loading MultiExplorer",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _message = new Label
        {
            AutoSize = true,
            Name = "startupLoadingMessage",
            Margin = new Padding(0, 0, 0, 14),
            Text = "Preparing files, folders, and navigation…",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var progress = new ProgressBar
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            MarqueeAnimationSpeed = 28,
            Margin = Padding.Empty,
            MinimumSize = new Size(372, 4),
            Name = "startupLoadingProgress",
            Size = new Size(372, 4),
            Style = ProgressBarStyle.Marquee,
        };
        var content = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Name = "startupLoadingContent",
            Padding = new Padding(24, 18, 24, 20),
            RowCount = 3,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(title, 0, 0);
        content.Controls.Add(_message, 0, 1);
        content.Controls.Add(progress, 0, 2);
        Controls.Add(content);

        // Apply DPI scaling only after the complete control tree exists. The
        // form grows from each control's preferred height instead of dividing a
        // fixed client height into percentage rows, so large DPI and text-scale
        // settings cannot make the labels overlap the progress bar.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = SizeFromClientSize(new Size(420, 170));

        ThemeManager.ApplyTo(this);
    }

    internal void ShowCentered(Form owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        PerformLayout();
        Rectangle workArea = Screen.FromRectangle(owner.Bounds).WorkingArea;
        int left = Math.Clamp(
            owner.Left + ((owner.Width - Width) / 2),
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - Width));
        int top = Math.Clamp(
            owner.Top + ((owner.Height - Height) / 2),
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - Height));
        Location = new Point(left, top);
        Show(owner);
        Activate();
    }

    internal void SetMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _message.Text = message;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyNativeWindow(Handle);
    }
}
