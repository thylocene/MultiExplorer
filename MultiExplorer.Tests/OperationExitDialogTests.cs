namespace MultiExplorer.Tests;

public class OperationExitDialogTests
{
    [Fact]
    public void ActionButtonsRemainInsideDialogWhenHeightIsConstrained()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new OperationExitDialog(
                    1, "Copying a file with a deliberately long description");

                // Simulate the reduced client area that exposed the high-DPI
                // bug. The details row must yield space to the action row.
                dialog.MinimumSize = Size.Empty;
                dialog.ClientSize = new Size(760, 230);
                dialog.PerformLayout();

                Control actions = FindControl(dialog, "operationActions");
                Point bottomRight = dialog.PointToClient(
                    actions.PointToScreen(new Point(actions.Width, actions.Height)));

                Assert.True(actions.Height > 0);
                Assert.True(bottomRight.Y <= dialog.ClientSize.Height,
                    $"Action row bottom {bottomRight.Y} exceeded client height {dialog.ClientSize.Height}.");
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

    private static Control FindControl(Control root, string name)
    {
        foreach (Control child in root.Controls)
        {
            if (child.Name == name)
                return child;

            Control? descendant = FindControlOrDefault(child, name);
            if (descendant is not null)
                return descendant;
        }

        throw new InvalidOperationException($"Control '{name}' was not found.");
    }

    private static Control? FindControlOrDefault(Control root, string name)
    {
        foreach (Control child in root.Controls)
        {
            if (child.Name == name)
                return child;

            Control? descendant = FindControlOrDefault(child, name);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }
}
