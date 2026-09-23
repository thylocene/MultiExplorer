namespace MultiExplorer.Tests;

public sealed class FilterInputTests
{
    [Theory]
    [InlineData(Keys.Space, Keys.Control, true)]
    [InlineData(Keys.Space, Keys.None, false)]
    [InlineData(Keys.Space, Keys.Control | Keys.Shift, false)]
    [InlineData(Keys.Enter, Keys.Control, false)]
    public void FilterQuickLookShortcut_RequiresCtrlSpaceOnly(
        Keys keyCode, Keys modifiers, bool expected)
    {
        Assert.Equal(expected,
            PanelView.IsFilterQuickLookShortcut(keyCode, modifiers));
    }

    [Fact]
    public void CtrlSpace_ClosesTheActiveFilterAndSuppressesTheSpaceCharacter()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView();
                var managedList = GetField<ManagedDetailsListView>(panel,
                    "_managedDetailsListView");
                var filterTextBox = GetField<TextBox>(panel, "_filterTextBox");
                var filterListView = GetField<ListView>(panel, "_filterListView");

                typeof(ManagedDetailsListView)
                    .GetMethod("OnKeyPress",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(managedList, [new KeyPressEventArgs('r')]);

                var keyDown = new KeyEventArgs(Keys.Control | Keys.Space);
                typeof(PanelView)
                    .GetMethod("OnFilterTextBoxKeyDown",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(panel, [filterTextBox, keyDown]);

                Assert.Empty(filterTextBox.Text);
                Assert.False(filterListView.Visible);
                Assert.True(keyDown.Handled);
                Assert.True(keyDown.SuppressKeyPress);
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
    public void ManagedDetailsTyping_UpdatesThePanelFilterText()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new PanelView();
                var managedList = GetField<ManagedDetailsListView>(panel,
                    "_managedDetailsListView");
                var filterTextBox = GetField<TextBox>(panel, "_filterTextBox");
                var keyPress = new KeyPressEventArgs('r');

                typeof(ManagedDetailsListView)
                    .GetMethod("OnKeyPress",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(managedList, [keyPress]);

                Assert.Equal("r", filterTextBox.Text);
                Assert.True(keyPress.Handled);
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
}
