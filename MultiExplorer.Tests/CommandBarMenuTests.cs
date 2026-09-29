using System.Collections;
using System.Reflection;

namespace MultiExplorer.Tests;

public sealed class CommandBarMenuTests
{
    [Fact]
    public void MoreMenu_ConsolidatesPreferencesIntoSettings()
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
                    args: [false, false, false, false],
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
                Assert.Single(labels, "Settings");
                Assert.DoesNotContain("Explorer options", labels);
                Assert.DoesNotContain("Set appearance", labels);
                Assert.DoesNotContain("Set hotkey", labels);
                Assert.DoesNotContain("Set Auto-start", labels);
                Assert.DoesNotContain("Minimize to System Tray", labels);
                Assert.DoesNotContain("QuickLook integration (Space)", labels);
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
}
