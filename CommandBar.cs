using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MultiExplorer;

public sealed class CommandBar : ToolStrip
{
    public enum Cmd
    {
        NewFolder,
        Cut, Copy, CopyPaths, Paste,
        Rename, Delete,
        ViewDetails, ViewList, ViewTiles, ViewIcons, ViewMediumIcons, ViewSmallIcons, ViewContent,
        ToggleDetailsPane, TogglePreviewPane,
        ShowNavPane, ShowCompactView, ShowCheckboxes, ShowFileExtensions, ShowHiddenItems,
        ToggleQuickLook,
        SelectAll, Properties, FolderOptions, About, ViewLog, SetHotkey,
        GoToParent, MirrorToOther,
        Exit,
    }

    public event EventHandler<Cmd>?  CommandIssued;
    public event EventHandler<ApplicationTheme>? ThemeSelected;
    /// <summary>Fires just before the View dropdown opens so the caller can refresh checkmarks.</summary>
    public event EventHandler?       ViewDropDownOpening;

    // Toolbar button glyphs (Segoe MDL2 Assets / Segoe Fluent Icons)
    private const char GlyphNewFolder = (char)0xE948;
    private const char GlyphCut       = (char)0xE8C6;
    private const char GlyphCopy      = (char)0xE8C8;
    private const char GlyphCopyPaths = (char)0xE71B;
    private const char GlyphPaste     = (char)0xE77F;
    private const char GlyphRename    = (char)0xE8AC;
    private const char GlyphDelete    = (char)0xE74D;
    private const char GlyphView      = (char)0xE8A9;
    private const char GlyphGoUp      = (char)0xE74A;
    private const char GlyphMirror    = (char)0xE8AB;

    // View-mode menu item glyphs
    private const char GlyphViewDetails     = (char)0xE9D5;
    private const char GlyphViewList        = (char)0xE8FD;
    private const char GlyphViewTiles       = (char)0xE80A;
    private const char GlyphViewLargeIcons  = (char)0xE8B9;
    private const char GlyphViewMediumIcons = (char)0xF0E2;
    private const char GlyphViewSmallIcons  = (char)0xE8C4;
    private const char GlyphViewContent     = (char)0xF168;

    // Pane toggle glyphs
    private const char GlyphDetailsPane = (char)0xE946;
    private const char GlyphPreviewPane = (char)0xE8FF;

    // Show sub-menu glyphs
    private const char GlyphNavPane   = (char)0xE700;
    private const char GlyphCompact   = (char)0xE8FD;
    private const char GlyphCheckbox  = (char)0xE73A;
    private const char GlyphFileExt   = (char)0xE8AC;
    private const char GlyphHidden    = (char)0xED1A;
    private const char GlyphQuickLook = (char)0xE71E;

    // Avoid enumerating every installed font during cold start. Windows 11 ships
    // Segoe Fluent Icons, while supported Windows 10 versions ship Segoe MDL2
    // Assets. Selecting from the OS version is deterministic and avoids warming
    // the system font catalogue before the first window can be displayed.
    private static readonly string _iconFont =
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";

    private readonly List<Image> _ownedImages = new();
    private readonly Font        _moreButtonFont;

    // Pixel size used for all glyph bitmaps — set to LogicalToDeviceUnits(24) so
    // bitmaps always match ImageScalingSize exactly, avoiding WinForms upscale blur.
    private int _iconPx = 24;

    // Kept as fields so ApplyDpi() can update sizes and recreate icons on DPI change.
    private readonly ToolStripDropDownButton _more;
    private readonly ToolStripDropDownMenu   _viewMenu;
    private readonly ToolStripDropDownMenu   _showMenu;

    // Maps each item that carries a glyph to that glyph so icons can be recreated at new DPI.
    private readonly List<(ToolStripItem Item, char Glyph)> _glyphItems = new();

    // Toggle menu items — updated by UpdateViewToggles()
    private readonly ToolStripMenuItem _miDetailsPane;
    private readonly ToolStripMenuItem _miPreviewPane;
    private readonly ToolStripMenuItem _miNavPane;
    private readonly ToolStripMenuItem _miCompactView;
    private readonly ToolStripMenuItem _miCheckboxes;
    private readonly ToolStripMenuItem _miFileExtensions;
    private readonly ToolStripMenuItem _miHiddenItems;
    private readonly ToolStripMenuItem _miQuickLook;
    private readonly ToolStripMenuItem _miLightTheme;
    private readonly ToolStripMenuItem _miDarkTheme;

    public CommandBar()
    {
        GripStyle       = ToolStripGripStyle.Hidden;
        RenderMode      = ToolStripRenderMode.System;
        Font            = new Font("Segoe UI", 9f);
        _moreButtonFont = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
        AutoSize        = false;

        // Compute icon size now so bitmaps and ImageScalingSize always agree.
        _iconPx          = LogicalToDeviceUnits(24);
        Height           = LogicalToDeviceUnits(46);
        Padding          = new Padding(4, 0, 4, 0);
        ImageScalingSize = new Size(_iconPx, _iconPx);
        ShowItemToolTips = true;

        // New folder: icon + text
        IconTextBtn(GlyphNewFolder, "New folder", Cmd.NewFolder, margin: new Padding(4, 0, 6, 0));
        Items.Add(new ToolStripSeparator());

        // Clipboard: icon only
        IconBtn(GlyphCut,   "Cut (Ctrl+X)",   Cmd.Cut);
        IconBtn(GlyphCopy,  "Copy (Ctrl+C)",  Cmd.Copy);
        IconBtn(GlyphCopyPaths, "Copy full paths", Cmd.CopyPaths);
        IconBtn(GlyphPaste, "Paste (Ctrl+V)", Cmd.Paste);
        Items.Add(new ToolStripSeparator());

        // File ops: icon only
        IconBtn(GlyphRename, "Rename (F2)",  Cmd.Rename);
        IconBtn(GlyphDelete, "Delete (Del)", Cmd.Delete);
        Items.Add(new ToolStripSeparator());

        // View dropdown
        var view = new ToolStripDropDownButton
        {
            Image             = MakeGlyph(GlyphView),
            Text              = "View",
            DisplayStyle      = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            AutoToolTip       = false,
            Margin            = new Padding(4, 0, 4, 0),
        };
        _glyphItems.Add((view, GlyphView));

        // ShowCheckMargin = true gives checkable items a dedicated leftmost column:
        // [✓]  [icon]  text
        _viewMenu = (ToolStripDropDownMenu)view.DropDown;
        _viewMenu.ImageScalingSize = new Size(_iconPx, _iconPx);
        _viewMenu.ShowCheckMargin  = true;
        _viewMenu.ShowImageMargin  = true;
        view.DropDownOpening += (s, e) => ViewDropDownOpening?.Invoke(s, e);

        DropItemWithIcon(view, GlyphViewDetails,     "Details",      Cmd.ViewDetails);
        DropItemWithIcon(view, GlyphViewList,        "List",         Cmd.ViewList);
        DropItemWithIcon(view, GlyphViewTiles,       "Tiles",        Cmd.ViewTiles);
        DropItemWithIcon(view, GlyphViewLargeIcons,  "Large icons",  Cmd.ViewIcons);
        DropItemWithIcon(view, GlyphViewMediumIcons, "Medium icons", Cmd.ViewMediumIcons);
        DropItemWithIcon(view, GlyphViewSmallIcons,  "Small icons",  Cmd.ViewSmallIcons);
        view.DropDownItems.Add(new ToolStripSeparator());
        DropItemWithIcon(view, GlyphViewContent,     "Content",      Cmd.ViewContent);
        view.DropDownItems.Add(new ToolStripSeparator());

        _miDetailsPane = DropCheckItem(view, GlyphDetailsPane, "Details pane",             Cmd.ToggleDetailsPane);
        _miPreviewPane = DropCheckItem(view, GlyphPreviewPane, "Preview pane",             Cmd.TogglePreviewPane);
        _miQuickLook   = DropCheckItem(view, GlyphQuickLook,  "QuickLook preview (Space)", Cmd.ToggleQuickLook);
        view.DropDownItems.Add(new ToolStripSeparator());

        // Show submenu
        var show  = new ToolStripMenuItem("Show") { Font = Font };
        _showMenu = (ToolStripDropDownMenu)show.DropDown;
        _showMenu.ImageScalingSize = new Size(_iconPx, _iconPx);
        _showMenu.ShowCheckMargin  = true;
        _showMenu.ShowImageMargin  = true;
        _miNavPane        = DropCheckItem(show, GlyphNavPane,  "Navigation pane",      Cmd.ShowNavPane);
        _miCompactView    = DropCheckItem(show, GlyphCompact,  "Compact view",         Cmd.ShowCompactView);
        show.DropDownItems.Add(new ToolStripSeparator());
        _miCheckboxes     = DropCheckItem(show, GlyphCheckbox, "Item check boxes",     Cmd.ShowCheckboxes);
        _miFileExtensions = DropCheckItem(show, GlyphFileExt,  "File name extensions", Cmd.ShowFileExtensions);
        _miHiddenItems    = DropCheckItem(show, GlyphHidden,   "Hidden items",         Cmd.ShowHiddenItems);
        view.DropDownItems.Add(show);

        Items.Add(view);

        Items.Add(new ToolStripSeparator());
        IconBtn(GlyphGoUp,   "Go to parent folder",                   Cmd.GoToParent);
        IconBtn(GlyphMirror, "Copy this folder to the opposite pane", Cmd.MirrorToOther);

        // Spring pushes the see-more button to the far right.
        Items.Add(new ToolStripSpring());

        _more = new ToolStripDropDownButton("…  ▾")
        {
            AutoToolTip       = false,
            DisplayStyle      = ToolStripItemDisplayStyle.Text,
            ShowDropDownArrow = false,
            Font              = _moreButtonFont,
            AutoSize          = false,
            Width             = LogicalToDeviceUnits(66),
            Height            = LogicalToDeviceUnits(38),
            TextAlign         = ContentAlignment.MiddleCenter,
        };
        DropItem(_more, "Options", Cmd.FolderOptions, "Ctrl+O");
        _more.DropDownItems.Add(new ToolStripSeparator());
        var appearance = new ToolStripMenuItem("Appearance") { Font = Font };
        _miLightTheme = new ToolStripMenuItem("Light")
        {
            Font = Font,
            CheckOnClick = false,
        };
        _miDarkTheme = new ToolStripMenuItem("Dark")
        {
            Font = Font,
            CheckOnClick = false,
        };
        _miLightTheme.Click += (_, _) => ThemeSelected?.Invoke(this, ApplicationTheme.Light);
        _miDarkTheme.Click  += (_, _) => ThemeSelected?.Invoke(this, ApplicationTheme.Dark);
        appearance.DropDownItems.Add(_miLightTheme);
        appearance.DropDownItems.Add(_miDarkTheme);
        _more.DropDownItems.Add(appearance);
        _more.DropDownItems.Add(new ToolStripSeparator());
        DropItem(_more, "View log", Cmd.ViewLog, "Ctrl+L");
        DropItem(_more, "Set show-window hotkey…", Cmd.SetHotkey);
        _more.DropDownItems.Add(new ToolStripSeparator());
        DropItem(_more, "About MultiExplorer", Cmd.About);
        _more.DropDownItems.Add(new ToolStripSeparator());
        DropItem(_more, "Exit", Cmd.Exit, "Ctrl+Q");
        Items.Add(_more);
        SetThemeSelection(ThemeManager.Current);
    }

    public void SetThemeSelection(ApplicationTheme theme)
    {
        _miLightTheme.Checked = theme == ApplicationTheme.Light;
        _miDarkTheme.Checked  = theme == ApplicationTheme.Dark;
    }

    public void ApplyTheme()
    {
        ThemeManager.ApplyToolStrip(this);
        RecreateGlyphs();
        Invalidate(true);
    }

    /// <summary>Syncs the checkmark state of all toggleable View menu items.</summary>
    public void UpdateViewToggles(bool detailsPane, bool previewPane, bool navPane,
        bool compactView, bool checkboxes, bool fileExtensions, bool hiddenItems,
        bool quickLook)
    {
        _miDetailsPane.Checked    = detailsPane;
        _miPreviewPane.Checked    = previewPane;
        _miNavPane.Checked        = navPane;
        _miCompactView.Checked    = compactView;
        _miCheckboxes.Checked     = checkboxes;
        _miFileExtensions.Checked = fileExtensions;
        _miHiddenItems.Checked    = hiddenItems;
        _miQuickLook.Checked      = quickLook;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Guard: can fire before the constructor assigns _more/_viewMenu/_showMenu.
        if (_more is null) return;
        ApplyDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpi();
    }

    private void ApplyDpi()
    {
        _iconPx = LogicalToDeviceUnits(24);

        Height           = LogicalToDeviceUnits(46);
        ImageScalingSize = new Size(_iconPx, _iconPx);
        _viewMenu.ImageScalingSize = new Size(_iconPx, _iconPx);
        _showMenu.ImageScalingSize = new Size(_iconPx, _iconPx);
        _more.Width  = LogicalToDeviceUnits(66);
        _more.Height = LogicalToDeviceUnits(38);

        RecreateGlyphs();
    }

    private void RecreateGlyphs()
    {
        // Capture old images; clear list so MakeGlyph fills it fresh.
        var old = new List<Image>(_ownedImages);
        _ownedImages.Clear();

        foreach (var (item, glyph) in _glyphItems)
            item.Image = MakeGlyph(glyph);

        // Dispose old images after items reference the new ones.
        foreach (var img in old) img.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var img in _ownedImages) img.Dispose();
            _moreButtonFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void IconBtn(char glyph, string tooltip, Cmd cmd)
    {
        var b = new ToolStripButton
        {
            Image        = MakeGlyph(glyph),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText  = tooltip,
            AutoToolTip  = false,
            Margin       = new Padding(3, 0, 3, 0),
        };
        b.Click += (_, _) => CommandIssued?.Invoke(this, cmd);
        _glyphItems.Add((b, glyph));
        Items.Add(b);
    }

    private void IconTextBtn(char glyph, string text, Cmd cmd, Padding? margin = null)
    {
        var b = new ToolStripButton(text)
        {
            Image             = MakeGlyph(glyph),
            DisplayStyle      = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ToolTipText       = text,
            AutoToolTip       = false,
            Margin            = margin ?? new Padding(3, 0, 3, 0),
        };
        b.Click += (_, _) => CommandIssued?.Invoke(this, cmd);
        _glyphItems.Add((b, glyph));
        Items.Add(b);
    }

    private void DropItem(
        ToolStripDropDownItem parent,
        string text,
        Cmd cmd,
        string? shortcutKeyDisplayString = null)
    {
        var item = new ToolStripMenuItem(text)
        {
            Font = Font,
            ShortcutKeyDisplayString = shortcutKeyDisplayString,
        };
        item.Click += (_, _) => CommandIssued?.Invoke(this, cmd);
        parent.DropDownItems.Add(item);
    }

    private void DropItemWithIcon(ToolStripDropDownItem parent, char glyph, string text, Cmd cmd)
    {
        var item = new ToolStripMenuItem(text) { Font = Font, Image = MakeGlyph(glyph) };
        item.Click += (_, _) => CommandIssued?.Invoke(this, cmd);
        _glyphItems.Add((item, glyph));
        parent.DropDownItems.Add(item);
    }

    private ToolStripMenuItem DropCheckItem(ToolStripDropDownItem parent, char glyph, string text, Cmd cmd)
    {
        var item = new ToolStripMenuItem(text)
        {
            Font         = Font,
            Image        = MakeGlyph(glyph),
            CheckOnClick = false,
        };
        item.Click += (_, _) => CommandIssued?.Invoke(this, cmd);
        _glyphItems.Add((item, glyph));
        parent.DropDownItems.Add(item);
        return item;
    }

    // Renders one glyph from _iconFont at _iconPx size using AntiAliasGridFit.
    // Segoe MDL2/Fluent Icons carry pixel-grid hints at 16, 20, 24, 32 px so
    // rendering at the exact device size is always sharper than upscaling.
    private Image MakeGlyph(char code)
    {
        var bmp = new Bitmap(_iconPx, _iconPx, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
            try
            {
                using var font  = new Font(_iconFont, _iconPx * 0.78f, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(ThemeManager.Text);
                var fmt = new StringFormat
                {
                    Alignment     = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags   = StringFormatFlags.NoWrap,
                };
                g.DrawString(code.ToString(), font, brush,
                             new RectangleF(0, 0, _iconPx, _iconPx), fmt);
            }
            catch (Exception ex)
            {
                AppLog.Debug(ex, nameof(MakeGlyph),
                    $"Could not render toolbar glyph U+{(int)code:X4}.");
            }
        }
        _ownedImages.Add(bmp);
        return bmp;
    }
}

// Expands to fill remaining horizontal space, right-aligning items that follow it.
file sealed class ToolStripSpring : ToolStripLabel
{
    public override Size GetPreferredSize(Size constrainingSize)
    {
        if (Owner is null) return base.GetPreferredSize(constrainingSize);
        int used = Owner.Padding.Horizontal;
        foreach (ToolStripItem item in Owner.Items)
            if (item != this && !item.IsOnOverflow)
                used += item.Width + item.Margin.Horizontal;
        return new Size(Math.Max(2, Owner.DisplayRectangle.Width - used),
                        base.GetPreferredSize(constrainingSize).Height);
    }
}
