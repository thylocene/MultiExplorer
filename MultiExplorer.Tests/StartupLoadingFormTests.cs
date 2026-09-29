namespace MultiExplorer.Tests;

public sealed class StartupLoadingFormTests
{
    [Fact]
    public void LoadingForm_IsDpiAwareAndScalesItsContent()
    {
        RunOnSta(() =>
        {
            using var form = new StartupLoadingForm(applicationIcon: null);
            form.SetMessage(
                "Preparing files, folders, and navigation in the second pane…");
            Label title = Assert.IsType<Label>(
                form.Controls.Find("startupLoadingTitle", true).Single());
            Label message = Assert.IsType<Label>(
                form.Controls.Find("startupLoadingMessage", true).Single());
            ProgressBar progress = Assert.IsType<ProgressBar>(
                form.Controls.Find("startupLoadingProgress", true).Single());
            Size originalFormSize = form.Size;

            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.Equal(new SizeF(96F, 96F), form.AutoScaleDimensions);
            Assert.Equal(ProgressBarStyle.Marquee, progress.Style);
            Assert.Equal(4, progress.Height);

            form.Scale(new SizeF(1.5F, 1.5F));
            form.PerformLayout();

            Assert.True(form.Width > originalFormSize.Width);
            Assert.True(form.Height > originalFormSize.Height);
            Assert.True(message.Width > 0);
            Assert.True(message.Height > 0);
            Assert.True(title.Bottom <= message.Top);
            Assert.True(message.Bottom <= progress.Top);
        });
    }

    [Theory]
    [InlineData(1F)]
    [InlineData(1.5F)]
    [InlineData(2F)]
    [InlineData(2.5F)]
    public void LoadingForm_ControlsDoNotOverlapAtHighDpi(float scale)
    {
        RunOnSta(() =>
        {
            using var form = new StartupLoadingForm(applicationIcon: null);
            form.SetMessage(
                "Preparing files, folders, and navigation in the second pane…");
            Label title = Assert.IsType<Label>(
                form.Controls.Find("startupLoadingTitle", true).Single());
            Label message = Assert.IsType<Label>(
                form.Controls.Find("startupLoadingMessage", true).Single());
            ProgressBar progress = Assert.IsType<ProgressBar>(
                form.Controls.Find("startupLoadingProgress", true).Single());

            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();

            Assert.True(title.Bottom <= message.Top,
                $"Title {title.Bounds} overlaps message {message.Bounds}.");
            Assert.True(message.Bottom <= progress.Top,
                $"Message {message.Bounds} overlaps progress {progress.Bounds}.");
            Assert.True(progress.Right <= form.ClientRectangle.Right,
                $"Progress {progress.Bounds} exceeds client {form.ClientRectangle}.");
            Assert.True(progress.Bottom <= form.ClientRectangle.Bottom,
                $"Progress {progress.Bounds} exceeds client {form.ClientRectangle}.");
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
}
