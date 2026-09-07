using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Markers;
using Cascade.Core.Model;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>
/// The map and the scrollbar draw marks a band of rows at a time - one pixel stands for anything up to a
/// screenful - and they ask the document which marker belongs to each band. That answer used to be worked out
/// the other way round, by asking every mark in the file which row it was on, which is a rank lookup apiece;
/// with a marker on every line of a large file it cost half a second per repaint, so holding an arrow key
/// stalled a line at a time.
/// <para>Asking per band instead makes the answer cost what the strip is tall, but only if it is the SAME
/// answer. So the checks here are comparisons against the slow walk: every band, in dim and filtered mode,
/// cropped and whole, with marks on hidden lines and marks outside the crop to be left out.</para>
/// </summary>
public class MarkerBandTests
{
    private const int Lines = 20_000;

    private static string WriteLog()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Lines; i++)
            sb.Append(i % 3 == 0 ? "ERROR " : i % 3 == 1 ? "WARN " : "INFO ").Append("line ").Append(i).Append('\n');
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static CascadeDocument Open(string path, bool filtered, string? pattern)
    {
        var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        var filters = new FilterCollection { ShowOnlyFilteredLines = filtered };
        if (pattern is not null) filters.Add(new Filter { Enabled = true, Match = { Text = pattern } });
        doc.SetFilters(filters);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!doc.IsFilterIdle && sw.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(2);
        return doc;
    }

    /// <summary>The marker a band should show, worked out the slow, obvious way: walk its rows, look up the
    /// line behind each, and take the lowest marker any of them carries.</summary>
    private static int Expected(CascadeDocument doc, long fromRow, long toRowExclusive)
    {
        int best = -1;
        for (long row = Math.Max(0, fromRow); row < Math.Min(toRowExclusive, doc.RowCount); row++)
        {
            byte mask = doc.Markers.MaskOf(doc.RowToLine(row));
            if (mask == 0) continue;
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            if (best < 0 || index < best) best = index;
        }
        return best;
    }

    private static void CheckEveryBand(CascadeDocument doc, int step)
    {
        for (long from = 0; from < doc.RowCount; from += step)
            Assert.Equal(Expected(doc, from, from + step), doc.MarkerForRows(from, from + step));
    }

    [Theory]
    [InlineData(false, null, 1)]
    [InlineData(false, null, 32)]
    [InlineData(true, "ERROR", 1)]
    [InlineData(true, "ERROR", 7)]
    [InlineData(true, "ERROR", 32)]
    public void Every_band_names_the_marker_a_walk_of_its_rows_finds(bool filtered, string? pattern, int step)
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered, pattern);
            var rng = new Random(3);

            // Marks on matched and unmatched lines alike: in filtered mode the unmatched ones have no row,
            // and a band must not borrow one from them.
            for (int i = 0; i < 900; i++) doc.Markers.Toggle(rng.Next(Lines), rng.Next(MarkerStore.MarkerCount));
            CheckEveryBand(doc, step);

            doc.SetCrop(4_001, 15_223);
            CheckEveryBand(doc, step);

            doc.ClearCrop();
            CheckEveryBand(doc, step);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_line_marked_twice_shows_its_first_marker_and_so_does_a_band()
    {
        // Lowest index wins within one line, so it has to win across a band too - otherwise the same two
        // marks would show as one colour zoomed in and another zoomed out.
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered: false, pattern: null);
            doc.Markers.Toggle(10, 5);
            doc.Markers.Toggle(10, 2);
            doc.Markers.Toggle(11, 6);

            Assert.Equal(2, doc.MarkerForRows(10, 11));
            Assert.Equal(6, doc.MarkerForRows(11, 12));
            Assert.Equal(2, doc.MarkerForRows(10, 12));
            Assert.Equal(2, doc.MarkerForRows(0, doc.RowCount));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bands_with_nothing_in_them_say_so()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered: false, pattern: null);
            Assert.Equal(-1, doc.MarkerForRows(0, doc.RowCount));   // no marks at all

            doc.Markers.Toggle(500, 0);
            Assert.Equal(-1, doc.MarkerForRows(0, 500));
            Assert.Equal(0, doc.MarkerForRows(500, 501));
            Assert.Equal(-1, doc.MarkerForRows(501, doc.RowCount));

            // Empty and out-of-range bands are asked for while a view is being laid out, and must not throw.
            Assert.Equal(-1, doc.MarkerForRows(500, 500));
            Assert.Equal(-1, doc.MarkerForRows(700, 500));
            Assert.Equal(-1, doc.MarkerForRows(-50, 0));
            Assert.Equal(-1, doc.MarkerForRows(doc.RowCount, doc.RowCount + 1_000));
            Assert.Equal(0, doc.MarkerForRows(-50, doc.RowCount + 1_000));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_mark_outside_the_crop_belongs_to_no_band()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered: false, pattern: null);
            doc.Markers.Toggle(50, 1);       // before the crop
            doc.Markers.Toggle(1_050, 0);    // inside it
            doc.Markers.Toggle(9_000, 2);    // after it
            doc.SetCrop(1_000, 1_300);

            Assert.Equal(300, doc.RowCount);
            Assert.Equal(0, doc.MarkerForRows(50, 51));            // row 50 of the crop is line 1,050
            Assert.Equal(0, doc.MarkerForRows(0, doc.RowCount));
            Assert.Equal(-1, doc.MarkerForRows(51, doc.RowCount));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reading_a_band_of_a_heavily_marked_file_costs_what_the_strip_is_tall()
    {
        // The regression this guards: a marker on every line, a map a few hundred pixels tall, and a repaint
        // that walked all of them. Nothing here should scale with the marks.
        string path = WriteLog();
        try
        {
            using var doc = Open(path, filtered: false, pattern: null);
            for (long line = 0; line < Lines; line++) doc.Markers.Toggle(line, (int)(line % 8));

            const int Slots = 400;
            long step = Math.Max(1, doc.RowCount / Slots);
            doc.MarkerForRows(0, step);   // warm the ordered snapshot, as the first repaint does

            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int repaint = 0; repaint < 20; repaint++)
                for (long from = 0; from < doc.RowCount; from += step)
                    Assert.True(doc.MarkerForRows(from, from + step) >= 0);
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 20_000, $"{allocated:N0} bytes allocated over 20 repaints' worth of asking");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"20 repaints of {Slots} bands took {sw.Elapsed}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_ordered_marks_slice_holds_exactly_the_lines_in_the_range()
    {
        var markers = new MarkerStore();
        foreach (long line in new long[] { 30, 10, 50, 20, 40 }) markers.Toggle(line, 1);

        Assert.Equal([20L, 30L, 40L], markers.Between(20, 41).ToArray().Select(m => m.Line));
        Assert.Equal([10L], markers.Between(long.MinValue, 11).ToArray().Select(m => m.Line));
        Assert.Equal([50L], markers.Between(41, long.MaxValue).ToArray().Select(m => m.Line));
        Assert.Empty(markers.Between(31, 40).ToArray());
        Assert.Empty(markers.Between(41, 20).ToArray());
        Assert.Empty(new MarkerStore().Between(0, 100).ToArray());

        // Exact bounds: the low line is in, the high line is out.
        Assert.Equal([30L, 40L], markers.Between(30, 50).ToArray().Select(m => m.Line));
    }
}
