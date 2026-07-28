using System.Diagnostics;
using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

public class DocumentIntegrationTests
{
    private static void WaitFilter(CascadeDocument doc, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException("Filtering did not become idle in time.");
    }

    [Fact]
    public void Filter_find_reports_progress_and_honors_cancellation()
    {
        // Scanning for a filter's next match decodes every line, so on a large file it has to be able to
        // report how far it has got and to be stopped part-way; otherwise the window just freezes.
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sb.Append("nope ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            var filters = new FilterCollection();
            var absent = new Filter { Enabled = true, Match = { Text = "absent-text" } };
            filters.Add(absent);
            doc.SetFilters(filters);

            int reports = 0;
            double last = -1;
            long found = doc.FindLineMatchingFilter(absent, 0, forward: true, CancellationToken.None,
                f => { reports++; last = f; });

            Assert.Equal(-1, found);
            Assert.True(reports > 1, $"expected multiple progress callbacks, got {reports}");
            Assert.InRange(last, 0.0, 1.0);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                doc.FindLineMatchingFilter(absent, 0, forward: true, cts.Token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetFilters_before_a_file_is_open_does_not_throw()
    {
        // Auto-loading a saved filter set at startup happens BEFORE any file is open (the filter service
        // isn't created until Open()). Enabled filters must not trigger a NullReferenceException here;
        // they should simply take effect once a file is opened.
        using var doc = new CascadeDocument();

        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        filters.Add(new Filter { Enabled = true, Match = { Text = "ERROR" } });

        doc.SetFilters(filters); // must not throw

        Assert.True(doc.FilteredMode);
        Assert.Equal(0, doc.RowCount); // nothing open yet
    }

    [Fact]
    public void Filters_loaded_before_open_take_effect_after_open()
    {
        string[] lines = { "ERROR one", "info two", "ERROR three", "debug four" };
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

        using var doc = new CascadeDocument();
        try
        {
            // Load filters first (as startup auto-load does), THEN open the file.
            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            filters.Add(new Filter { Enabled = true, Match = { Text = "ERROR" } });
            doc.SetFilters(filters);

            doc.Open(path);
            doc.WaitForIndex();
            WaitFilter(doc);

            Assert.True(doc.FilteredMode);
            Assert.Equal(2, doc.MatchedLineCount);
            Assert.Equal(2, doc.RowCount);
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Disabling_all_filters_cancels_the_running_pass_cleanly()
    {
        // Large enough that a filter pass cannot complete in the microseconds between the two
        // ApplyFilters calls below, so the "disable all" happens while a pass is genuinely running.
        var sb = new StringBuilder();
        for (int i = 0; i < 1_000_000; i++)
            sb.Append(i % 3 == 0 ? "ERROR x" : "info x").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var f = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            doc.Filters.Add(f);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();     // kick off a real pass over 1,000,000 lines

            f.Enabled = false;      // ...then immediately remove all enabled filters
            doc.ApplyFilters();

            // With no enabled filters the previous pass must be cancelled at once, so filtering is idle
            // immediately — otherwise the status bar stays "busy" with a frozen progress bar until the
            // orphaned run finishes on its own.
            Assert.True(doc.IsFilterIdle, "disabling all filters must cancel the running pass immediately");
            Assert.Equal(0, doc.FilterProcessedLineCount);
        }
        finally { doc.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void Rapid_filter_changes_settle_without_getting_stuck_busy()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++)
            sb.Append(i % 5 == 0 ? "MATCH " : "other ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var a = new Filter { Enabled = true, Match = { Text = "MATCH" } };
            var b = new Filter { Enabled = true, Match = { Text = "other" } };
            doc.Filters.Add(a);
            doc.Filters.Add(b);
            doc.Filters.ShowOnlyFilteredLines = true;

            // Hammer ApplyFilters with rapidly changing enabled states, never waiting for completion —
            // this includes transient "nothing enabled" states.
            var rnd = new Random(1234);
            for (int i = 0; i < 300; i++)
            {
                a.Enabled = rnd.Next(2) == 0;
                b.Enabled = rnd.Next(2) == 0;
                doc.ApplyFilters();
            }

            // End in a known state and let it settle.
            a.Enabled = true;
            b.Enabled = false;
            doc.ApplyFilters();

            var sw = Stopwatch.StartNew();
            while (!doc.IsFilterIdle && sw.ElapsedMilliseconds < 20000) Thread.Sleep(2);
            Assert.True(doc.IsFilterIdle, "filtering never settled after rapid changes");
            Assert.Equal(40_000, doc.MatchedLineCount); // every 5th line matches "MATCH"
        }
        finally { doc.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void Opening_a_new_file_while_a_find_runs_does_not_read_the_freed_mmap()
    {
        // A long scan on file A must be cancelled and joined before A's memory-mapped file is freed by
        // opening file B — otherwise the background find would read freed memory (AccessViolation crash).
        var sb = new StringBuilder();
        for (int i = 0; i < 2_000_000; i++) sb.Append("nope ").Append(i).Append('\n');
        string a = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        string b = Harness.TempFile(Encoding.UTF8.GetBytes("hello\nworld\n"));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(a);
            doc.WaitForIndex();
            var find = doc.FindLineAsync(new FindQuery("absent-string", false, false), 0, true); // long scan

            doc.Open(b); // disposes A's mmap; must cancel + join the find first
            doc.WaitForIndex();

            Assert.Equal(2, doc.CompletedLineCount);
            try { find.Wait(3000); } catch (AggregateException) { /* OperationCanceledException is fine */ }
            Assert.True(find.IsCompleted);
        }
        finally { doc.Dispose(); File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Changing_filters_keeps_every_line_resolvable_while_the_new_pass_streams()
    {
        // Big enough that the new pass cannot finish in the microseconds after ApplyFilters returns.
        var sb = new StringBuilder();
        for (int i = 0; i < 2_000_000; i++) sb.Append(i % 10 == 0 ? "MATCH " : "other ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            long total = doc.CompletedLineCount;

            var filter = new Filter { Enabled = true, Match = { Text = "MATCH" } };
            doc.Filters.Add(filter);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            long matches = doc.MatchedLineCount;
            long deep = total - 1000;
            long rowBefore = doc.RowAtOrAfterLine(deep);
            Assert.True(rowBefore > 0);

            // Change the filters. Because the visible set is updated IN PLACE rather than rebuilt, a line
            // deep in the file still maps to a real row immediately - the UI can hold the user's position
            // instead of waiting for the sweep to reach it. (Rebuilding would report row 0 here.)
            filter.Match.Text = "other";
            doc.ApplyFilters();
            long rowDuring = doc.RowAtOrAfterLine(deep);
            long knownDuring = doc.ViewKnownThroughLine;

            Assert.Equal(total, knownDuring);
            Assert.True(Math.Abs(rowDuring - rowBefore) < 100_000,
                $"deep line should stay near its row while re-filtering (was {rowBefore}, now {rowDuring})");

            // ...and the pass still converges to exactly the new result: 9 of every 10 lines say "other".
            WaitFilter(doc);
            Assert.Equal(matches * 9, doc.MatchedLineCount);
            Assert.Equal(total, doc.ViewKnownThroughLine);
        }
        finally { doc.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void Open_index_filter_and_map_rows()
    {
        string[] lines =
        {
            "ERROR one", "info two", "ERROR three", "debug four", "warn five", "ERROR six"
        };
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            Assert.Equal(6, doc.CompletedLineCount);

            // No filters yet → dim mode shows everything, all "match".
            Assert.False(doc.FilteredMode);
            Assert.Equal(6, doc.RowCount);
            Assert.Equal(6, doc.MatchedLineCount);
            Assert.Equal("ERROR three", doc.GetLineText(2));

            // Enable an ERROR filter and switch to filtered mode.
            var error = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            doc.Filters.Add(error);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            Assert.Equal(3, doc.MatchedLineCount);
            Assert.True(doc.FilteredMode);
            Assert.Equal(3, doc.RowCount);
            Assert.Equal(0, doc.RowToLine(0));
            Assert.Equal(2, doc.RowToLine(1));
            Assert.Equal(5, doc.RowToLine(2));

            var eval = doc.EvaluateText("ERROR one".AsSpan(), 0);
            Assert.True(eval.Shown);
            Assert.Same(error, eval.ColorFilter);
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Exclude_filter_removes_lines()
    {
        string[] lines = { "keep 1", "drop me", "keep 2", "drop me too", "keep 3" };
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            doc.Filters.Add(new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "drop" } });
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            Assert.Equal(3, doc.RowCount);
            Assert.Equal(0, doc.RowToLine(0));
            Assert.Equal(2, doc.RowToLine(1));
            Assert.Equal(4, doc.RowToLine(2));
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Hierarchical_streaming_matches_reference()
    {
        // 3,000 mixed lines; verify the streamed matched set equals a direct evaluation.
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            string kind = (i % 3) switch { 0 => "Error disk", 1 => "Error net", _ => "info" };
            sb.Append(kind).Append(' ').Append(i).Append('\n');
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            // Parent disabled, child enabled: the child still requires the parent's predicate, so only
            // lines matching BOTH "Error" and "disk" (every 3rd line) are shown.
            var error = new Filter { Enabled = false, Match = { Text = "Error" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };
            doc.Filters.Add(error);
            doc.Filters.Add(disk, error);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            // Every 3rd line (i % 3 == 0) is "Error disk".
            Assert.Equal(1000, doc.MatchedLineCount);
            for (long row = 0; row < doc.RowCount; row++)
                Assert.Equal(0, doc.RowToLine(row) % 3);
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Per_filter_match_counts()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            string kind = (i % 3) switch { 0 => "Error disk", 1 => "Error net", _ => "info" };
            sb.Append(kind).Append(' ').Append(i).Append('\n');
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var error = new Filter { Enabled = true, Match = { Text = "Error" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };
            doc.Filters.Add(error);
            doc.Filters.Add(disk, error);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            // Error deep-matches i%3 in {0,1} = 2000; Disk deep-matches (Error AND disk) i%3==0 = 1000.
            Assert.Equal(2000, doc.MatchCountFor(error));
            Assert.Equal(1000, doc.MatchCountFor(disk));
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void FindLineMatchingFilter_navigates_forward_and_back()
    {
        string[] lines = { "info 0", "ERROR 1", "info 2", "info 3", "ERROR 4", "info 5" };
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var error = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            doc.Filters.Add(error);
            doc.ApplyFilters();
            WaitFilter(doc);

            // Forward from the top finds line 1, then line 4, then nothing.
            Assert.Equal(1, doc.FindLineMatchingFilter(error, 0, forward: true, CancellationToken.None));
            Assert.Equal(4, doc.FindLineMatchingFilter(error, 2, forward: true, CancellationToken.None));
            Assert.Equal(-1, doc.FindLineMatchingFilter(error, 5, forward: true, CancellationToken.None));

            // Backward from the bottom finds line 4, then line 1, then nothing.
            Assert.Equal(4, doc.FindLineMatchingFilter(error, 5, forward: false, CancellationToken.None));
            Assert.Equal(1, doc.FindLineMatchingFilter(error, 3, forward: false, CancellationToken.None));
            Assert.Equal(-1, doc.FindLineMatchingFilter(error, 0, forward: false, CancellationToken.None));
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void FindLineMatchingFilter_respects_hierarchy_and_disabled_state()
    {
        string[] lines = { "Error disk 0", "Error net 1", "info 2", "Error disk 3", "info 4" };
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            // Parent disabled, child enabled: find still requires BOTH predicates (deep match).
            var error = new Filter { Enabled = false, Match = { Text = "Error" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };
            doc.Filters.Add(error);
            doc.Filters.Add(disk, error);
            doc.ApplyFilters();
            WaitFilter(doc);

            // "Error disk" lines are 0 and 3 (line "Error net 1" fails the child's "disk" predicate).
            Assert.Equal(0, doc.FindLineMatchingFilter(disk, 0, forward: true, CancellationToken.None));
            Assert.Equal(3, doc.FindLineMatchingFilter(disk, 1, forward: true, CancellationToken.None));
            Assert.Equal(3, doc.FindLineMatchingFilter(disk, 4, forward: false, CancellationToken.None));
            Assert.Equal(0, doc.FindLineMatchingFilter(disk, 2, forward: false, CancellationToken.None));
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }
}
