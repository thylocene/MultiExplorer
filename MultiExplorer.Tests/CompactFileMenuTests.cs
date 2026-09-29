namespace MultiExplorer.Tests;

[CollectionDefinition(nameof(ThemePaletteCollection), DisableParallelization = true)]
public sealed class ThemePaletteCollection;

[Collection(nameof(ThemePaletteCollection))]
public sealed class CompactFileMenuTests
{
    [Fact]
    public void CopyAsPathMenuLabel_ShowsTheApplicationShortcut()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        try
        {
            Assert.True(NativeMethods.AppendMenu(menu,
                NativeMethods.MF_STRING, 42, "Copy as path"));

            Assert.Equal(1,
                PanelView.EnsureCopyAsPathShortcutLabels(menu));

            var label = new System.Text.StringBuilder(128);
            Assert.True(NativeMethods.GetMenuString(menu, 0, label,
                label.Capacity, NativeMethods.MF_BYPOSITION) > 0);
            Assert.Equal("Copy as path\tCtrl+Shift+C", label.ToString());
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Theory]
    [InlineData(96, 20)]
    [InlineData(144, 30)]
    [InlineData(192, 40)]
    [InlineData(240, 50)]
    public void MenuIconSize_ScalesAtTheTargetDpi(int dpi, int expectedPixels)
    {
        Assert.Equal(expectedPixels, NativeMenuIconSet.GetIconPixelSize(dpi));
    }

    [Theory]
    [InlineData(96, 32)]
    [InlineData(144, 48)]
    [InlineData(192, 64)]
    [InlineData(240, 80)]
    public void MenuIconBitmap_AddsDpiScaledTextSpacing(int dpi, int expectedPixels)
    {
        Assert.Equal(expectedPixels, NativeMenuIconSet.GetBitmapPixelWidth(dpi));
    }

    [Theory]
    [InlineData((int)CompactMenuGlyph.MoveToOtherPane)]
    [InlineData((int)CompactMenuGlyph.Favorite)]
    [InlineData((int)CompactMenuGlyph.CopyPath)]
    [InlineData((int)CompactMenuGlyph.Properties)]
    public void CommonMenuGlyphs_HaveVisibleStrokesAndTransparentInteriors(
        int glyphValue)
    {
        using var bitmap = new Bitmap(80, 80);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            NativeMenuIconSet.DrawGlyph(graphics,
                new RectangleF(0, 0, 80, 80), (CompactMenuGlyph)glyphValue);
        }

        int visiblePixels = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A > 64)
                visiblePixels++;
        }

        Assert.InRange(visiblePixels, 150, 2200);
    }

    [Theory]
    [InlineData((int)CompactMenuGlyph.Favorite)]
    [InlineData((int)CompactMenuGlyph.Properties)]
    public void DrawGlyph_AntialiasesEvenWhenGraphicsStartsWithSmoothingDisabled(
        int glyphValue)
    {
        using var bitmap = new Bitmap(20, 20);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

        NativeMenuIconSet.DrawGlyph(graphics, new RectangleF(0, 0, 20, 20),
            (CompactMenuGlyph)glyphValue);

        Assert.Equal(System.Drawing.Drawing2D.SmoothingMode.None,
            graphics.SmoothingMode);
        int edgePixels = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            byte alpha = bitmap.GetPixel(x, y).A;
            if (alpha is > 0 and < 255)
                edgePixels++;
        }

        Assert.True(edgePixels > 10,
            $"Glyph {glyphValue} has no visible anti-aliased edge pixels.");
    }

    [Fact]
    public void DarkMenuGlyph_AntialiasesWhenDrawnDirectlyToTheMenuSurface()
    {
        ApplicationTheme previousTheme = ThemeManager.Current;
        try
        {
            ThemeManager.SetCurrent(ApplicationTheme.Dark);
            using var renderer = new DarkShellMenuRenderer(96);
            using var bitmap = new Bitmap(300, 32);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try
                {
                    renderer.DrawRow(dc, new Rectangle(0, 0, 300, 32), 0,
                        new DarkShellMenuRenderer.MenuItemVisual(
                            "Add to Favorites", false, true, false, false,
                            false, IntPtr.Zero, CompactMenuGlyph.Favorite));
                }
                finally
                {
                    graphics.ReleaseHdc(dc);
                }
            }

            int blendedEdgePixels = 0;
            for (int y = 6; y < 26; y++)
            for (int x = 10; x < 30; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.ToArgb() != ThemeManager.Surface.ToArgb()
                    && pixel.ToArgb() != ThemeManager.AccentText.ToArgb())
                    blendedEdgePixels++;
            }

            Assert.True(blendedEdgePixels > 10);
        }
        finally
        {
            ThemeManager.SetCurrent(previousTheme);
        }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void OppositePaneMenuIcons_RenderAtTargetDpi(int dpi)
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        try
        {
            Assert.True(NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                PanelView.CopyToOtherPaneMenuCommandId, "Copy to opposite pane"));
            Assert.True(NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                PanelView.MoveToOtherPaneMenuCommandId, "Move to opposite pane"));
            using var icons = new NativeMenuIconSet(dpi);
            Assert.True(icons.Apply(menu, PanelView.CopyToOtherPaneMenuCommandId,
                CompactMenuGlyph.CopyToOtherPane));
            Assert.True(icons.Apply(menu, PanelView.MoveToOtherPaneMenuCommandId,
                CompactMenuGlyph.MoveToOtherPane));
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void OppositePaneMenuIcons_RemainVisibleInDarkOwnerDrawMenu(int dpi)
    {
        ApplicationTheme previousTheme = ThemeManager.Current;
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        try
        {
            ThemeManager.SetCurrent(ApplicationTheme.Dark);
            Assert.True(NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                1, "Open"));
            Assert.True(PanelView.AddOppositePaneMenuCommands(menu));
            using var icons = new NativeMenuIconSet(dpi);
            PanelView.PopulateFolderMenuIcons(menu, icons);
            using var renderer = new DarkShellMenuRenderer(dpi);
            renderer.Apply(menu);

            foreach (uint position in new uint[] { 1, 2 })
            {
                Assert.True(renderer.TryGetRegisteredVisual(menu, position,
                    out var visual));
                Assert.NotNull(visual);
                Assert.NotEqual(IntPtr.Zero, visual.Bitmap);
                Assert.Equal(position == 1
                        ? CompactMenuGlyph.CopyToOtherPane
                        : CompactMenuGlyph.MoveToOtherPane,
                    visual.Glyph);
                int size = NativeMenuIconSet.GetIconPixelSize(dpi);
                int rowHeight = renderer.Measure(visual).Height;
                int rowWidth = Math.Max(renderer.Measure(visual).Width,
                    (int)Math.Round(300 * dpi / 96f));
                int iconLeft = (int)Math.Round(10 * dpi / 96f);
                int iconTop = (rowHeight - size) / 2;
                for (int paint = 0; paint < 8; paint++)
                {
                    using var target = new Bitmap(rowWidth, rowHeight);
                    using (Graphics graphics = Graphics.FromImage(target))
                    {
                        IntPtr dc = graphics.GetHdc();
                        try
                        {
                            renderer.DrawRow(dc,
                                new Rectangle(0, 0, rowWidth, rowHeight),
                                paint % 2 == 0 ? 0u : 1u,
                                paint < 4 ? visual : visual with
                                {
                                    Bitmap = IntPtr.Zero,
                                });
                        }
                        finally
                        {
                            graphics.ReleaseHdc(dc);
                        }
                    }

                    int highContrastPixels = 0;
                    for (int y = iconTop; y < iconTop + size; y++)
                    for (int x = iconLeft; x < iconLeft + size; x++)
                    {
                        Color pixel = target.GetPixel(x, y);
                        if ((pixel.R + pixel.G + pixel.B) / 3 > 140)
                            highContrastPixels++;
                    }
                    Assert.True(highContrastPixels > 10,
                        $"Menu icon at position {position} was invisible on paint {paint} at {dpi} DPI.");
                }
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
            ThemeManager.SetCurrent(previousTheme);
        }
    }

    [Theory]
    [InlineData("Restore previous versions", 5)]
    [InlineData("&Properties", 10)]
    [InlineData("Copy &as path", 8)]
    [InlineData("Add to &Favorites", 14)]
    public void DarkMenuDrawsApplicationGlyphWithoutBitmap(string label,
        int expectedGlyph)
    {
        ApplicationTheme previousTheme = ThemeManager.Current;
        IntPtr menu = NativeMethods.CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, menu);
        try
        {
            ThemeManager.SetCurrent(ApplicationTheme.Dark);
            Assert.True(NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                42, label));
            using var renderer = new DarkShellMenuRenderer(96);
            renderer.Apply(menu);
            Assert.True(renderer.TryGetRegisteredVisual(menu, 0, out var visual));
            Assert.NotNull(visual);
            Assert.Equal((CompactMenuGlyph)expectedGlyph, visual.Glyph);
            Assert.Equal(IntPtr.Zero, visual.Bitmap);

            using var target = new Bitmap(300, 32);
            using (Graphics graphics = Graphics.FromImage(target))
            {
                IntPtr dc = graphics.GetHdc();
                try
                {
                    renderer.DrawRow(dc, new Rectangle(0, 0, 300, 32),
                        0, visual);
                }
                finally
                {
                    graphics.ReleaseHdc(dc);
                }
            }

            int brightPixels = 0;
            for (int y = 6; y < 26; y++)
            for (int x = 10; x < 30; x++)
            {
                Color pixel = target.GetPixel(x, y);
                if ((pixel.R + pixel.G + pixel.B) / 3 > 140)
                    brightPixels++;
            }
            Assert.True(brightPixels > 10,
                $"The {label} glyph did not appear in the dark menu.");
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
            ThemeManager.SetCurrent(previousTheme);
        }
    }

    [Theory]
    [InlineData("Give access to", 4)]
    [InlineData("Restore previous versions", 5)]
    [InlineData("Include in library", 6)]
    [InlineData("Pin to Start", 7)]
    [InlineData("Unpin from Start", 7)]
    [InlineData("Pin to Quick access", 7)]
    [InlineData("Unpin from Quick access", 7)]
    [InlineData("Copy as path\tCtrl+Shift+C", 8)]
    [InlineData("Create shortcut", 9)]
    [InlineData("&Properties", 10)]
    [InlineData("Copy to opposite pane\tAlt+C", 12)]
    [InlineData("Move to opposite pane\tAlt+M", 13)]
    public void FolderMenuCommand_MapsToHighDpiGlyph(string label,
        int expected)
    {
        Assert.True(PanelView.TryGetFolderMenuGlyph(label,
            out CompactMenuGlyph glyph));
        Assert.Equal((CompactMenuGlyph)expected, glyph);
    }
}
