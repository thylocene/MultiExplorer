namespace MultiExplorer.Tests;

public sealed class ExplorerCommandBandTests
{
    [Fact]
    public void PaneHeader_DoesNotDuplicateTheCommandBarViewMenu()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var band = new ExplorerCommandBand();
                Assert.DoesNotContain(band.Items.Cast<ToolStripItem>(),
                    item => item.ToolTipText == "Change view");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void OrganizePopup_ShowsCopyAsPathShortcut()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var band = new ExplorerCommandBand();
                var organize = band.Items.OfType<ToolStripDropDownButton>()
                    .Single(item => item.Text == "Organize");
                var copyAsPath = organize.DropDownItems
                    .OfType<ToolStripMenuItem>()
                    .Single(item => item.Text == "Copy as path");

                Assert.Equal(CommandBar.CopyPathsShortcutText,
                    copyAsPath.ShortcutKeyDisplayString);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Theory]
    [InlineData("Toggle preview pane", CommandBar.Cmd.TogglePreviewPane)]
    [InlineData("MultiExplorer help (F1)", CommandBar.Cmd.Help)]
    public void PaneHeaderButtons_RaiseTheirMultiExplorerCommands(
        string toolTip, CommandBar.Cmd expected)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var band = new ExplorerCommandBand();
                CommandBar.Cmd? issued = null;
                band.CommandIssued += (_, command) => issued = command;

                ToolStripItem button = band.Items
                    .Cast<ToolStripItem>()
                    .Single(item => item.ToolTipText == toolTip);
                button.PerformClick();

                Assert.Equal(expected, issued);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void PreviewButton_ReflectsThePreviewPaneState()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var band = new ExplorerCommandBand();
                var button = (ToolStripButton)band.Items
                    .Cast<ToolStripItem>()
                    .Single(item => item.ToolTipText == "Toggle preview pane");

                band.SetPreviewPaneVisible(true);
                Assert.True(button.Checked);

                band.SetPreviewPaneVisible(false);
                Assert.False(button.Checked);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void PreviewPane_ReservesSpaceInsteadOfCoveringTheFileView()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView { Size = new Size(900, 700) };
                panel.CreateControl();
                panel.PerformLayout();
                panel.ExecuteCommand(CommandBar.Cmd.TogglePreviewPane);
                panel.PerformLayout();

                var content = GetField<Panel>(panel, "_content");
                var host = GetField<Panel>(panel, "_hostContainer");
                var previewRegion = GetField<Panel>(panel, "_previewRegion");
                var preview = GetField<PreviewPane>(panel, "_previewPanel");
                var splitter = GetField<PreviewSplitterBar>(panel, "_previewSplitter");
                var handlerHost = GetField<Panel>(preview, "_handlerHost");
                content.PerformLayout();

                Assert.True(preview.Visible);
                Assert.True(splitter.Visible);
                Assert.Equal(
                    panel.LogicalToDeviceUnits(PanelView.PreviewSplitterWidth),
                    splitter.Width);
                Assert.Same(preview, splitter.Parent);
                Assert.Equal(previewRegion.ClientRectangle, preview.Bounds);
                Assert.Equal(0, splitter.Left);
                Assert.Equal(splitter.Right, preview.ContentInsetLeft);
                Assert.Same(preview, handlerHost.Parent);
                Assert.Equal(splitter.Right, handlerHost.Left);
                Assert.False(splitter.Bounds.IntersectsWith(handlerHost.Bounds));
                Assert.True(host.Width > 0);
                Assert.True(host.Right <= previewRegion.Left,
                    $"Host {host.Bounds} overlapped Preview region {previewRegion.Bounds}.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void PreviewSplitter_RendersASubtleDividerAndGrip()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var splitter = new PreviewSplitterBar
                {
                    Size = new Size(PreviewSplitterBar.BarWidth, 120),
                };
                splitter.CreateControl();

                using var rendered = new Bitmap(splitter.Width, splitter.Height);
                splitter.DrawToBitmap(rendered, splitter.ClientRectangle);

                Assert.Equal(
                    ThemeManager.Surface.ToArgb(),
                    rendered.GetPixel(0, 10).ToArgb());
                Assert.NotEqual(
                    ThemeManager.Window.ToArgb(),
                    rendered.GetPixel(0, 10).ToArgb());
                Assert.Equal(
                    ThemeManager.Border.ToArgb(),
                    rendered.GetPixel(splitter.Width / 2, 10).ToArgb());

                bool hasResizeGlyph = Enumerable.Range(0, splitter.Width)
                    .SelectMany(x => Enumerable.Range(splitter.Height / 2 - 6, 13)
                        .Select(y => rendered.GetPixel(x, y).ToArgb()))
                    .Contains(ThemeManager.MutedText.ToArgb());
                Assert.True(hasResizeGlyph);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void PreviewPane_RestoresPreferredWidthAfterTemporaryConstraint()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView { Size = new Size(1000, 700) };
                panel.CreateControl();
                panel.PerformLayout();
                panel.SetPreviewPaneWidth(420);
                panel.ExecuteCommand(CommandBar.Cmd.TogglePreviewPane);
                panel.PerformLayout();

                var content = GetField<Panel>(panel, "_content");
                var preview = GetField<PreviewPane>(panel, "_previewPanel");
                content.PerformLayout();
                Assert.Equal(420, preview.ContentWidth);
                Assert.Equal(420, panel.PreviewPaneWidth);

                panel.Width = 600;
                panel.PerformLayout();
                content.PerformLayout();
                Assert.True(preview.ContentWidth < 420);
                Assert.Equal(420, panel.PreviewPaneWidth);

                panel.Width = 1000;
                panel.PerformLayout();
                content.PerformLayout();
                Assert.Equal(420, preview.ContentWidth);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void PreviewPane_SplitterMoveUpdatesPreferredWidthAndNotifiesOwner()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView { Size = new Size(1000, 700) };
                panel.CreateControl();
                panel.PerformLayout();
                panel.ExecuteCommand(CommandBar.Cmd.TogglePreviewPane);
                panel.PerformLayout();

                var splitter = GetField<PreviewSplitterBar>(panel, "_previewSplitter");
                int? notifiedWidth = null;
                panel.PreviewPaneWidthChanged += (_, width) =>
                    notifiedWidth = width;

                InvokePrivate(panel, "OnPreviewResizeStarted", splitter, EventArgs.Empty);
                InvokePrivate(panel, "OnPreviewResizeMoved", splitter, -80);
                InvokePrivate(panel, "OnPreviewResizeCompleted", splitter, EventArgs.Empty);
                panel.PerformLayout();

                Assert.Equal(380, panel.PreviewPaneWidth);
                Assert.Equal(380, notifiedWidth);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static T GetField<T>(object instance, string name) where T : class =>
        (T)(instance.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance)
            ?? throw new InvalidOperationException($"Field '{name}' was null."));

    private static void InvokePrivate(object instance, string name, params object?[] arguments) =>
        instance.GetType()
            .GetMethod(name, System.Reflection.BindingFlags.Instance
                             | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(instance, arguments);
}
