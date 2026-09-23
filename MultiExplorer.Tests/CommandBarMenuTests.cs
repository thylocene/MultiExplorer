using System.Collections;
using System.Reflection;

namespace MultiExplorer.Tests;

public sealed class CommandBarMenuTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MoreMenu_ShowsMinimizeToSystemTrayAfterAutoStart(
        bool minimizeToTray)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Type popupType = typeof(CommandBar).GetNestedType(
                    "MoreMenuPopup", BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "The More menu popup type was not found.");
                using var popup = (Form)(Activator.CreateInstance(
                    popupType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args: [false, minimizeToTray, false, false],
                    culture: null)
                    ?? throw new InvalidOperationException(
                        "The More menu popup could not be created."));

                FieldInfo rowsField = popupType.GetField("_rows",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "The More menu rows were not found.");
                object[] rows = ((IEnumerable)(rowsField.GetValue(popup)
                        ?? throw new InvalidOperationException(
                            "The More menu rows were null.")))
                    .Cast<object>()
                    .ToArray();
                string?[] labels = rows.Select(GetRowText).ToArray();
                int autoStartIndex = Array.IndexOf(labels, "Set Auto-start");
                int minimizeIndex = Array.IndexOf(labels,
                    "Minimize to System Tray");

                Assert.True(autoStartIndex >= 0);
                Assert.Equal(autoStartIndex + 1, minimizeIndex);
                Assert.Equal(minimizeToTray,
                    GetRowChecked(rows[minimizeIndex]));
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

    private static string? GetRowText(object row) =>
        row.GetType().GetProperty("Text")?.GetValue(row) as string;

    private static bool GetRowChecked(object row) =>
        row.GetType().GetProperty("IsChecked")?.GetValue(row) as bool?
        ?? throw new InvalidOperationException(
            "The More menu row check state was not found.");
}
