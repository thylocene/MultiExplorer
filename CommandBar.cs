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
        SelectAll, Properties, FolderOptions, Help, About, ViewLog, SetHotkey, ToggleStartWithWindows,
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
    private readonly ToolStripButton _more;
    private readonly ToolStripDropDownMenu   _viewMenu;
    private readonly ToolStripDropDownMenu   _showMenu;
    private MoreMenuPopup? _morePopup;
    private AppearanceMenuPopup? _appearancePopup;

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
    private readonly ToolStripMenuItem _miStartWithWindows;

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
        IconBtn(GlyphGoUp,   "Go to parent folder", Cmd.GoToParent);
        IconBtn(GlyphMirror, "Open in other pane",  Cmd.MirrorToOther);

        _more = new ToolStripButton("…  ▾")
        {
            AutoToolTip       = false,
            DisplayStyle      = ToolStripItemDisplayStyle.Text,
            // ToolStrip's built-in right alignment is stable across the two
            // independently sized panes.  A spring item can briefly consume
            // the available width during initial layout and send this button
            // to the native overflow chevron, where it then remains.
            Alignment         = ToolStripItemAlignment.Right,
            Overflow          = ToolStripItemOverflow.Never,
            Font              = _moreButtonFont,
            AutoSize          = false,
            Width             = LogicalToDeviceUnits(66),
            Height            = LogicalToDeviceUnits(38),
            TextAlign         = ContentAlignment.MiddleCenter,
        };
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
        _miStartWithWindows = new ToolStripMenuItem("Start MultiExplorer with Windows")
        {
            Font = Font,
            CheckOnClick = false,
        };
        Items.Add(_more);
        _more.Click += (_, _) => ShowMoreMenu();
        SetThemeSelection(ThemeManager.Current);
    }

    public void SetThemeSelection(ApplicationTheme theme)
    {
        _miLightTheme.Checked = theme == ApplicationTheme.Light;
        _miDarkTheme.Checked  = theme == ApplicationTheme.Dark;
    }

    public void SetStartWithWindowsChecked(bool enabled) =>
        _miStartWithWindows.Checked = enabled;

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

    private void ShowMoreMenu(bool selectExit = false)
    {
        if (Disposing || IsDisposed) return;
        _morePopup?.Close();

        var popup = new MoreMenuPopup(_miStartWithWindows.Checked, selectExit);
        popup.CommandChosen += (_, command) => CommandIssued?.Invoke(this, command);
        popup.AppearanceRequested += (_, rowBounds) =>
            BeginInvoke(() => ShowAppearanceMenu(popup, rowBounds));
        popup.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_morePopup, popup)) _morePopup = null;
        };
        _morePopup = popup;

        Point location = PointToScreen(new Point(
            _more.Bounds.Right - popup.Width, _more.Bounds.Bottom));
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        location.X = Math.Max(workingArea.Left,
            Math.Min(location.X, workingArea.Right - popup.Width));
        if (location.Y + popup.Height > workingArea.Bottom)
            location.Y = PointToScreen(new Point(_more.Bounds.Left, _more.Bounds.Top)).Y - popup.Height;
        popup.Location = location;
        Form? owner = FindForm();
        if (owner != null) popup.Show(owner); else popup.Show();
        popup.Activate();
    }

    private void ShowAppearanceMenu(MoreMenuPopup parent, Rectangle appearanceRow)
    {
        if (parent.IsDisposed || !parent.Visible) return;

        _appearancePopup?.Close();
        var popup = new AppearanceMenuPopup(ThemeManager.Current);
        popup.ThemeChosen += (_, theme) => ThemeSelected?.Invoke(this, theme);
        popup.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_appearancePopup, popup)) _appearancePopup = null;
            parent.CloseAppearanceSubmenu();
        };
        _appearancePopup = popup;

        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        Point location = new(appearanceRow.Right - 8, appearanceRow.Top);
        if (location.X + popup.Width > workingArea.Right)
            location.X = appearanceRow.Left - popup.Width + 8;
        location.X = Math.Max(workingArea.Left, location.X);
        location.Y = Math.Max(workingArea.Top,
            Math.Min(location.Y, workingArea.Bottom - popup.Height));
        popup.Location = location;
        popup.Show(parent);
        popup.Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _morePopup?.Dispose();
            _appearancePopup?.Dispose();
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

    private ToolStripMenuItem DropCheckItem(ToolStripDropDownItem parent, string text, Cmd cmd)
    {
        var item = new ToolStripMenuItem(text)
        {
            Font         = Font,
            CheckOnClick = false,
        };
        item.Click += (_, _) => CommandIssued?.Invoke(this, cmd);
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

    private sealed class MoreMenuPopup : LayeredPopupForm
    {
        private readonly List<MenuRow> _rows = new();
        private readonly Font _font;
        private readonly Font _shortcutFont;
        private int _hovered = -1;
        private bool _appearanceSubmenuOpen;
        private bool _closing;

        internal event EventHandler<Cmd>? CommandChosen;
        internal event EventHandler<Rectangle>? AppearanceRequested;

        internal MoreMenuPopup(bool startWithWindows, bool selectExit)
        {
            _font = new Font("Segoe UI", 10f);
            _shortcutFont = new Font("Segoe UI", 9f);
            int rowHeight = LogicalToDeviceUnits(29);
            int separatorHeight = Math.Max(1, LogicalToDeviceUnits(1));
            int y = LogicalToDeviceUnits(3);
            Width = LogicalToDeviceUnits(318);

            AddRow("Explorer options", "Ctrl+O", Cmd.FolderOptions, rowHeight, ref y);
            AddSeparator(separatorHeight, ref y);
            AddRow("Set appearance", null, null, rowHeight, ref y, isAppearance: true);
            AddRow("Set hotkey", null, Cmd.SetHotkey, rowHeight, ref y);
            AddRow("Set Auto-start", null,
                Cmd.ToggleStartWithWindows, rowHeight, ref y, isChecked: startWithWindows);
            AddSeparator(separatorHeight, ref y);
            AddRow("View logs", "Ctrl+L", Cmd.ViewLog, rowHeight, ref y);
            AddSeparator(separatorHeight, ref y);
            AddRow("Help", null, Cmd.Help, rowHeight, ref y);
            AddRow("About MultiExplorer", null, Cmd.About, rowHeight, ref y);
            AddSeparator(separatorHeight, ref y);
            AddRow("Exit", "Ctrl+Q", Cmd.Exit, rowHeight, ref y);
            Height = y + LogicalToDeviceUnits(4);
            if (selectExit) _hovered = _rows.FindLastIndex(row => row.Command == Cmd.Exit);

            MouseMove += OnPopupMouseMove;
            MouseLeave += (_, _) =>
            {
                if (!_appearanceSubmenuOpen) SetHovered(-1);
            };
            MouseDown += OnPopupMouseDown;
            KeyDown += OnPopupKeyDown;
            FormClosing += (_, _) => _closing = true;
            Deactivate += (_, _) =>
            {
                if (!_appearanceSubmenuOpen) ClosePopup();
            };
        }

        internal void CloseAppearanceSubmenu()
        {
            _appearanceSubmenuOpen = false;
            ClosePopup();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                _font.Dispose();
                _shortcutFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void RenderSurface(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int lastActionableRow = _rows.FindLastIndex(row => !row.IsSeparator);
            bool lastRowSelected = lastActionableRow >= 0 && _hovered == lastActionableRow;
            using GraphicsPath outline = PopupVisuals.CreateBottomRoundedPath(
                new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), CornerRadius);
            using (var brush = new SolidBrush(lastRowSelected
                ? ThemeManager.MenuHighlight : ThemeManager.Surface))
                graphics.FillPath(brush, outline);
            if (lastRowSelected)
            {
                // The final highlighted row shares the popup's outer contour.
                // Painting the rows above it back to the surface colour avoids a
                // second, slightly different rounded path at the lower-right edge.
                using var surface = new SolidBrush(ThemeManager.Surface);
                graphics.FillRectangle(surface, new RectangleF(1, 1, Width - 2f,
                    Math.Max(0, _rows[lastActionableRow].Bounds.Top - 1f)));
            }
            using (var pen = new Pen(ThemeManager.Border))
                graphics.DrawPath(pen, outline);

            for (int index = 0; index < _rows.Count; index++)
            {
                MenuRow row = _rows[index];
                if (row.IsSeparator)
                {
                    using var pen = new Pen(ThemeManager.Border);
                    int inset = LogicalToDeviceUnits(16);
                    graphics.DrawLine(pen, inset, row.Bounds.Top + row.Bounds.Height / 2,
                        Width - inset, row.Bounds.Top + row.Bounds.Height / 2);
                    continue;
                }

                bool selected = index == _hovered;
                if (selected && !lastRowSelected)
                {
                    using var brush = new SolidBrush(ThemeManager.MenuHighlight);
                    graphics.FillRectangle(brush, row.Bounds);
                }

                Color textColor = selected ? ThemeManager.MenuHighlightText : ThemeManager.Text;
                int left = LogicalToDeviceUnits(row.IsChecked ? 34 : 16);
                var textBounds = new Rectangle(left, row.Bounds.Top,
                    Math.Max(1, Width - left - LogicalToDeviceUnits(82)), row.Bounds.Height);
                DrawText(graphics, row.Text!, _font, textBounds, textColor, StringAlignment.Near);

                if (row.Shortcut is not null)
                {
                    var shortcutBounds = new Rectangle(Width - LogicalToDeviceUnits(82),
                        row.Bounds.Top, LogicalToDeviceUnits(66), row.Bounds.Height);
                    DrawText(graphics, row.Shortcut, _shortcutFont, shortcutBounds, textColor,
                        StringAlignment.Far);
                }
                if (row.IsChecked)
                    DrawCheckmark(graphics, row.Bounds, textColor);
                if (row.IsAppearance)
                    DrawArrow(graphics, row.Bounds, textColor);
            }
        }

        private void AddRow(string text, string? shortcut, Cmd? command, int height, ref int y,
            bool isChecked = false, bool isAppearance = false)
        {
            _rows.Add(new MenuRow(new Rectangle(0, y, Width, height), text, shortcut,
                command, false, isChecked, isAppearance));
            y += height;
        }

        private void AddSeparator(int height, ref int y)
        {
            _rows.Add(new MenuRow(new Rectangle(0, y, Width, height), null, null,
                null, true, false, false));
            y += height;
        }

        private void OnPopupMouseMove(object? sender, MouseEventArgs e)
        {
            int index = _rows.FindIndex(row => !row.IsSeparator && row.Bounds.Contains(e.Location));
            SetHovered(index);
        }

        private void OnPopupMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _hovered < 0) return;
            MenuRow row = _rows[_hovered];
            if (row.IsAppearance)
            {
                _appearanceSubmenuOpen = true;
                AppearanceRequested?.Invoke(this, RectangleToScreen(row.Bounds));
                return;
            }
            if (row.Command is Cmd command)
            {
                CommandChosen?.Invoke(this, command);
                ClosePopup();
            }
        }

        private void OnPopupKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { ClosePopup(); return; }
            if (e.KeyCode is Keys.Down or Keys.Up)
            {
                int direction = e.KeyCode == Keys.Down ? 1 : -1;
                int index = _hovered;
                do { index = (index + direction + _rows.Count) % _rows.Count; }
                while (_rows[index].IsSeparator);
                SetHovered(index);
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Enter && _hovered >= 0)
                OnPopupMouseDown(this, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
        }

        private void ClosePopup()
        {
            if (_closing || IsDisposed || Disposing) return;
            _closing = true;
            Close();
        }

        private void SetHovered(int index)
        {
            if (_hovered == index) return;
            _hovered = index;
            RefreshSurface();
        }

        private void DrawCheckmark(Graphics graphics, Rectangle bounds, Color color)
        {
            int centerX = LogicalToDeviceUnits(22);
            int centerY = bounds.Top + bounds.Height / 2;
            using var pen = new Pen(color, Math.Max(1.5f, DeviceDpi * 1.6f / 96));
            graphics.DrawLines(pen, new[]
            {
                new Point(centerX - 5, centerY), new Point(centerX - 1, centerY + 4),
                new Point(centerX + 6, centerY - 5),
            });
        }

        private void DrawArrow(Graphics graphics, Rectangle bounds, Color color)
        {
            int x = Width - LogicalToDeviceUnits(17);
            int y = bounds.Top + bounds.Height / 2;
            using var brush = new SolidBrush(color);
            graphics.FillPolygon(brush, new[]
            {
                new Point(x - 2, y - 5), new Point(x - 2, y + 5), new Point(x + 3, y),
            });
        }

        private static void DrawText(Graphics graphics, string text, Font font,
            Rectangle bounds, Color color, StringAlignment alignment)
        {
            using var format = new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter,
            };
            using var brush = new SolidBrush(color);
            graphics.DrawString(text, font, brush, bounds, format);
        }

        private sealed record MenuRow(Rectangle Bounds, string? Text, string? Shortcut,
            Cmd? Command, bool IsSeparator, bool IsChecked, bool IsAppearance);
    }

    private sealed class AppearanceMenuPopup : LayeredPopupForm
    {
        private readonly Font _font = new("Segoe UI", 10f);
        private readonly ApplicationTheme _selectedTheme;
        private int _hovered = -1;

        internal event EventHandler<ApplicationTheme>? ThemeChosen;

        internal AppearanceMenuPopup(ApplicationTheme selectedTheme)
        {
            _selectedTheme = selectedTheme;
            Width = LogicalToDeviceUnits(150);
            Height = LogicalToDeviceUnits(62);
            MouseMove += (_, e) =>
            {
                int next = e.Y < Height / 2 ? 0 : 1;
                if (_hovered == next) return;
                _hovered = next;
                RefreshSurface();
            };
            MouseLeave += (_, _) => { _hovered = -1; RefreshSurface(); };
            MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ThemeChosen?.Invoke(this, e.Y < Height / 2
                    ? ApplicationTheme.Light : ApplicationTheme.Dark);
                Close();
            };
            Deactivate += (_, _) => Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }

        protected override void RenderSurface(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            bool lastRowSelected = _hovered == 1;
            using GraphicsPath outline = PopupVisuals.CreateBottomRoundedPath(
                new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), CornerRadius);
            using (var brush = new SolidBrush(lastRowSelected
                ? ThemeManager.MenuHighlight : ThemeManager.Surface))
                graphics.FillPath(brush, outline);
            if (lastRowSelected)
            {
                using var surface = new SolidBrush(ThemeManager.Surface);
                graphics.FillRectangle(surface, new RectangleF(1, 1, Width - 2f,
                    Height / 2f - 1));
            }
            using (var pen = new Pen(ThemeManager.Border)) graphics.DrawPath(pen, outline);

            for (int index = 0; index < 2; index++)
            {
                int top = index * Height / 2;
                bool hover = _hovered == index;
                if (hover && !lastRowSelected)
                {
                    using var brush = new SolidBrush(ThemeManager.MenuHighlight);
                    graphics.FillRectangle(brush, 1, top, Width - 2, Height / 2);
                }
                bool checkedItem = (index == 0 ? ApplicationTheme.Light : ApplicationTheme.Dark) == _selectedTheme;
                Color textColor = hover ? ThemeManager.MenuHighlightText : ThemeManager.Text;
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                })
                using (var brush = new SolidBrush(textColor))
                {
                    graphics.DrawString(index == 0 ? "Light" : "Dark", _font, brush,
                        new Rectangle(LogicalToDeviceUnits(34), top,
                            Width - LogicalToDeviceUnits(46), Height / 2), format);
                }
                if (checkedItem)
                {
                    int y = top + Height / 4;
                    using var pen = new Pen(textColor, Math.Max(1.5f, DeviceDpi * 1.6f / 96));
                    graphics.DrawLines(pen, new[]
                    {
                        new Point(LogicalToDeviceUnits(18), y), new Point(LogicalToDeviceUnits(22), y + 4),
                        new Point(LogicalToDeviceUnits(29), y - 5),
                    });
                }
            }
        }
    }
}
