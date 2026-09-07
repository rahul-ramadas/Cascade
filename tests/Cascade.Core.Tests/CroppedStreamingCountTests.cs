using System.Diagnostics;
using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Filtering;
using Cascade.Core.Model;
using Cascade.Core.Persistence;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>A count beside a filter has to climb while a pass runs whether or not a crop is on, and a crop's
/// count has to be of the crop. The pass keeps one whole-file total per filter, which cannot answer the second
/// question, so the answer comes from the half-built set of matching LINES instead - and these tests hold a
/// pass at known frontiers to check the number it gives at each one against a walk of the lines it has read.
/// </summary>
public class CroppedStreamingCountTests
{
    private const int Lines = 120_000;      // ~3.7 blocks of 32,768
    private const int Block = 32_768;

    private static string WriteLog()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Lines; i++)
        {
            sb.Append(i % 3 == 0 ? "ERROR " : i % 3 == 1 ? "WARN " : "INFO ");
            if (i % 5 == 0) sb.Append("disk ");
            sb.Append("line ").Append(i).Append('\n');
        }
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>The answer worked out the slow, obvious way: walk the lines and look at each one.</summary>
    private static long Walk(CascadeDocument doc, string text, long from, long toExclusive)
    {
        long n = 0;
        for (long line = Math.Max(0, from); line < Math.Min(toExclusive, doc.CompletedLineCount); line++)
            if (doc.GetLineText(line).Contains(text, StringComparison.Ordinal)) n++;
        return n;
    }

    private static void WaitIdle(CascadeDocument doc)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(2);
        }
        throw new TimeoutException("filtering did not settle");
    }

    /// <summary>Drives a pass one block at a time. <paramref name="atEachBlock"/> is called on the test's own
    /// thread with the pass parked at that frontier, so what it reads cannot move under it.</summary>
    private static void RunHeld(CascadeDocument doc, FilterCollection filters, Action<long> atEachBlock)
    {
        var reached = new SemaphoreSlim(0);
        var go = new SemaphoreSlim(0);
        long frontier = 0;
        doc.FilterCheckpointForTesting = at =>
        {
            Volatile.Write(ref frontier, at);
            reached.Release();
            go.Wait(TimeSpan.FromSeconds(20));
        };
        try
        {
            doc.SetFilters(filters);
            while (reached.Wait(TimeSpan.FromSeconds(20)))
            {
                long at = Volatile.Read(ref frontier);
                try { atEachBlock(at); }
                finally { go.Release(); }
                if (at >= Lines) break;
            }
        }
        finally
        {
            doc.FilterCheckpointForTesting = null;
            go.Release(1_000);          // never leave the worker parked, however this exits
        }
    }

    private static CascadeDocument Open(string path)
    {
        // Every pass must really sweep: rebuilding the view from cached sets is exactly what leaves nothing
        // half-built to read, and it is the settled path these tests are not about.
        var doc = new CascadeDocument { SkipFilterCacheForTesting = true };
        doc.Open(path);
        doc.WaitForIndex();
        return doc;
    }

    private static (FilterCollection Filters, Filter Error) ErrorFilter()
    {
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        var error = new Filter { Enabled = true, Match = { Text = "ERROR" } };
        filters.Add(error);
        return (filters, error);
    }

    [Theory]
    [InlineData(40_000, 80_000)]     // spans several blocks
    [InlineData(0, Lines)]           // the whole file, expressed as a crop
    [InlineData(100_000, 119_999)]   // the last stretch, reached only at the end
    [InlineData(33_000, 33_100)]     // a sliver inside one block
    public void A_cropped_count_climbs_with_the_sweep(int from, int to)
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            doc.SetCrop(from, to);
            var (filters, error) = ErrorFilter();

            long previous = 0;
            int samples = 0;
            RunHeld(doc, filters, at =>
            {
                long got = doc.MatchCountFor(error, out bool final);
                Assert.Equal(Walk(doc, "ERROR", from, Math.Min(to, at)), got);
                Assert.True(got >= previous, $"count went backwards at {at}: {previous} -> {got}");
                Assert.Equal(at >= to, final);
                previous = got;
                samples++;
            });

            Assert.True(samples >= 3, $"expected several blocks, saw {samples}");
            WaitIdle(doc);
            Assert.Equal(Walk(doc, "ERROR", from, to), doc.MatchCountFor(error, out bool settled));
            Assert.True(settled);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The count is of the crop, so it is finished when the SWEEP is past the crop - not when the
    /// pass is past the file. For a crop near the start of a long log that is the difference between a number
    /// that arrives at once and one that arrives when the whole file has been read.</summary>
    [Fact]
    public void A_crop_the_sweep_has_passed_settles_while_the_pass_runs_on()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            doc.SetCrop(1_000, 5_000);
            var (filters, error) = ErrorFilter();

            bool checkedIt = false;
            RunHeld(doc, filters, at =>
            {
                if (at != Block) return;        // the first block already covers the whole crop
                long got = doc.MatchCountFor(error, out bool final);
                Assert.True(final, "a crop the sweep has cleared is finished, whatever is left of the file");
                Assert.Equal(Walk(doc, "ERROR", 1_000, 5_000), got);
                Assert.False(doc.IsFilterIdle, "the pass still has most of the file to read");
                Assert.True(doc.FilterProcessedLineCount < Lines);
                checkedIt = true;
            });

            Assert.True(checkedIt);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Moving a crop must stay free. The pass counts the whole file and the crop is applied when the
    /// number is read, so a crop that moves mid-pass is answered from what has already been swept rather than
    /// by reading a single line again.</summary>
    [Fact]
    public void Moving_the_crop_mid_pass_re_answers_without_re_reading()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            doc.SetCrop(0, 20_000);
            var (filters, error) = ErrorFilter();

            bool checkedIt = false;
            RunHeld(doc, filters, at =>
            {
                if (at != Block * 2) return;
                long scanned = doc.FilterLinesScanned;

                foreach (var (from, to) in new[] { (0, 20_000), (10_000, 30_000), (60_000, 70_000), (5, 6) })
                {
                    Assert.True(doc.SetCrop(from, to));
                    Assert.Equal(Walk(doc, "ERROR", from, Math.Min(to, at)),
                                 doc.MatchCountFor(error, out _));
                }

                Assert.Equal(scanned, doc.FilterLinesScanned);   // not one line re-read
                Assert.Equal(Block * 2, doc.FilterProcessedLineCount);
                checkedIt = true;
            });

            Assert.True(checkedIt);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A crop the sweep has not reached yet reads zero, still counting - never a whole-file number,
    /// which is the one answer a cropped count must never give.</summary>
    [Fact]
    public void A_crop_ahead_of_the_sweep_reads_zero_and_unsettled()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            doc.SetCrop(110_000, 115_000);
            var (filters, error) = ErrorFilter();

            bool checkedIt = false;
            RunHeld(doc, filters, at =>
            {
                if (at != Block) return;
                long got = doc.MatchCountFor(error, out bool final);
                Assert.Equal(0, got);
                Assert.False(final);
                checkedIt = true;
            });

            Assert.True(checkedIt);
            WaitIdle(doc);
            Assert.Equal(Walk(doc, "ERROR", 110_000, 115_000), doc.MatchCountFor(error, out _));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Nesting, excludes and a filter that matches almost every line - the shapes whose sets are
    /// stored differently - all have to agree with a walk of the crop at every frontier.</summary>
    [Fact]
    public void Every_filter_in_a_tree_agrees_with_a_walk_of_the_crop()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            const int From = 20_000, To = 90_000;
            doc.SetCrop(From, To);

            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            var error = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };     // nested under ERROR
            var line = new Filter { Enabled = true, Match = { Text = "line" } };     // matches every line: dense
            var warn = new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "WARN" } };
            filters.Add(error);
            filters.Add(disk, error);
            filters.Add(line);
            filters.Add(warn);

            RunHeld(doc, filters, at =>
            {
                long end = Math.Min(To, at);
                Assert.Equal(Walk(doc, "ERROR", From, end), doc.MatchCountFor(error, out _));
                Assert.Equal(Walk(doc, "line", From, end), doc.MatchCountFor(line, out _));
                Assert.Equal(Walk(doc, "WARN", From, end), doc.MatchCountFor(warn, out _));

                // "disk" is nested, so its count is of lines matching BOTH it and its parent.
                long both = 0;
                for (long l = From; l < end; l++)
                {
                    string text = doc.GetLineText(l);
                    if (text.Contains("ERROR", StringComparison.Ordinal) && text.Contains("disk", StringComparison.Ordinal)) both++;
                }
                Assert.Equal(both, doc.MatchCountFor(disk, out _));
            });
        }
        finally { File.Delete(path); }
    }

    /// <summary>What the streaming path says at the end and what the finished set says must be the same
    /// number - otherwise a count would visibly jump the moment the pass stored its results.</summary>
    [Fact]
    public void The_streaming_answer_and_the_settled_answer_agree()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            var (filters, error) = ErrorFilter();

            long lastStreamed = -1;
            doc.SetCrop(30_000, 100_000);
            RunHeld(doc, filters, at => { if (at >= Lines) lastStreamed = doc.MatchCountFor(error, out _); });

            WaitIdle(doc);
            doc.SkipFilterCacheForTesting = false;
            Assert.Equal(lastStreamed, doc.MatchCountFor(error, out bool final));
            Assert.True(final);
            Assert.Equal(Walk(doc, "ERROR", 30_000, 100_000), lastStreamed);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Nothing about the whole-file count changes: it is still the running total the pass keeps, and
    /// it still climbs to the same answer.</summary>
    [Fact]
    public void An_uncropped_count_is_untouched()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            var (filters, error) = ErrorFilter();

            RunHeld(doc, filters, at =>
            {
                long got = doc.MatchCountFor(error, out bool final);
                Assert.Equal(Walk(doc, "ERROR", 0, at), got);
                Assert.False(final);            // whole-file finality still follows the pass
            });

            WaitIdle(doc);
            Assert.Equal(Walk(doc, "ERROR", 0, Lines), doc.MatchCountFor(error, out bool settled));
            Assert.True(settled);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A switched-off filter counts nothing, crop or no crop: its set records deep matches, which do
    /// not depend on what is enabled, so counting it would report lines it is not putting on screen.</summary>
    [Fact]
    public void A_switched_off_filter_is_not_counted_from_the_pass()
    {
        string path = WriteLog();
        try
        {
            using var doc = Open(path);
            doc.SetCrop(10_000, 50_000);

            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            var on = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            var off = new Filter { Enabled = false, Match = { Text = "WARN" } };
            filters.Add(on);
            filters.Add(off);

            RunHeld(doc, filters, _ => Assert.True(doc.MatchCountFor(off, out bool _) <= 0));
        }
        finally { File.Delete(path); }
    }

    /// <summary>The known limit, kept honest: a chain that cannot be NAMED has no set being built for it, so
    /// there is nothing positional to count and its cropped count never settles. A marker filter naming no
    /// valid marker is the one shape that reaches this, and it matches nothing by design.</summary>
    [Fact]
    public void A_chain_that_cannot_be_named_still_has_no_cropped_count()
    {
        string log = WriteLog();
        string coll = Path.Combine(Path.GetTempPath(), "cascade_" + Guid.NewGuid().ToString("N") + ".cascade");
        File.WriteAllText(coll, """
        {
          "schemaVersion": 2,
          "showOnlyFilteredLines": true,
          "filters": [ { "matchType": "Marker", "enabled": true } ]
        }
        """);
        try
        {
            var filters = CascadeFile.Load(coll).Filters;
            var marker = filters.Roots[0];
            Assert.Equal(-1, marker.Match.MarkerIndex);

            using var doc = Open(log);
            doc.SetFilters(filters);
            WaitIdle(doc);
            Assert.Equal(0, doc.MatchCountFor(marker, out bool final));
            Assert.True(final);

            doc.SetCrop(10_000, 50_000);
            Assert.Equal(-1, doc.MatchCountFor(marker, out bool croppedFinal));
            Assert.False(croppedFinal);
        }
        finally { File.Delete(log); File.Delete(coll); }
    }
}
