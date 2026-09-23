using System.Drawing.Text;

namespace MultiExplorer;

/// <summary>
/// Application-owned replacement for ExplorerBrowser's legacy DirectUI command
/// band. Besides keeping colours consistent, this ensures Preview and Help are
/// connected to MultiExplorer instead of non-functional legacy Shell commands.
/// </summary>
internal sealed class ExplorerCommandBand : ToolStrip
{
    private const char GlyphOpen = (char)0xE8E5;
    private const char GlyphNewFolder = (char)0xE948;
    private const char GlyphPreviewPane = (char)0xE8FF;
    private const char GlyphHelp = (char)0xE897;

    private static readonly string IconFontName =
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";

    private readonly List<Image> _ownedImages = [];
    private readonly List<(ToolStripItem Item, char Glyph)> _glyphItems = [];
    private readonly ToolStripButton _previewPane;
    private int _glyphDpi = 96;

    internal event EventHandler<CommandBar.Cmd>? CommandIssued;
    internal event EventHandler? OpenRequested;

    internal ExplorerCommandBand()
    {
        GripStyle = ToolStripGripStyle.Hidden;
        AutoSize = false;
        Dock = DockStyle.None;
        Padding = new Padding(8, 0, 8, 0);
        Font = new Font("Segoe UI", 9f);
        RenderMode = ToolStripRenderMode.ManagerRenderMode;
        ShowItemToolTips = true;

        var organize = new ToolStripDropDownButton("Organize")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoToolTip = false,
        };
        AddCommand(organize, "Cut", CommandBar.Cmd.Cut, "Ctrl+X");
        AddCommand(organize, "Copy", CommandBar.Cmd.Copy, "Ctrl+C");
        AddCommand(organize, "Copy as path", CommandBar.Cmd.CopyPaths,
            CommandBar.CopyPathsShortcutText);
        AddCommand(organize, "Paste", CommandBar.Cmd.Paste, "Ctrl+V");
        organize.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(organize, "Rename", CommandBar.Cmd.Rename, "F2");
        AddCommand(organize, "Delete", CommandBar.Cmd.Delete, "Del");
        organize.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(organize, "Select all", CommandBar.Cmd.SelectAll, "Ctrl+A");
        AddCommand(organize, "Properties", CommandBar.Cmd.Properties, "Alt+Enter");

        ToolStripButton open = CreateButton(GlyphOpen, "Open", "Open selected item");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        ToolStripButton newFolder = CreateButton(
            GlyphNewFolder, "New folder", "Create a new folder");
        newFolder.Click += (_, _) => RaiseCommand(CommandBar.Cmd.NewFolder);

        ToolStripButton help = CreateButton(
            GlyphHelp, string.Empty, "MultiExplorer help (F1)");
        help.DisplayStyle = ToolStripItemDisplayStyle.Image;
        help.Alignment = ToolStripItemAlignment.Right;
        help.Click += (_, _) => RaiseCommand(CommandBar.Cmd.Help);

        _previewPane = CreateButton(
            GlyphPreviewPane, string.Empty, "Toggle preview pane");
        _previewPane.DisplayStyle = ToolStripItemDisplayStyle.Image;
        _previewPane.Alignment = ToolStripItemAlignment.Right;
        _previewPane.Click += (_, _) => RaiseCommand(CommandBar.Cmd.TogglePreviewPane);

        Items.AddRange([
            organize,
            new ToolStripSeparator(),
            open,
            new ToolStripSeparator(),
            newFolder,
            help,
            _previewPane,
        ]);

        ApplyTheme();
    }

    internal void SetPreviewPaneVisible(bool visible)
    {
        if (_previewPane.Checked == visible) return;
        _previewPane.Checked = visible;
        _previewPane.Invalidate();
    }

    internal void ApplyTheme()
    {
        RecreateGlyphs(_glyphDpi);
        BackColor = ThemeManager.Surface;
        ForeColor = ThemeManager.Text;
        ThemeManager.ApplyToolStrip(this);
        foreach (ToolStripDropDownItem item in Items.OfType<ToolStripDropDownItem>())
            ThemeManager.ApplyToolStrip(item.DropDown);
        Invalidate(true);
    }

    internal void ApplyDpi(int dpi)
    {
        int effectiveDpi = Math.Max(96, dpi);
        if (effectiveDpi != _glyphDpi)
            RecreateGlyphs(effectiveDpi);
        Height = Math.Max(1, 39 * effectiveDpi / 96);
        ImageScalingSize = new Size(
            Math.Max(16, 18 * effectiveDpi / 96),
            Math.Max(16, 18 * effectiveDpi / 96));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Image image in _ownedImages)
                image.Dispose();
            _ownedImages.Clear();
        }

        base.Dispose(disposing);
    }

    private ToolStripButton CreateButton(char glyph, string text, string toolTip)
    {
        var button = new ToolStripButton
        {
            Image = CreateGlyph(glyph, _glyphDpi),
            Text = text,
            ToolTipText = toolTip,
            DisplayStyle = string.IsNullOrEmpty(text)
                ? ToolStripItemDisplayStyle.Image
                : ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            AutoToolTip = false,
            Margin = new Padding(3, 0, 3, 0),
        };
        _glyphItems.Add((button, glyph));
        return button;
    }

    private void AddCommand(ToolStripDropDownItem parent, string text,
        CommandBar.Cmd command, string? shortcut = null)
    {
        var item = new ToolStripMenuItem(text)
        {
            ShortcutKeyDisplayString = shortcut,
        };
        item.Click += (_, _) => RaiseCommand(command);
        parent.DropDownItems.Add(item);
    }

    private void RaiseCommand(CommandBar.Cmd command) =>
        CommandIssued?.Invoke(this, command);

    private void RecreateGlyphs(int dpi)
    {
        foreach (Image image in _ownedImages)
            image.Dispose();
        _ownedImages.Clear();
        _glyphDpi = Math.Max(96, dpi);
        foreach ((ToolStripItem item, char glyph) in _glyphItems)
            item.Image = CreateGlyph(glyph, _glyphDpi);
    }

    private Image CreateGlyph(char glyph, int dpi)
    {
        int size = Math.Max(18, 18 * Math.Max(96, dpi) / 96);
        var image = new Bitmap(size, size);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var font = new Font(IconFontName, Math.Max(14, size - 3),
            FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(ThemeManager.Text))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        })
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(glyph.ToString(), font, brush,
                new RectangleF(0, 0, size, size), format);
        }

        _ownedImages.Add(image);
        return image;
    }
}
