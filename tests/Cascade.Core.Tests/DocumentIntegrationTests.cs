using System.Diagnostics;
using System.Text;
using Cascade.Core.Document;
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
