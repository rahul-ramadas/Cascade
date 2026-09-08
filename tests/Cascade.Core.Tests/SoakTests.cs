using System.Diagnostics;
using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>
/// The engine over a log big enough to behave like a real one, run nightly rather than on every push.
///
/// <para>Everything else in this suite works on a few thousand lines, which is right - a push has to be
/// answered in seconds. But several whole mechanisms only exist above a certain size and are simply not
/// reached down there: the line index only pages at 65,536 lines a page, so its compact shape never comes
/// into being; a filter pass over four thousand lines finishes before anything can observe it streaming;
/// and the match cache's word alignment only matters when indexing has not finished before filtering
/// starts. Every one of those has carried a real bug, and none of them could have been caught by a test
/// that runs in a second.</para>
///
/// <para>Gated on CASCADE_SOAK so it costs a push nothing. CASCADE_SOAK_LINES sizes it; the default is
/// chosen to be a few index pages rather than to be impressive, because what is being exercised is the
/// paging, not the gigabyte.</para>
/// </summary>
public class SoakTests : IDisposable
{
    private static bool Asked => Environment.GetEnvironmentVariable("CASCADE_SOAK") == "1";

    private static long Lines =>
        long.TryParse(Environment.GetEnvironmentVariable("CASCADE_SOAK_LINES"), out long n) && n > 0
            ? n
            : 3_000_000;

    private string? _log;

    // Gated in the teardown as well as in the tests. xUnit constructs and disposes the class whatever the
    // test decides to do, and a Dispose that assumed the file existed would fail on every machine that
    // opted out - which is a failure to clean up after doing nothing on purpose.
    public void Dispose()
    {
        if (_log is not null) { try { File.Delete(_log); } catch { /* best effort */ } }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A log whose contents are arithmetic, so what any filter or search should find is known exactly
    /// without asking the engine. Every line carries its own number, and the tokens repeat on a fixed
    /// cycle - the same trick the small fixtures use, at a size where it means something.
    /// </summary>
    private string WriteLog(long lines)
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_soak_" + Guid.NewGuid().ToString("N") + ".log");
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false), 1 << 20))
            for (long i = 0; i < lines; i++)
            {
                string service = Services[(int)(i % Services.Length)];
                string level = i % 1000 == 0 ? "ERROR" : i % 25 == 0 ? "WARN " : "INFO ";
                // Every hundred thousandth line is enormous, so the index meets a page it cannot describe
                // in two bytes a line and has to fall back - the path that is otherwise never taken.
                string tail = i % 100_000 == 99_999 ? new string('x', 70_000) : "";
                writer.Write($"[{at.AddSeconds(i):yyyy-MM-ddTHH:mm:ss}][{service}][{level}] request {i} handled{tail}\n");
            }

        return path;
    }

    private static readonly string[] Services = ["api-gateway", "order-service", "payment-svc", "db-pool"];

    private static long Every(long lines, long n) => (lines + n - 1) / n;

    [Fact]
    public void A_large_log_indexes_filters_and_searches_to_the_answers_arithmetic_gives()
    {
        if (!Asked) return;

        long lines = Lines;
        _log = WriteLog(lines);
        var clock = Stopwatch.StartNew();

        // The real startup order: filters first, file second. That is what once left the most important
        // pass of all unrecorded, because every test until then had waited for the index before filtering.
        var filters = new FilterCollection();
        var errors = new Filter { Enabled = true, Match = { Text = "[ERROR]" } };
        var payments = new Filter { Enabled = false, Match = { Text = "[payment-svc]" } };
        filters.Add(errors);
        filters.Add(payments);

        using var doc = new CascadeDocument();
        doc.SetFilters(filters);
        doc.Open(_log);
        doc.WaitForIndex();
        WaitForFilters(doc);

        Assert.Equal(lines, doc.CompletedLineCount);

        // The index is big enough to page, so its compact shape must really be the one in use. Every
        // answer stays correct if it silently reverts to four bytes a line; only the footprint changes,
        // and only a heap dump would show it.
        Assert.True(doc.CompletedLineCount > Cascade.Core.Indexing.LineIndex.BlockLinesForTesting * 8,
                    "the fixture is too small to exercise the index's paging at all");

        // And a page that had to fall back to wider offsets still reads back exactly. The 70,000-character
        // lines are what force that: a block of them spans more than the 65,535 bytes a two-byte distance
        // can describe, so the page holding one is rebuilt in the wider shape.
        const long Huge = 99_999;
        Assert.True(lines > Huge, "the fixture must be long enough to contain one of the enormous lines");
        // Compared against the line four back, not the one before: the service name cycles every four
        // lines, and "db-pool" against "payment-svc" is a four-character difference that has nothing to do
        // with what is being measured.
        Assert.Equal(70_000, doc.GetLineText(Huge).Length - doc.GetLineText(Huge - 4).Length);
        Assert.EndsWith(new string('x', 100), doc.GetLineText(Huge), StringComparison.Ordinal);

        // What the filters should find, from the generator's arithmetic rather than from the engine.
        Assert.Equal(Every(lines, 1000), doc.MatchedLineCount);

        // Toggling from the cache has to agree with evaluating from scratch, at a size where the pass
        // streams and the cache is built from partial information.
        long before = doc.MatchedLineCount;
        payments.Enabled = true;
        doc.ApplyFilters();
        WaitForFilters(doc);
        long both = doc.MatchedLineCount;
        Assert.True(both > before, "switching on a second filter must show more lines, not fewer");

        payments.Enabled = false;
        doc.ApplyFilters();
        WaitForFilters(doc);
        Assert.Equal(before, doc.MatchedLineCount);

        // A find over the whole file, counted against the same arithmetic. "WARN " is on every 25th line
        // except where ERROR takes it, which is every 1000th - and 1000 is a multiple of 25.
        long warns = Every(lines, 25) - Every(lines, 1000);
        var tally = FindTally(doc, "[WARN ]");
        Assert.Equal(warns, tally);

        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(20), $"the soak took {clock.Elapsed}");
    }

    /// <summary>
    /// Opening one file after another, over and over, must not leave anything behind. Each round maps a
    /// multi-gigabyte view and starts an indexer, a filter worker and a find sweep; anything that outlives
    /// its document holds the mapping open, and the symptom in the field is a process that grows all day.
    /// </summary>
    [Fact]
    public void Opening_the_same_log_over_and_over_settles_rather_than_growing()
    {
        if (!Asked) return;

        _log = WriteLog(Math.Min(Lines, 400_000));
        var filters = new FilterCollection();
        filters.Add(new Filter { Enabled = true, Match = { Text = "[ERROR]" } });

        long settled = 0;
        for (int round = 0; round < 12; round++)
        {
            using (var doc = new CascadeDocument())
            {
                doc.SetFilters(filters);
                doc.Open(_log);
                doc.WaitForIndex();
                WaitForFilters(doc);
                _ = doc.GetLineText(doc.CompletedLineCount / 2);
            }

            // A document per round, out of scope before this runs: `using` inside the loop is what makes
            // that true. A `using` on the loop variable itself keeps the PREVIOUS round's document alive
            // until the next assignment, which reads as a leak and is not one.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long now = GC.GetTotalMemory(forceFullCollection: true);

            // The first few rounds settle the allocator; compare the last against the middle.
            if (round == 5) settled = now;
            else if (round == 11)
                Assert.True(now < settled * 2,
                            $"the managed heap went from {settled:N0} bytes at round 5 to {now:N0} at round 11");
        }
    }

    private static void WaitForFilters(CascadeDocument doc)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(10))
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException("the filter pass never settled");
    }

    private static long FindTally(CascadeDocument doc, string term)
    {
        var query = new Cascade.Core.Find.FindQuery(term, Regex: false, CaseSensitive: false);
        var task = doc.FindNextAsync(query, 0, true);
        task.Wait(TimeSpan.FromMinutes(10));

        var clock = Stopwatch.StartNew();
        while (!doc.FindComplete && clock.Elapsed < TimeSpan.FromMinutes(10)) Thread.Sleep(5);
        Assert.True(doc.FindComplete, "the sweep never finished");

        var tally = doc.FindTally(0);
        Assert.NotNull(tally);
        return tally.Value.VisibleLines + tally.Value.HiddenLines;
    }
}
