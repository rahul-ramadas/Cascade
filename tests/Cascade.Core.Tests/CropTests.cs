using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Filtering;
using Cascade.Core.Model;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>A crop is meant to be indistinguishable from a shorter file: every count, every row and every
/// answer about what is on screen has to agree with the same question asked of a file that really did contain
/// only those lines. So the checks here are almost all comparisons against a brute-force walk of the range,
/// at both storage densities and with filters on and off - an off-by-one in the row offset would otherwise
/// show up only as a log scrolled one line out of step.</summary>
public class CropTests
{
    private const int Lines = 40_000;

    private static string WriteLog(int lines = Lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            sb.Append(i % 3 == 0 ? "ERROR " : i % 3 == 1 ? "WARN " : "INFO ");
            if (i % 7 == 0) sb.Append("disk ");
            sb.Append("line ").Append(i).Append('\n');
        }
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void WaitIdle(CascadeDocument doc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!doc.IsFilterIdle && sw.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(2);
    }

    private static CascadeDocument Open(string path, bool filtered, string? pattern = null)
    {
        var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        var filters = new FilterCollection { ShowOnlyFilteredLines = filtered };
        if (pattern is not null) filters.Add(new Filter { Enabled = true, Match = { Text = pattern } });
        doc.SetFilters(filters);
        WaitIdle(doc);
        return doc;
    }

    /// <summary>The lines a crop should show, worked out the slow, obvious way.</summary>
    private static List<long> Expected(CascadeDocument doc, long from, long toExclusive, string? pattern)
    {
        var rows = new List<long>();
        for (long line = from; line < Math.Min(toExclusive, doc.CompletedLineCount); line++)
            if (pattern is null || doc.GetLineText(line).Contains(pattern)) rows.Add(line);
        return rows;
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "ERROR")]
    [InlineData(true, "disk")]
    public void A_cropped_view_shows_exactly_the_rows_a_walk_of_the_range_finds(bool filtered, string? pattern)
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered, pattern);
            var rng = new Random(7);
            for (int trial = 0; trial < 60; trial++)
            {
                long from = rng.Next(0, Lines);
                long to = from + rng.Next(1, Lines - (int)from + 1);
                Assert.True(doc.SetCrop(from, to));

                var expected = Expected(doc, from, to, pattern);
                Assert.Equal(expected.Count, doc.RowCount);
                Assert.Equal(expected.Count, doc.DisplayLineCount is var _ ? doc.RowCount : -1);

                // Every row maps back to the line the walk found, and every line back to its row.
                for (int i = 0; i < expected.Count; i += Math.Max(1, expected.Count / 25))
                {
                    Assert.Equal(expected[i], doc.RowToLine(i));
                    Assert.Equal(i, doc.RowForLine(expected[i]));
                    Assert.True(doc.IsLineVisible(expected[i]));
                }
                // And nothing outside the crop is on show, whatever the filters say about it.
                if (from > 0) Assert.False(doc.IsLineVisible(from - 1));
                if (to < Lines) Assert.False(doc.IsLineVisible(to));
                Assert.Equal(-1, doc.RowForLine(Math.Max(0, from - 1) == from ? Lines : from - 1));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolving_a_screen_gives_the_same_lines_as_asking_row_by_row()
    {
        // The window is resolved in one shot against a single snapshot, by a different route than the row
        // lookups take. The two have to agree, or a frame would be painted a line out from where the caret
        // and the scrollbar believe they are.
        string path = WriteLog();
        try
        {
            foreach (bool filtered in new[] { false, true })
            {
                using var doc = Open(path, filtered, filtered ? "ERROR" : null);
                doc.SetCrop(3_001, 21_507);

                var window = new long[37];
                var rng = new Random(11);
                for (int trial = 0; trial < 40; trial++)
                {
                    long anchorLine = rng.Next(3_001, 21_507);
                    int offset = rng.Next(0, 37);
                    long first = doc.ResolveWindow(anchorLine, offset, window, out int n);

                    Assert.True(first >= 0 && first + n <= doc.RowCount);
                    for (int i = 0; i < n; i++) Assert.Equal(doc.RowToLine(first + i), window[i]);

                    var byRows = new long[37];
                    int m = doc.LinesForRows(first, byRows);
                    Assert.Equal(n, m);
                    for (int i = 0; i < n; i++) Assert.Equal(window[i], byRows[i]);
                }

                // The last screen stops at the crop's end rather than running past it into the file.
                doc.ResolveWindow(long.MaxValue, 0, window, out int tail);
                Assert.Equal(Math.Min(window.Length, doc.RowCount), tail);
                for (int i = 0; i < tail; i++) Assert.True(window[i] < 21_507);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Counts_report_the_crop_and_not_the_file()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered: true, pattern: "ERROR");
            long whole = doc.MatchedLineCount;
            Assert.Equal(Lines, doc.DisplayLineCount);

            doc.SetCrop(1_000, 2_000);
            Assert.Equal(1_000, doc.DisplayLineCount);
            Assert.Equal(Expected(doc, 1_000, 2_000, "ERROR").Count, doc.MatchedLineCount);
            Assert.Equal(1_000, doc.FirstDisplayLine);
            Assert.Equal(1_999, doc.LastDisplayLine);

            // Counting a band of the map stops at the crop's edges too, so the map summarises the crop.
            Assert.Equal(0, doc.MatchedLinesInRange(0, 1_000));
            Assert.Equal(0, doc.MatchedLinesInRange(2_000, Lines));
            Assert.Equal(doc.MatchedLineCount, doc.MatchedLinesInRange(0, Lines));

            doc.ClearCrop();
            Assert.Equal(whole, doc.MatchedLineCount);
            Assert.Equal(Lines, doc.DisplayLineCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_filters_count_is_of_the_crop_while_the_whole_file_stays_available()
    {
        string path = WriteLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            var f = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            filters.Add(f);
            doc.SetFilters(filters);
            WaitIdle(doc);

            long whole = doc.MatchCountFor(f);
            Assert.Equal(Expected(doc, 0, Lines, "ERROR").Count, whole);

            doc.SetCrop(5_000, 6_000);
            long inCrop = doc.MatchCountFor(f, out bool final);
            Assert.True(final);
            Assert.Equal(Expected(doc, 5_000, 6_000, "ERROR").Count, inCrop);
            Assert.True(inCrop < whole);
            // The comparison the reader is offered: what it matches here, against the file it came from.
            Assert.Equal(whole, doc.WholeFileMatchCountFor(f));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Saving_the_current_lines_writes_the_crop()
    {
        string path = WriteLog();
        string outPath = Path.Combine(Path.GetTempPath(), $"crop-save-{Guid.NewGuid():N}.log");
        try
        {
            using var doc = Open(path, filtered: true, pattern: "ERROR");
            doc.SetCrop(900, 1_400);
            await doc.SaveRowsAsync(outPath, null, CancellationToken.None);

            var written = File.ReadAllLines(outPath);
            var expected = Expected(doc, 900, 1_400, "ERROR");
            Assert.Equal(expected.Count, written.Length);
            for (int i = 0; i < expected.Count; i++) Assert.Equal(doc.GetLineText(expected[i]), written[i]);
        }
        finally { File.Delete(path); File.Delete(outPath); }
    }

    [Fact]
    public void A_find_lands_only_inside_the_crop()
    {
        string path = WriteLog();
        try
        {
            foreach (bool filtered in new[] { false, true })
            {
                using var doc = Open(path, filtered, filtered ? "ERROR" : null);
                doc.SetCrop(10_000, 11_000);
                var query = new Find.FindQuery("line 1", false, false);

                // Searching from before the crop cannot reach a line outside it, in either direction.
                long hit = doc.FindLine(query, 0, forward: true, CancellationToken.None);
                Assert.InRange(hit, 10_000, 10_999);
                hit = doc.FindLine(query, Lines - 1, forward: false, CancellationToken.None);
                Assert.InRange(hit, 10_000, 10_999);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_find_tally_counts_only_what_the_crop_shows()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered: false);
            var query = new Find.FindQuery("ERROR", false, false);
            await doc.FindNextAsync(query, 0, true, CancellationToken.None);
            for (long at = 0; ;)
            {
                long hit = await doc.FindNextAsync(query, at, true, CancellationToken.None);
                if (hit < 0) break;
                at = hit + 1;
            }

            var whole = doc.FindTally(0);
            Assert.NotNull(whole);

            doc.SetCrop(2_000, 2_300);
            var cropped = doc.FindTally(2_000);
            Assert.NotNull(cropped);
            long expected = Expected(doc, 2_000, 2_300, "ERROR").Count;
            Assert.Equal(expected, cropped!.Value.VisibleLines);
            Assert.True(cropped.Value.VisibleLines < whole!.Value.VisibleLines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_crop_survives_re_reading_the_same_file_and_not_a_different_one()
    {
        // Re-reading is what F5 does after a log has grown, and losing the crop each time would make it
        // useless on a file being written to. Another file is another set of lines, and a line number means
        // nothing against it - the same reasoning the reference line is never saved for.
        string first = WriteLog(2_000);
        string second = WriteLog(2_000);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(first);
            doc.WaitForIndex();
            doc.SetCrop(500, 800);

            doc.Open(first);              // F5
            doc.WaitForIndex();
            Assert.NotNull(doc.Crop);
            Assert.Equal(300, doc.RowCount);

            doc.Open(second);             // a different log
            doc.WaitForIndex();
            Assert.Null(doc.Crop);
            Assert.Equal(2_000, doc.RowCount);
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public void A_crop_of_one_line_and_a_crop_past_the_end_stay_sane()
    {
        string path = WriteLog(500);
        try
        {
            using var doc = Open(path, filtered: false);

            Assert.True(doc.SetCrop(250, 251));
            Assert.Equal(1, doc.RowCount);
            Assert.Equal(250, doc.RowToLine(0));
            Assert.Equal(250, doc.FirstDisplayLine);
            Assert.Equal(250, doc.LastDisplayLine);

            // Asking for more than there is settles on what there is, rather than inventing rows.
            Assert.True(doc.SetCrop(400, 100_000));
            Assert.Equal(100, doc.RowCount);
            Assert.Equal(499, doc.LastDisplayLine);
            var window = new long[64];
            doc.ResolveWindow(400, 0, window, out int n);
            Assert.Equal(window.Length, n);           // a screenful, since the crop has more than that

            // A crop starting past the end shows nothing at all, and nothing throws for asking.
            Assert.True(doc.SetCrop(100_000, 200_000));
            Assert.Equal(0, doc.RowCount);
            Assert.Equal(-1, doc.RowForLine(0));
            doc.ResolveWindow(0, 0, window, out int none);
            Assert.Equal(0, none);

            Assert.False(doc.SetCrop(300, 300));   // an empty range is not a crop
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_crop_takes_in_lines_the_filters_are_hiding()
    {
        // The crop is a stretch of the FILE, so switching the filters off inside one reveals the lines it
        // always contained rather than a different stretch.
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered: true, pattern: "ERROR");
            doc.SetCrop(600, 700);
            Assert.Equal(Expected(doc, 600, 700, "ERROR").Count, doc.RowCount);

            doc.Filters.ShowOnlyFilteredLines = false;
            doc.ApplyFilters();
            WaitIdle(doc);
            Assert.Equal(100, doc.RowCount);
            Assert.Equal(600, doc.RowToLine(0));
            Assert.Equal(699, doc.RowToLine(99));
        }
        finally { File.Delete(path); }
    }
}
