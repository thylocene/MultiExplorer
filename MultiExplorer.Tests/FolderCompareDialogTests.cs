namespace MultiExplorer.Tests;

public sealed class FolderCompareDialogTests
{
    [Fact]
    public void MatchingFiles_AppearAsReadOnlyRowsWithACompletedStatus()
    {
        RunOnSta(() =>
        {
            using var form = new FolderCompareDialog(@"C:\site1", @"C:\site2",
                (_, _, _) => null);
            var left = new Dictionary<string, FolderItemSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            var right = new Dictionary<string, FolderItemSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            foreach (int index in Enumerable.Range(1, 4))
            {
                string name = $"file-{index}.txt";
                var snapshot = new FolderItemSnapshot(name, false, false, 100,
                    DateTime.UtcNow);
                left.Add(name, snapshot);
                right.Add(name, snapshot);
            }

            form.ShowComparisonResult(new FolderComparisonResult(@"C:\site1",
                @"C:\site2", [], left, right));

            DataGridView grid = Find<DataGridView>(form, "comparisonGrid");
            Assert.Equal(4, grid.Rows.Count);
            Assert.All(grid.Rows.Cast<DataGridViewRow>(), row =>
            {
                Assert.Equal("Same size/time", row.Cells[1].Value);
                Assert.True(string.IsNullOrEmpty(Convert.ToString(row.Cells[4].Value)));
                Assert.True(string.IsNullOrEmpty(Convert.ToString(row.Cells[5].Value)));
                Assert.True(row.ReadOnly);
                Assert.Null(row.Tag);
            });
            Assert.Equal("Compared 4 files per pane; 4 matches shown; no differences.",
                Find<Label>(form, "comparisonStatus").Text);
            Assert.False(Find<RoundedButton>(form,
                "compareSelectSuggested").Enabled);
            Assert.False(Find<RoundedButton>(form,
                "compareReviewActions").Enabled);
        });
    }

    [Fact]
    public void MixedResults_ShowMatchingFilesAlongsideDifferenceRows()
    {
        RunOnSta(() =>
        {
            using var form = new FolderCompareDialog(@"C:\site1", @"C:\site2",
                (_, _, _) => null);
            var left = new Dictionary<string, FolderItemSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            var right = new Dictionary<string, FolderItemSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[] { "alpha.txt", "beta.txt", "gamma.txt" })
            {
                var snapshot = new FolderItemSnapshot(name, false, false, 100,
                    DateTime.UtcNow);
                left.Add(name, snapshot);
                right.Add(name, snapshot);
            }

            var differentLeft = new FolderItemSnapshot("zeta-different.txt",
                false, false, 100, DateTime.UtcNow);
            var differentRight = differentLeft with { Length = 200 };
            left.Add(differentLeft.RelativePath, differentLeft);
            right.Add(differentRight.RelativePath, differentRight);
            var difference = new FolderDifference(differentLeft.RelativePath,
                FolderDifferenceKind.DifferentSize, differentLeft, differentRight);
            form.ShowComparisonResult(new FolderComparisonResult(@"C:\site1",
                @"C:\site2", [difference], left, right));

            DataGridView grid = Find<DataGridView>(form, "comparisonGrid");
            Assert.Equal(4, grid.Rows.Count);
            Assert.Equal(["alpha.txt", "beta.txt", "gamma.txt", "zeta-different.txt"],
                grid.Rows.Cast<DataGridViewRow>()
                    .Select(static row => (string)row.Cells[0].Value!)
                    .ToArray());
            Assert.Equal(3, grid.Rows.Cast<DataGridViewRow>().Count(row =>
                row.ReadOnly && row.Tag is null
                && Equals(row.Cells[1].Value, "Same size/time")));
            DataGridViewRow differenceRow = Assert.Single(
                grid.Rows.Cast<DataGridViewRow>(), row => row.Tag is FolderDifference);
            Assert.False(differenceRow.ReadOnly);
            Assert.Equal("Skip", differenceRow.Cells[4].Value);
            Assert.Equal("Skip", differenceRow.Cells[5].Value);
            Assert.Equal("Compared 4 left / 4 right files; 3 matches shown; "
                + "1 difference; 0 selected.",
                Find<Label>(form, "comparisonStatus").Text);
            grid.Sort(grid.Columns[5],
                System.ComponentModel.ListSortDirection.Ascending);
            Assert.Same(grid.Columns[5], grid.SortedColumn);
            Assert.Equal(4, grid.Rows.Count);
        });
    }

    [Fact]
    public void SuggestedActions_AreChosenInitiallyAndCanBeRestoredAfterManualEdits()
    {
        RunOnSta(() =>
        {
            using var form = new FolderCompareDialog(@"C:\site1", @"C:\site2",
                (_, _, _) => null);
            var left = new Dictionary<string, FolderItemSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            var right = new Dictionary<string, FolderItemSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            var differences = new List<FolderDifference>();
            DateTime baseline = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            foreach ((string name, int leftOffset, int rightOffset) in
                     new[]
                     {
                         ("left-newer.txt", 60, 0),
                         ("right-newer.txt", 0, 60),
                         ("similar-times.txt", 1, 0),
                     })
            {
                var leftItem = new FolderItemSnapshot(name, false, false,
                    100, baseline.AddSeconds(leftOffset));
                var rightItem = new FolderItemSnapshot(name, false, false,
                    200, baseline.AddSeconds(rightOffset));
                left.Add(name, leftItem);
                right.Add(name, rightItem);
                differences.Add(new FolderDifference(name,
                    FolderDifferenceKind.DifferentSize, leftItem, rightItem));
            }

            form.ShowComparisonResult(new FolderComparisonResult(@"C:\site1",
                @"C:\site2", differences, left, right));
            DataGridView grid = Find<DataGridView>(form, "comparisonGrid");
            Assert.Equal("Suggested action", grid.Columns[4].HeaderText);
            Assert.Equal("Chosen action", grid.Columns[5].HeaderText);
            Assert.True(grid.Columns[4].ReadOnly);

            Dictionary<string, string?> suggestions = grid.Rows
                .Cast<DataGridViewRow>()
                .ToDictionary(row => (string)row.Cells[0].Value!,
                    row => (string?)row.Cells[4].Value,
                    StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Copy left → right", suggestions["left-newer.txt"]);
            Assert.Equal("Copy right → left", suggestions["right-newer.txt"]);
            Assert.Equal("Skip", suggestions["similar-times.txt"]);

            Dictionary<string, DataGridViewRow> rows = grid.Rows
                .Cast<DataGridViewRow>()
                .ToDictionary(row => (string)row.Cells[0].Value!,
                    StringComparer.OrdinalIgnoreCase);
            Assert.All(rows, pair =>
                Assert.Equal(pair.Value.Cells[4].Value, pair.Value.Cells[5].Value));
            Assert.Contains("2 selected",
                Find<Label>(form, "comparisonStatus").Text);
            Assert.True(Find<RoundedButton>(form,
                "compareReviewActions").Enabled);

            RoundedButton suggested = Find<RoundedButton>(form,
                "compareSelectSuggested");
            Assert.True(suggested.Enabled);
            Assert.Equal("Restore suggestions", suggested.Text);
            rows["left-newer.txt"].Cells[5].Value = "Skip";
            rows["right-newer.txt"].Cells[5].Value = "Copy left → right";
            rows["similar-times.txt"].Cells[5].Value = "Copy left → right";
            Assert.Equal("Skip", rows["left-newer.txt"].Cells[5].Value);
            Assert.Equal("Copy left → right",
                rows["right-newer.txt"].Cells[5].Value);
            Assert.Equal("Copy left → right",
                rows["similar-times.txt"].Cells[5].Value);

            // The test form is not shown, so PerformClick cannot select it.
            suggested.GetType().GetMethod("OnClick",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(suggested, [EventArgs.Empty]);

            Dictionary<string, string?> actions = grid.Rows
                .Cast<DataGridViewRow>()
                .ToDictionary(row => (string)row.Cells[0].Value!,
                    row => (string?)row.Cells[5].Value,
                    StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Copy left → right", actions["left-newer.txt"]);
            Assert.Equal("Copy right → left", actions["right-newer.txt"]);
            Assert.Equal("Skip", actions["similar-times.txt"]);
            Assert.Contains("2 selected",
                Find<Label>(form, "comparisonStatus").Text);

            RoundedButton skipAll = Find<RoundedButton>(form, "compareSkipAll");
            skipAll.GetType().GetMethod("OnClick",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(skipAll, [EventArgs.Empty]);
            Assert.All(grid.Rows.Cast<DataGridViewRow>(), row =>
                Assert.Equal("Skip", row.Cells[5].Value));
            Assert.Contains("0 selected",
                Find<Label>(form, "comparisonStatus").Text);
            Assert.False(Find<RoundedButton>(form,
                "compareReviewActions").Enabled);
        });
    }

    [Fact]
    public void MissingItemsAreChosenForCopy_WhileConflictsRemainSkipped()
    {
        RunOnSta(() =>
        {
            using var form = new FolderCompareDialog(@"C:\site1", @"C:\site2",
                (_, _, _) => null);
            DateTime modified = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            var leftOnly = new FolderItemSnapshot("left-only.txt", false, false,
                100, modified);
            var rightOnly = new FolderItemSnapshot("right-only.txt", false, false,
                100, modified);
            var leftConflict = new FolderItemSnapshot("conflict", false, false,
                100, modified);
            var rightConflict = new FolderItemSnapshot("conflict", true, false,
                0, modified);
            FolderDifference[] differences =
            [
                new(leftOnly.RelativePath, FolderDifferenceKind.OnlyLeft,
                    leftOnly, null),
                new(rightOnly.RelativePath, FolderDifferenceKind.OnlyRight,
                    null, rightOnly),
                new(leftConflict.RelativePath, FolderDifferenceKind.TypeConflict,
                    leftConflict, rightConflict),
            ];
            form.ShowComparisonResult(new FolderComparisonResult(@"C:\site1",
                @"C:\site2", differences,
                new Dictionary<string, FolderItemSnapshot>
                {
                    [leftOnly.RelativePath] = leftOnly,
                    [leftConflict.RelativePath] = leftConflict,
                },
                new Dictionary<string, FolderItemSnapshot>
                {
                    [rightOnly.RelativePath] = rightOnly,
                    [rightConflict.RelativePath] = rightConflict,
                }));

            DataGridView grid = Find<DataGridView>(form, "comparisonGrid");
            Dictionary<string, DataGridViewRow> rows = grid.Rows
                .Cast<DataGridViewRow>()
                .ToDictionary(row => (string)row.Cells[0].Value!,
                    StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Copy left → right",
                rows["left-only.txt"].Cells[5].Value);
            Assert.Equal("Copy right → left",
                rows["right-only.txt"].Cells[5].Value);
            Assert.Equal("Skip", rows["conflict"].Cells[5].Value);
            Assert.All(rows, pair =>
                Assert.Equal(pair.Value.Cells[4].Value, pair.Value.Cells[5].Value));
            Assert.Contains("2 selected",
                Find<Label>(form, "comparisonStatus").Text);
        });
    }

    [Fact]
    public void Headers_SortAllColumnsWithoutSelectionHighlight()
    {
        RunOnSta(() =>
        {
            using var form = new FolderCompareDialog(@"C:\site1", @"C:\site2",
                (_, _, _) => null);
            DataGridView grid = Find<DataGridView>(form, "comparisonGrid");

            Assert.False(grid.EnableHeadersVisualStyles);
            Assert.Equal(ThemeManager.Surface,
                grid.ColumnHeadersDefaultCellStyle.BackColor);
            Assert.Equal(ThemeManager.Surface,
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor);
            Assert.Equal(ThemeManager.Text,
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor);
            Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column =>
                Assert.Equal(DataGridViewColumnSortMode.Automatic, column.SortMode));

            var first = new FolderItemSnapshot("a.txt", false, false, 1,
                DateTime.UtcNow);
            var second = new FolderItemSnapshot("b.txt", false, false, 1,
                DateTime.UtcNow);
            form.ShowComparisonResult(new FolderComparisonResult(@"C:\site1",
                @"C:\site2", [],
                new Dictionary<string, FolderItemSnapshot>
                {
                    [first.RelativePath] = first,
                    [second.RelativePath] = second,
                },
                new Dictionary<string, FolderItemSnapshot>
                {
                    [first.RelativePath] = first,
                    [second.RelativePath] = second,
                }));

            Assert.Same(grid.Columns[0], grid.SortedColumn);
            Assert.Equal(SortOrder.Ascending, grid.SortOrder);
            grid.Sort(grid.Columns[0], System.ComponentModel.ListSortDirection.Descending);
            Assert.Equal("b.txt", grid.Rows[0].Cells[0].Value);
            Assert.Equal(SortOrder.Descending, grid.SortOrder);
        });
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(2.5f)]
    public void CompareDialog_ScalesAllControlsAndUsesRoundedButtons(float scale)
    {
        RunOnSta(() =>
        {
            using var form = new FolderCompareDialog(
                @"\\192.168.1.116\storage_sdb\MultiExplorer-latest\a very long nested folder name\documents",
                @"\\192.168.1.116\storage_sdb\MultiExplorer-archive\a very long nested folder name\documents",
                (_, _, _) => null);

            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.Equal(new SizeF(96f, 96f), form.AutoScaleDimensions);

            RoundedButton[] buttons =
            [
                Find<RoundedButton>(form, "compareSelectSuggested"),
                Find<RoundedButton>(form, "compareSkipAll"),
                Find<RoundedButton>(form, "compareAgain"),
                Find<RoundedButton>(form, "compareReviewActions"),
                Find<RoundedButton>(form, "compareClose"),
            ];
            DataGridView grid = Find<DataGridView>(form, "comparisonGrid");
            Size originalButtonSize = buttons[0].Size;
            int originalRowHeight = grid.RowTemplate.Height;

            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();

            Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column =>
                Assert.Equal(DataGridViewAutoSizeColumnMode.Fill, column.AutoSizeMode));
            Assert.True(originalRowHeight >= grid.Font.Height);
            if (scale > 1f)
            {
                Assert.True(buttons[0].Width > originalButtonSize.Width);
                Assert.True(buttons[0].Height > originalButtonSize.Height);
            }
            AssertControlsFit(form);
            AssertNoOverlap(buttons);
            Assert.Equal(buttons[3].Top, buttons[4].Top);
            Assert.Equal(buttons[3].Height, buttons[4].Height);
        });
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(2.5f)]
    public void ReviewDialog_ScalesAllControlsAndUsesRoundedButtons(float scale)
    {
        RunOnSta(() =>
        {
            FolderSyncPlanItem[] plan =
            [
                new(FolderSyncAction.CopyLeftToRight,
                    @"C:\Data\Left\long example filename.txt",
                    @"C:\Data\Right",
                    @"C:\Data\Right\long example filename.txt"),
            ];
            using var form = new FolderSyncReviewDialog(plan);

            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.Equal(new SizeF(96f, 96f), form.AutoScaleDimensions);
            RoundedButton start = Find<RoundedButton>(form, "reviewStartOperations");
            RoundedButton cancel = Find<RoundedButton>(form, "reviewCancel");
            Size originalButtonSize = start.Size;

            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();

            if (scale > 1f)
            {
                Assert.True(start.Width > originalButtonSize.Width);
                Assert.True(start.Height > originalButtonSize.Height);
            }
            AssertControlsFit(form);
            AssertNoOverlap([start, cancel]);
        });
    }

    private static T Find<T>(Control parent, string name) where T : Control =>
        Assert.IsType<T>(parent.Controls.Find(name, true).Single());

    private static void AssertControlsFit(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            Assert.True(parent.ClientRectangle.Contains(child.Bounds),
                $"{child.GetType().Name} {child.Name} bounds {child.Bounds} exceed "
                + $"{parent.GetType().Name} {parent.Name} client area {parent.ClientRectangle}.");
            AssertControlsFit(child);
        }
    }

    private static void AssertNoOverlap(IReadOnlyList<RoundedButton> buttons)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            for (int j = i + 1; j < buttons.Count; j++)
            {
                if (buttons[i].Parent != buttons[j].Parent) continue;
                Assert.False(buttons[i].Bounds.IntersectsWith(buttons[j].Bounds),
                    $"Buttons {buttons[i].Name} and {buttons[j].Name} overlap.");
            }
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
