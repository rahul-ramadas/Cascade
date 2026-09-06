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

    private static string WriteLines(string word, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(word).Append(" line ").Append(i).Append('\n');
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [Fact]
    public async Task Opening_another_file_does_not_wait_for_the_old_one_to_be_let_go()
    {
        // Letting go of a mapping makes the kernel hand back every resident page one at a time - MEASURED at
        // 973 ms for a 15.8 GB log - and it used to run on the thread that draws, so the window sat frozen
        // for a second every time another file was opened. The gate stands in for that wait.
        string a = WriteLines("alpha", 40), b = WriteLines("bravo", 90);
        using var held = new ManualResetEventSlim(false);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(a);
            doc.WaitForIndex();

            doc.ReleaseDelayForTesting = () => held.Wait(TimeSpan.FromSeconds(10));
            var sw = Stopwatch.StartNew();
            doc.Open(b);
            sw.Stop();
            doc.WaitForIndex();

            Assert.True(sw.ElapsedMilliseconds < 2000,
                        $"opening waited for the old file to be released ({sw.ElapsedMilliseconds} ms)");
            Assert.False(doc.ReleasePending.IsCompleted, "the old mapping should still be being let go");

            // And the new file is completely usable while that is still going on.
            Assert.Equal(90, doc.CompletedLineCount);
            Assert.StartsWith("bravo", doc.GetLineText(0), StringComparison.Ordinal);

            held.Set();
            await doc.ReleasePending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            held.Set();
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void Opening_a_file_does_not_inherit_the_last_one_s_filtering()
    {
        // The first pass over a file starts from "every line visible" and narrows, so the reader sees their
        // lines straight away. That is decided by whether a pass is already running, which belongs to the
        // file being let go - inherited, a newly opened file would be treated as already filtered.
        string a = WriteLines("alpha", 40), b = WriteLines("bravo", 90);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(a);
            doc.WaitForIndex();

            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            var f = new Filter { Enabled = true, Match = { Text = "line 1" } };
            filters.Add(f);
            doc.SetFilters(filters);
            WaitFilter(doc);
            Assert.True(doc.CurrentPassSeededFromEverything, "the first pass over a file seeds from everything");

            f.Match.Text = "line 2";
            doc.SetFilters(filters);
            WaitFilter(doc);
            Assert.False(doc.CurrentPassSeededFromEverything, "a later change carries on from the last result");

            doc.Open(b);
            WaitFilter(doc);
            Assert.True(doc.CurrentPassSeededFromEverything, "a new file starts over");
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Filter_find_reports_progress_and_honors_cancellation()
    {
        // Finding a filter's next match runs the same pass that enabling the filter would, and remembers the
        // result: the first search does the work (and reports progress), every later one is a bit scan.
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sb.Append("nope ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            // Disabled, and the only filter there is - so no filtering pass runs and nothing is cached yet.
            var filters = new FilterCollection();
            var absent = new Filter { Enabled = false, Match = { Text = "absent-text" } };
            filters.Add(absent);
            doc.SetFilters(filters);

            int reports = 0;
            double last = -1;
            long found = doc.FindLineMatchingFilter(absent, 0, forward: true, CancellationToken.None,
                f => { reports++; last = f; });

            Assert.Equal(-1, found);
            Assert.True(reports > 1, $"expected multiple progress callbacks, got {reports}");
            Assert.InRange(last, 0.0, 1.0);

            // The pass is remembered, so asking again does no work at all.
            int again = 0;
            Assert.Equal(-1, doc.FindLineMatchingFilter(absent, 0, forward: true, CancellationToken.None,
                _ => again++));
            Assert.Equal(0, again);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                doc.FindLineMatchingFilter(absent, 0, forward: true, cts.Token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Filter_find_from_the_cache_agrees_with_the_data()
    {
        // "disk" nested under "ERROR" deep-matches a line only when both match, i.e. every 21st line. The
        // expected answer is therefore computable from the data alone - a reference that owes nothing to the
        // implementation being tested. Checked for the cached path and for the path that has to compute it.
        const int Lines = 50_000;
        var sb = new StringBuilder();
        for (int i = 0; i < Lines; i++)
        {
            sb.Append(i % 3 == 0 ? "ERROR " : "INFO ");
            if (i % 7 == 0) sb.Append("disk ");
            sb.Append("line ").Append(i).Append('\n');
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        static long Expected(long start, bool forward)
        {
            if (forward)
            {
                for (long l = Math.Max(0, start); l < Lines; l++) if (l % 21 == 0) return l;
                return -1;
            }
            for (long l = Math.Min(start, Lines - 1); l >= 0; l--) if (l % 21 == 0) return l;
            return -1;
        }

        static (CascadeDocument Doc, Filter Target) Open(string path, bool enabled)
        {
            var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            var filters = new FilterCollection();
            var error = new Filter { Enabled = enabled, Match = { Text = "ERROR" } };
            var disk = new Filter { Enabled = enabled, Match = { Text = "disk" } };
            filters.Add(error);
            filters.Add(disk, error);
            doc.SetFilters(filters);
            return (doc, disk);
        }

        try
        {
            // Enabled: a completed pass has already recorded the answer.
            var (cached, cachedTarget) = Open(path, enabled: true);
            // Disabled: nothing is cached, so the find has to compute it the way enabling would.
            var (computed, computedTarget) = Open(path, enabled: false);
            using (cached)
            using (computed)
            {
                WaitFilter(cached);
                foreach (long start in new long[] { 0, 1, 20, 21, 22, 1000, 25_000, 49_998, 49_999 })
                {
                    foreach (bool forward in new[] { true, false })
                    {
                        long expected = Expected(start, forward);
                        Assert.Equal(expected, cached.FindLineMatchingFilter(cachedTarget, start, forward, CancellationToken.None));
                        Assert.Equal(expected, computed.FindLineMatchingFilter(computedTarget, start, forward, CancellationToken.None));
                    }
                }
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Filter_find_skips_matches_the_view_is_hiding()
    {
        // A line can match one filter and still be hidden by an exclude. Navigating to a hidden line strands
        // the caret on a neighbouring, non-matching line - and because the next search starts from the caret,
        // it then returns that same hidden line forever instead of moving on.
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            if (i % 10 == 0) sb.Append("TARGET ");
            if (i % 20 == 0) sb.Append("SKIP ");
            sb.Append("line ").Append(i).Append('\n');
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            var target = new Filter { Enabled = true, Match = { Text = "TARGET" } };
            var skip = new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "SKIP" } };
            filters.Add(target);
            filters.Add(skip);
            doc.SetFilters(filters);
            WaitFilter(doc);

            // TARGET is on every 10th line and SKIP hides every 20th, so 10, 30, 50 ... 990 remain.
            long[] expected = Enumerable.Range(0, 50).Select(k => (long)(k * 20 + 10)).ToArray();

            var forwardHits = new List<long>();
            for (long at = 0; ;)
            {
                long hit = doc.FindLineMatchingFilter(target, at, forward: true, CancellationToken.None);
                if (hit < 0) break;
                Assert.True(forwardHits.Count < expected.Length + 5, "find never reached the end");
                forwardHits.Add(hit);
                at = hit + 1;
            }
            Assert.Equal(expected, forwardHits);

            var backwardHits = new List<long>();
            for (long at = doc.CompletedLineCount - 1; ;)
            {
                long hit = doc.FindLineMatchingFilter(target, at, forward: false, CancellationToken.None);
                if (hit < 0) break;
                Assert.True(backwardHits.Count < expected.Length + 5, "find never reached the start");
                backwardHits.Add(hit);
                at = hit - 1;
            }
            Assert.Equal(expected.Reverse(), backwardHits);

            Assert.All(forwardHits, l => Assert.True(doc.IsLineVisible(l), $"line {l} is not visible"));
        }
        finally { File.Delete(path); }
    }

    // ---- a filtering pass held still, so streaming behaviour can be tested without racing it ----

    private const int StreamLines = 200_000;                        // seven 32,768-line blocks
    private static readonly long[] StreamHits = { 100, 40_000, 150_000 };   // blocks 0, 1 and 4

    /// <summary>A log whose TARGET lines sit in known blocks, so a test can say which of them a pass that has
    /// been let through a given number of blocks can possibly have reached.</summary>
    private static string WriteStreamLog(params long[] hidden)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < StreamLines; i++)
        {
            if (Array.IndexOf(StreamHits, (long)i) >= 0) sb.Append("TARGET ");
            if (Array.IndexOf(hidden, (long)i) >= 0) sb.Append("SKIP ");
            sb.Append("line ").Append(i).Append('\n');
        }
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Opens a file and holds its filtering pass still after every block, so a test can say exactly
    /// how far it has got and what a find running alongside it can therefore know.</summary>
    private sealed class HeldPass : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(0);
        private readonly List<long> _reached = new();
        private readonly string _path;

        public HeldPass(string path, FilterCollection filters)
        {
            _path = path;
            Doc = new CascadeDocument();
            Doc.Open(path);
            Doc.WaitForIndex();
            Doc.FilterCheckpointForTesting = frontier =>
            {
                lock (_reached) _reached.Add(frontier);
                _gate.Wait(TimeSpan.FromSeconds(20));
            };
            Doc.SetFilters(filters);
            WaitFor(() => BlocksDone >= 1, "the pass never finished its first block");
        }

        public CascadeDocument Doc { get; }

        public int BlocksDone { get { lock (_reached) return _reached.Count; } }

        public void ReleaseBlocks(int count)
        {
            int want = BlocksDone + count;
            _gate.Release(count);
            WaitFor(() => BlocksDone >= want || Doc.IsFilterIdle, $"the pass never reached block {want}");
        }

        public void ReleaseAll()
        {
            Doc.FilterCheckpointForTesting = null;
            _gate.Release(1000);
            WaitFilter(Doc);
        }

        public void Dispose()
        {
            Doc.FilterCheckpointForTesting = null;
            _gate.Release(1000);
            Doc.Dispose();
            _gate.Dispose();
            File.Delete(_path);
        }
    }

    private static void WaitFor(Func<bool> done, string what, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return;
            Thread.Sleep(2);
        }
        throw new TimeoutException(what);
    }

    private static FilterCollection StreamFilters(out Filter target, bool hideSkipped = false)
    {
        var filters = new FilterCollection { ShowOnlyFilteredLines = hideSkipped };
        target = new Filter { Enabled = true, Match = { Text = "TARGET" } };
        filters.Add(target);
        if (hideSkipped) filters.Add(new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "SKIP" } });
        else filters.Add(new Filter { Enabled = true, Match = { Text = "line" } });   // matches everything
        return filters;
    }

    [Fact]
    public async Task Filter_find_during_a_pass_reads_that_pass_instead_of_scanning_the_file_again()
    {
        // Reported: open a very large file, enable every filter, then press F4 while filtering is still
        // running - the search crawls and the filtering slows down with it. The running pass is already
        // working out which lines each filter matches, but the find ignored it and started a second
        // whole-file scan of its own on every core. MEASURED on a 15.8 GB, 66 M-line log with 30 filters:
        // the pass alone cost 32.2 s of CPU, with the find alongside it 63.1 s (and 46% more wall time),
        // and the find took 2,237 ms to report a hit that was on line 0.
        using var held = new HeldPass(WriteStreamLog(), StreamFilters(out var target));
        var doc = held.Doc;

        long frontier = doc.FilterProcessedLineCount;
        Assert.InRange(frontier, 1, StreamLines - 1);      // held mid-file, which is the whole point
        long scanned = doc.FilterLinesScanned;

        // The answer is already inside the swept region, so it costs nothing at all.
        Assert.Equal(100, doc.FindLineMatchingFilter(target, 0, forward: true, CancellationToken.None));
        Assert.Equal(scanned, doc.FilterLinesScanned);
        Assert.Equal(frontier, doc.FilterProcessedLineCount);

        // The next one is not, so the find has to wait - but only until the sweep passes it, not until the
        // pass has finished. It must not go off and scan the file on its own to avoid waiting.
        var find = Task.Run(() => doc.FindLineMatchingFilter(target, 200, forward: true, CancellationToken.None));
        await Task.Delay(250);
        Assert.False(find.IsCompleted, "the find answered while the pass was held, so it scanned the file itself");
        Assert.Equal(scanned, doc.FilterLinesScanned);

        held.ReleaseBlocks(1);                             // block 1 contains line 40,000
        Assert.Equal(40_000, await find.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(doc.FilterProcessedLineCount < 150_000,
                    $"the find waited for the whole pass (frontier {doc.FilterProcessedLineCount:N0})");
        // Everything the engine has read is the pass's own sweep - the find added not one line to it.
        Assert.Equal(doc.FilterProcessedLineCount, doc.FilterLinesScanned);
    }

    [Fact]
    public async Task Filter_find_backwards_during_a_pass_waits_for_the_sweep_to_pass_the_caret()
    {
        // The sweep runs upwards, so "the last match before line X" is not settled until it has gone past X -
        // a later one can still turn up. Answering from what has been swept so far would report 100 here,
        // when the true answer is 40,000.
        using var held = new HeldPass(WriteStreamLog(), StreamFilters(out var target));
        var doc = held.Doc;
        long scanned = doc.FilterLinesScanned;

        var find = Task.Run(() => doc.FindLineMatchingFilter(target, 149_999, forward: false, CancellationToken.None));
        await Task.Delay(250);
        Assert.False(find.IsCompleted, "answered before the sweep reached the line the search started from");

        held.ReleaseBlocks(4);                             // frontier 163,840, i.e. past 149,999
        Assert.Equal(40_000, await find.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(doc.FilterProcessedLineCount, doc.FilterLinesScanned);
        Assert.True(doc.FilterProcessedLineCount < StreamLines, "the find waited for the whole pass");

        // And once the sweep is past 150,000 the nearer match is the right answer.
        held.ReleaseAll();
        Assert.Equal(150_000, doc.FindLineMatchingFilter(target, 149_999 + 1, forward: false, CancellationToken.None));
    }

    [Fact]
    public async Task Filter_find_during_a_pass_skips_matches_the_view_is_hiding()
    {
        // A line can deep-match the filter and still be hidden by an enabled exclude. That has to hold while
        // the results are still streaming in, not just once the pass has finished.
        using var held = new HeldPass(WriteStreamLog(hidden: 100), StreamFilters(out var target, hideSkipped: true));
        var doc = held.Doc;
        long scanned = doc.FilterLinesScanned;

        Assert.False(doc.IsLineVisible(100), "line 100 should be hidden by the exclude");
        var find = Task.Run(() => doc.FindLineMatchingFilter(target, 0, forward: true, CancellationToken.None));
        await Task.Delay(250);
        Assert.False(find.IsCompleted, "the hidden match was skipped but the find did not wait for the next one");
        Assert.Equal(scanned, doc.FilterLinesScanned);

        held.ReleaseBlocks(1);
        Assert.Equal(40_000, await find.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Filter_find_during_a_pass_reports_no_more_matches_only_once_it_has_looked_everywhere()
    {
        // "No more matches" is a promise about the whole file, so it must never be answered from the part of
        // it the sweep happens to have covered.
        var filters = new FilterCollection();
        var absent = new Filter { Enabled = true, Match = { Text = "absent-text" } };
        filters.Add(absent);
        filters.Add(new Filter { Enabled = true, Match = { Text = "line" } });

        using var held = new HeldPass(WriteStreamLog(), filters);
        var doc = held.Doc;

        var find = Task.Run(() => doc.FindLineMatchingFilter(absent, 0, forward: true, CancellationToken.None));
        await Task.Delay(250);
        Assert.False(find.IsCompleted, "reported the end of the file after looking at one block of it");

        held.ReleaseAll();
        Assert.Equal(-1, await find.WaitAsync(TimeSpan.FromSeconds(10)));
        // The pass finished while the find was reading it and handed over through the cache - one pass, no more.
        Assert.Equal(StreamLines, doc.FilterLinesScanned);
    }

    [Fact]
    public async Task Filter_find_waiting_on_a_pass_can_still_be_called_off()
    {
        // The status bar offers Esc while a find runs, and a new search supersedes the last one. Neither can
        // work if waiting for the sweep is not interruptible.
        using var held = new HeldPass(WriteStreamLog(), StreamFilters(out var target));
        var doc = held.Doc;
        using var cts = new CancellationTokenSource();

        var find = Task.Run(() => doc.FindLineMatchingFilter(target, 200, forward: true, cts.Token), CancellationToken.None);
        await Task.Delay(200);
        Assert.False(find.IsCompleted);

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => find.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms to stop");
    }

    [Fact]
    public void Filter_find_gives_the_same_answers_whether_or_not_a_pass_is_running()
    {
        // The whole point of reading a running pass is that it is indistinguishable from reading a finished
        // one. The expected answers come from the data, so they owe nothing to either path.
        static long Expected(long start, bool forward)
        {
            if (forward)
            {
                foreach (long hit in StreamHits) if (hit >= start) return hit;
                return -1;
            }
            for (int i = StreamHits.Length - 1; i >= 0; i--) if (StreamHits[i] <= start) return StreamHits[i];
            return -1;
        }

        string path = WriteStreamLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            doc.SetFilters(StreamFilters(out var target));   // the pass starts here and runs alongside

            long[] starts = { 0, 99, 100, 101, 39_999, 40_000, 40_001, 149_999, 150_000, StreamLines - 1 };
            foreach (long start in starts)
                foreach (bool forward in new[] { true, false })
                    Assert.Equal(Expected(start, forward),
                                 doc.FindLineMatchingFilter(target, start, forward, CancellationToken.None));

            WaitFilter(doc);
            foreach (long start in starts)
                foreach (bool forward in new[] { true, false })
                    Assert.Equal(Expected(start, forward),
                                 doc.FindLineMatchingFilter(target, start, forward, CancellationToken.None));

            // Twenty searches, and between them they cost the file exactly one pass.
            Assert.Equal(StreamLines, doc.FilterLinesScanned);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Priming_a_switched_off_filter_records_its_own_chain_and_nothing_else()
    {
        // A find on a filter no pass is evaluating has to work it out itself. It only ever needs that filter's
        // own chain of predicates - its deep match, and the key it is stored under, depend on nothing else -
        // so it must not drag every other filter in the list through the scan with it.
        var filters = new FilterCollection();
        for (int i = 0; i < 5; i++)
            filters.Add(new Filter { Enabled = true, Match = { Text = $"line {i}" } });
        var outer = new Filter { Enabled = false, Match = { Text = "TARGET" } };
        var inner = new Filter { Enabled = false, Match = { Text = "line 40000" } };
        filters.Add(outer);
        filters.Add(inner, outer);

        using var held = new HeldPass(WriteStreamLog(), filters);
        var doc = held.Doc;
        Assert.Equal(0, doc.FilterCacheCount);           // the held pass has stored nothing yet

        Assert.Equal(40_000, doc.FindLineMatchingFilter(inner, 0, forward: true, CancellationToken.None));
        Assert.Equal(2, doc.FilterCacheCount);           // TARGET and TARGET>line 40000, and none of the five
    }

    [Fact]
    public void A_filter_change_does_not_blank_the_counts_already_worked_out()
    {
        // Reported on a 72 M-line log with 156 filters: toggling one filter made EVERY filter's count flash
        // 0 for about a third of a second before settling back to the same number it had before. A new pass
        // owns fresh accumulators, so until it sweeps a line no filter has a count - even though enabling or
        // disabling a filter cannot change what any filter matches, and the answers were already worked out.
        string path = WriteStreamLog();
        var gate = new SemaphoreSlim(0);
        var reached = new List<long>();
        try
        {
            using var doc = new CascadeDocument();
            try
            {
                doc.Open(path);
                doc.WaitForIndex();

                var filters = new FilterCollection();
                var target = new Filter { Enabled = true, Match = { Text = "TARGET" } };
                filters.Add(target);
                doc.SetFilters(filters);
                WaitFilter(doc);
                Assert.Equal(StreamHits.Length, doc.MatchCountFor(target));

                // A filter nothing has evaluated yet forces a real pass - the cached path cannot serve this
                // change - and that pass is held after its first block, which holds one TARGET line of three.
                doc.FilterCheckpointForTesting = frontier =>
                {
                    lock (reached) reached.Add(frontier);
                    gate.Wait(TimeSpan.FromSeconds(20));
                };
                filters.Add(new Filter { Enabled = true, Match = { Text = "line 4" } });
                doc.ApplyFilters();
                WaitFor(() => { lock (reached) return reached.Count >= 1; }, "the pass never finished a block");

                Assert.InRange(doc.FilterProcessedLineCount, 1, StreamLines - 1);   // genuinely mid-file
                Assert.Equal(StreamHits.Length, doc.MatchCountFor(target));
            }
            finally
            {
                doc.FilterCheckpointForTesting = null;
                gate.Release(1000);
            }
        }
        finally { gate.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void A_marker_filter_elsewhere_in_the_list_no_longer_spoils_every_other_find()
    {
        // Marker membership changes independently of the filters, so nothing whose chain involves one can be
        // remembered. Working from a snapshot of the whole filter set made that condemn every filter in the
        // list: one marker filter anywhere and no find could ever be cached. Its own chain is all a filter
        // needs, and this one's has no marker in it.
        var filters = new FilterCollection();
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 1 } });
        var target = new Filter { Enabled = false, Match = { Text = "TARGET" } };
        filters.Add(target);

        string path = WriteStreamLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            doc.Markers.Toggle(7, 1);
            doc.SetFilters(filters);
            WaitFilter(doc);
            // The marker filter's own results are remembered too, worked out from the marks themselves.
            Assert.Equal(1, doc.FilterCacheCount);

            Assert.Equal(100, doc.FindLineMatchingFilter(target, 0, forward: true, CancellationToken.None));
            Assert.Equal(2, doc.FilterCacheCount);

            // And having been worked out once it is a bit scan from then on.
            long scanned = doc.FilterLinesScanned;
            Assert.Equal(40_000, doc.FindLineMatchingFilter(target, 101, forward: true, CancellationToken.None));
            Assert.Equal(scanned, doc.FilterLinesScanned);
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
    public async Task Opening_a_new_file_while_a_find_runs_does_not_read_the_freed_mmap()
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
            var find = doc.FindNextAsync(new FindQuery("absent-string", false, false), 0, true); // long sweep

            doc.Open(b); // disposes A's mmap; must cancel + join the find first
            doc.WaitForIndex();

            Assert.Equal(2, doc.CompletedLineCount);
            // Cancelled by the second Open, which is the point; a timeout here means it was never joined.
            try { await find.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
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

            var eval = doc.ColouringSnapshot().Evaluate("ERROR one".AsSpan(), 0);
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

    [Fact]
    public void MatchedWords_reads_the_same_answer_as_asking_line_by_line()
    {
        // Summarising the whole file wants to know where the matches are, and asking one line at a time is a
        // rank and a select apiece. This reads them 64 to a word, so it has to agree exactly - including in
        // the ragged words at either end of the range.
        const int lines = 5_000;
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++) sb.Append(i % 7 == 3 ? "HIT" : "miss").Append(" line ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "HIT" } });
            doc.ApplyFilters();
            WaitFilter(doc);

            var read = doc.MatchedWords;
            Assert.NotNull(read);
            foreach (long firstWord in new long[] { 0, 1, 13, 60 })
            {
                var words = new ulong[20];
                read!(firstWord, words);
                for (int bit = 0; bit < words.Length * 64; bit++)
                {
                    long line = firstWord * 64 + bit;
                    bool said = (words[bit >> 6] >> (bit & 63) & 1) != 0;
                    bool truth = line < lines && line % 7 == 3;
                    Assert.True(said == truth, $"line {line} from word {firstWord}: read {said}, expected {truth}");
                }
            }

            // With nothing hidden there is no set to read, and the caller is meant to take that as "all of it"
            // rather than as "none of it".
            doc.Filters.Roots.Clear();
            doc.ApplyFilters();
            WaitFilter(doc);
            Assert.Null(doc.MatchedWords);
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void A_row_the_view_is_still_showing_keeps_its_colour_until_the_pass_drops_it()
    {
        // Reported as a flicker: switch a filter off and the lines it was showing turn into plain white text
        // for a frame or two before they disappear. The visible set is rewritten IN PLACE, so until the sweep
        // reaches a line the view is still listing it - and asked about that line the new filters answer
        // "not shown", which has no colour at all. It must keep the appearance the filters that put it there
        // gave it, so the view has one clean change rather than two.
        const int lines = 200_000, alphaOnly = 150_000;   // 150,000 is a multiple of 10 and not of 7
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i % 10 == 0 ? "ALPHA" : i % 7 == 0 ? "BETA" : "plain").Append(" line ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        var alpha = new Filter { Enabled = true, Match = { Text = "ALPHA" } };
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        filters.Add(alpha);
        filters.Add(new Filter { Enabled = true, Match = { Text = "BETA" } });
        // A marker filter keeps the match cache out of it, so the next pass really sweeps the file and can be
        // held part way. Answered from the cache the whole set is replaced in one go and there is no
        // half-finished state to test - though the window this is about is just as real there. It has to be
        // ENABLED to count: a filter nothing under it switches on is pruned, and prunes its veto with it.
        // Nothing is marked, so it matches nothing and leaves what is on show exactly as it was.
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 3 } });

        var gate = new SemaphoreSlim(0);
        int blocks = 0;
        using var doc = new CascadeDocument();
        try
        {
            // The next pass has to really sweep the file, so that it can be held part way. Answered from the cache
            // the whole set is replaced in one go and there is no half-finished state to test - though the window
            // this is about is just as real there.
            doc.SkipFilterCacheForTesting = true;
            doc.Open(path);
            doc.WaitForIndex();
            doc.SetFilters(filters);
            WaitFilter(doc);
            Assert.True(doc.IsLineVisible(alphaOnly), "the line the check is about is not on show to begin with");
            Assert.Same(alpha, doc.ColouringSnapshot().Evaluate(doc.GetLineText(alphaOnly), alphaOnly).ColorFilter);

            doc.FilterCheckpointForTesting = _ =>
            {
                Interlocked.Increment(ref blocks);
                gate.Wait(TimeSpan.FromSeconds(20));
            };
            alpha.Enabled = false;
            doc.ApplyFilters();
            WaitFor(() => Volatile.Read(ref blocks) >= 1, "the pass never finished a block");

            // Held near the start of the file, so the sweep cannot have reached the line yet.
            Assert.True(doc.FilterProcessedLineCount < alphaOnly,
                        $"the pass got too far to test ({doc.FilterProcessedLineCount:N0})");
            Assert.True(doc.IsLineVisible(alphaOnly), "the view has already dropped it, so there is nothing to draw");
            Assert.Same(alpha, doc.ColouringSnapshot().Evaluate(doc.GetLineText(alphaOnly), alphaOnly).ColorFilter);

            doc.FilterCheckpointForTesting = null;
            gate.Release(1000);
            WaitFilter(doc);

            // And once the view has caught up it really is gone, colour and all.
            Assert.False(doc.IsLineVisible(alphaOnly));
            Assert.Null(doc.ColouringSnapshot().Evaluate(doc.GetLineText(alphaOnly), alphaOnly).ColorFilter);
        }
        finally
        {
            doc.FilterCheckpointForTesting = null;
            gate.Release(1000);
            doc.Dispose();
            gate.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void A_frames_colours_do_not_change_when_the_pass_ends_half_way_through_it()
    {
        // The same flash again, in the shape the fallback above still missed: the pass ends while a frame is
        // being PAINTED. Its rows were resolved against the visible set as it stood, so they are the rows the
        // old filters put there - but the moment the pass finished, "the view has not caught up yet" went
        // false, and every row drawn after that instant came out with no colour: the bottom of the screen
        // went white for one frame. So the decision of which filters answer is taken once, up front, and a
        // frame keeps the one it was given.
        const int lines = 200_000, first = 150_000, second = 150_010;   // both ALPHA-only
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i % 10 == 0 ? "ALPHA" : i % 7 == 0 ? "BETA" : "plain").Append(" line ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        var alpha = new Filter { Enabled = true, Match = { Text = "ALPHA" } };
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        filters.Add(alpha);
        filters.Add(new Filter { Enabled = true, Match = { Text = "BETA" } });
        // Enabled, matches nothing, and stands in the list purely so that something does: this test is about
        // what a pass leaves behind, not about markers.
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 3 } });

        var gate = new SemaphoreSlim(0);
        int blocks = 0;
        using var doc = new CascadeDocument();
        try
        {
            doc.SkipFilterCacheForTesting = true;   // the next change has to really sweep, not be answered
            doc.Open(path);
            doc.WaitForIndex();
            doc.SetFilters(filters);
            WaitFilter(doc);

            doc.FilterCheckpointForTesting = _ =>
            {
                Interlocked.Increment(ref blocks);
                gate.Wait(TimeSpan.FromSeconds(20));
            };
            alpha.Enabled = false;
            doc.ApplyFilters();
            WaitFor(() => Volatile.Read(ref blocks) >= 1, "the pass never finished a block");
            Assert.True(doc.FilterProcessedLineCount < first,
                        $"the pass got too far to test ({doc.FilterProcessedLineCount:N0})");

            // A frame begins: it takes its colours, and its rows still include the lines ALPHA was showing.
            var frame = doc.ColouringSnapshot();
            Assert.True(doc.IsLineVisible(first) && doc.IsLineVisible(second));
            Assert.Same(alpha, frame.Evaluate(doc.GetLineText(first), first).ColorFilter);

            // The pass finishes underneath it, exactly as it did on screen.
            doc.FilterCheckpointForTesting = null;
            gate.Release(1000);
            WaitFilter(doc);

            // The rest of the frame must come out the same. Asked about a line it had not reached yet, so no
            // caching of an earlier answer can be what makes this pass.
            Assert.Same(alpha, frame.Evaluate(doc.GetLineText(second), second).ColorFilter);

            // And the NEXT frame is told the truth - the guard is still doing its job, not simply gone.
            Assert.False(doc.IsLineVisible(second));
            Assert.Null(doc.ColouringSnapshot().Evaluate(doc.GetLineText(second), second).ColorFilter);
        }
        finally
        {
            doc.FilterCheckpointForTesting = null;
            gate.Release(1000);
            doc.Dispose();
            gate.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void A_stretch_left_behind_by_a_superseded_pass_is_still_coloured()
    {
        // Change the filters twice before a pass can finish and the view becomes a mixture: the new pass
        // rewrites from line 0, so between where it has reached and where the abandoned one got to, the view
        // is still listing the rows THAT pass added. The filters in force do not show them, and neither do
        // the ones from before it started - so with only one set remembered they were drawn with no colour
        // at all, for as long as it took the new pass to sweep that far. The filters each pass was running
        // are remembered, not just the last settled set.
        const int lines = 200_000, alphaOnly = 50_000;   // a multiple of 10, so ALPHA and nothing else
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i % 10 == 0 ? "ALPHA" : i % 7 == 0 ? "BETA" : "plain").Append(" line ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        var alpha = new Filter { Enabled = false, Match = { Text = "ALPHA" } };
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        filters.Add(alpha);
        filters.Add(new Filter { Enabled = true, Match = { Text = "BETA" } });
        // Enabled, matches nothing, and stands in the list purely so that something does: this test is about
        // what a superseded pass leaves behind, not about markers.
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 3 } });

        var gate = new SemaphoreSlim(0);
        int blocks = 0;
        using var doc = new CascadeDocument();
        try
        {
            doc.SkipFilterCacheForTesting = true;   // each change has to really sweep, not be answered
            doc.Open(path);
            doc.WaitForIndex();
            doc.SetFilters(filters);
            WaitFilter(doc);
            Assert.False(doc.IsLineVisible(alphaOnly), "the line starts out hidden, so only a pass can put it on screen");

            doc.FilterCheckpointForTesting = _ => { Interlocked.Increment(ref blocks); gate.Wait(TimeSpan.FromSeconds(20)); };
            alpha.Enabled = true;
            doc.ApplyFilters();
            WaitFor(() => Volatile.Read(ref blocks) >= 1, "the pass never started");
            gate.Release(3);
            WaitFor(() => Volatile.Read(ref blocks) >= 4, "the pass did not reach the line");

            // Superseded well past the line, and the next pass held before it gets back there.
            alpha.Enabled = false;
            doc.ApplyFilters();
            int was = Volatile.Read(ref blocks);
            gate.Release(1);
            WaitFor(() => Volatile.Read(ref blocks) >= was + 1, "the second pass never started");

            Assert.True(doc.FilterProcessedLineCount < alphaOnly,
                        $"the second pass has already swept the stretch ({doc.FilterProcessedLineCount:N0})");
            Assert.True(doc.IsLineVisible(alphaOnly), "the abandoned pass's stretch is not on screen, so there is nothing to draw");
            Assert.Same(alpha, doc.ColouringSnapshot().Evaluate(doc.GetLineText(alphaOnly), alphaOnly).ColorFilter);
        }
        finally
        {
            doc.FilterCheckpointForTesting = null;
            gate.Release(1000);
            doc.Dispose();
            gate.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void A_storm_of_filter_changes_cannot_alter_what_a_frame_already_holds()
    {
        // The invariant the two checks above are particular cases of: once a frame has taken its colours,
        // nothing - a pass finishing, a pass starting, the filters changing again - can change the answers
        // it gets for the rows it is drawing. Every round below moves thousands of lines in and out of the
        // view while the same frame keeps asking.
        const int lines = 40_000;
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i % 3 == 0 ? "ALPHA" : i % 3 == 1 ? "BETA" : "GAMMA").Append(" line ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        var toggles = new[]
        {
            new Filter { Enabled = true, Match = { Text = "ALPHA" }, Style = { Foreground = new RgbColor(200, 0, 0) } },
            new Filter { Enabled = true, Match = { Text = "BETA" }, Style = { Foreground = new RgbColor(0, 150, 0) } },
            new Filter { Enabled = true, Match = { Text = "GAMMA" }, Style = { Foreground = new RgbColor(0, 0, 200) } },
            new Filter { Enabled = false, Kind = FilterKind.Exclude, Match = { Text = "line 1" } },
            new Filter { Enabled = false, Kind = FilterKind.Exclude, Match = { Text = "7 " } },
        };
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        foreach (var f in toggles) filters.Add(f);
        // Enabled and matching nothing: stands in the list purely so that something does.
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 3 } });

        using var doc = new CascadeDocument();
        try
        {
            doc.SkipFilterCacheForTesting = true;   // every round has to be a real pass
            doc.Open(path);
            doc.WaitForIndex();
            doc.SetFilters(filters);
            WaitFilter(doc);

            // One screenful, resolved in one shot, exactly as a frame does it.
            var window = new long[40];
            int n = doc.LinesForRows(doc.RowCount / 2, window);
            Assert.Equal(window.Length, n);

            var frame = doc.ColouringSnapshot();
            var texts = new string[n];
            var first = new Filter?[n];
            for (int i = 0; i < n; i++)
            {
                texts[i] = doc.GetLineText(window[i]);
                first[i] = frame.Evaluate(texts[i], window[i]).ColorFilter;
                Assert.NotNull(first[i]);   // the fixture colours everything it shows, so a blank is a fault
            }

            var rnd = new Random(7);
            for (int round = 0; round < 60; round++)
            {
                var moved = toggles[rnd.Next(toggles.Length)];
                moved.Enabled = !moved.Enabled;
                doc.ApplyFilters();
                if (round % 4 == 0) WaitFilter(doc);            // sometimes let it finish, sometimes cut it short
                for (int i = 0; i < n; i++)
                    Assert.Same(first[i], frame.Evaluate(texts[i], window[i]).ColorFilter);
            }
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void The_filters_a_settled_view_can_no_longer_be_asked_about_are_let_go()
    {
        // Each remembered set carries its own matching automaton - about 400 KB for a filter file of a
        // couple of hundred - and a finished pass has swept the whole file, so nothing can ask about the
        // older ones again. They used to sit there until the next filter change, which may never come.
        // The refusal while a pass is still running matters more than the release: those stretches still
        // need the filters that put them on screen, or they go back to being drawn as unfiltered text.
        const int lines = 200_000, alphaOnly = 50_000;
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i % 10 == 0 ? "ALPHA" : i % 7 == 0 ? "BETA" : "plain").Append(" line ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        var alpha = new Filter { Enabled = false, Match = { Text = "ALPHA" } };
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        filters.Add(alpha);
        filters.Add(new Filter { Enabled = true, Match = { Text = "BETA" } });
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 3 } });

        var gate = new SemaphoreSlim(0);
        int blocks = 0;
        using var doc = new CascadeDocument();
        try
        {
            doc.SkipFilterCacheForTesting = true;   // the pass has to really run, so it can be held part way
            doc.Open(path);
            doc.WaitForIndex();
            doc.SetFilters(filters);
            WaitFilter(doc);

            doc.FilterCheckpointForTesting = _ => { Interlocked.Increment(ref blocks); gate.Wait(TimeSpan.FromSeconds(20)); };
            alpha.Enabled = true;
            doc.ApplyFilters();
            WaitFor(() => Volatile.Read(ref blocks) >= 1, "the pass never started");
            gate.Release(3);
            WaitFor(() => Volatile.Read(ref blocks) >= 4, "the pass did not reach the line");
            alpha.Enabled = false;
            doc.ApplyFilters();
            int was = Volatile.Read(ref blocks);
            gate.Release(1);
            WaitFor(() => Volatile.Read(ref blocks) >= was + 1, "the second pass never started");
            Assert.Equal(2, doc.RememberedViewCountForTesting);

            // Asked while the pass is still catching up, it must keep them - and the stretch the abandoned
            // pass left behind must still have its colour.
            doc.DropRememberedViews();
            Assert.Equal(2, doc.RememberedViewCountForTesting);
            Assert.Same(alpha, doc.ColouringSnapshot().Evaluate(doc.GetLineText(alphaOnly), alphaOnly).ColorFilter);

            doc.FilterCheckpointForTesting = null;
            gate.Release(1000);
            WaitFilter(doc);

            doc.DropRememberedViews();
            Assert.Equal(1, doc.RememberedViewCountForTesting);
            // And what is left is the filters in force, so consulting it can only repeat what they say.
            Assert.False(doc.IsLineVisible(alphaOnly));
            Assert.Null(doc.ColouringSnapshot().Evaluate(doc.GetLineText(alphaOnly), alphaOnly).ColorFilter);
        }
        finally
        {
            doc.FilterCheckpointForTesting = null;
            gate.Release(1000);
            doc.Dispose();
            gate.Dispose();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(300)]     // answered on the calling thread
    [InlineData(4_000)]   // shared out across the cores
    public void Colouring_many_lines_at_once_agrees_with_asking_one_at_a_time(int count)
    {
        // Summarising the file for the minimap means colouring tens of thousands of rows at once, which is a
        // fifth of a second of filter matching on one thread - paid on every scroll, by the thread that has
        // to repaint. Sharing it out is only safe if it comes to exactly the same answer, so that is what is
        // asserted here: against the very call the text view colours a row with.
        var sb = new StringBuilder();
        for (int i = 0; i < 5_000; i++)
        {
            string kind = i % 11 == 0 ? "ERROR" : i % 5 == 0 ? "WARN" : "INFO";
            string area = i % 3 == 0 ? "disk" : "net";
            sb.Append(kind).Append(' ').Append(area).Append(" line ").Append(i).Append('\n');
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            // Nested both above and below the catch-all, so the rows exercise both halves of the colour
            // rule: a claim refined by a child (ERROR > disk), and a claim that a deeper filter further
            // down the list must not steal (line, against INFO > net). That is the part a per-line walk
            // could get right and a bulk one wrong.
            var error = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };
            doc.Filters.Add(error);
            doc.Filters.Add(disk, error);
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "WARN" } });
            doc.Filters.Add(new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "line 7" } });
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "line" } });   // so most rows have a colour
            var info = new Filter { Enabled = true, Match = { Text = "INFO" } };
            doc.Filters.Add(info);
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "net" } }, info);
            doc.Open(path);
            doc.WaitForIndex();
            doc.ApplyFilters();
            WaitFilter(doc);

            var lines = new long[count];
            var got = new Filter?[count];
            var rnd = new Random(11);
            for (int i = 0; i < count; i++)
                lines[i] = i % 17 == 0 ? -1 : rnd.Next(-3, 5_100);   // includes skips and out-of-range lines

            doc.ColouringFilters(lines, count, got);

            int coloured = 0;
            var colouring = doc.ColouringSnapshot();
            for (int i = 0; i < count; i++)
            {
                long line = lines[i];
                Filter? want = line >= 0 && line < 5_000
                    ? colouring.Evaluate(doc.GetLineText(line), line).ColorFilter
                    : null;
                Assert.True(ReferenceEquals(want, got[i]),
                            $"line {line}: expected {want?.DisplayName ?? "none"}, got {got[i]?.DisplayName ?? "none"}");
                if (got[i] is not null) coloured++;
            }
            // The fixture has to be able to tell a right answer from a blank one.
            Assert.True(coloured > count / 2, $"only {coloured} of {count} lines were coloured at all");
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Dragging_a_filter_into_another_branch_re_colours_the_file()
    {
        // The colour rule reads the filter list as it is drawn, so moving a filter changes which lines it
        // answers for. Everything downstream is keyed off a snapshot taken when the pass starts - the match
        // cache by the chain of predicates above each filter, the counts by position - so this is the case
        // where a stale snapshot or a stale cache entry would show up as the wrong colour on screen.
        var sb = new StringBuilder();
        for (int i = 0; i < 4_000; i++)
            sb.Append(i % 3 == 0 ? "ERROR" : "INFO").Append(" payment-svc line ").Append(i).Append('\n');
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));

        using var doc = new CascadeDocument();
        try
        {
            // "line" matches everything and sits at the top, so it claims every row. payment-svc is nested
            // under ERROR further down: deeper, but in a branch of its own, so it must not take anything.
            var everything = new Filter { Enabled = true, Match = { Text = "line" } };
            var error = new Filter { Enabled = true, Match = { Text = "ERROR" } };
            var service = new Filter { Enabled = true, Match = { Text = "payment-svc" } };
            doc.Filters.Add(everything);
            doc.Filters.Add(error);
            doc.Filters.Add(service, error);

            doc.Open(path);
            doc.WaitForIndex();
            doc.ApplyFilters();
            WaitFilter(doc);

            const long errorLine = 9;    // 9 % 3 == 0, so this one says ERROR
            const long infoLine = 10;
            Assert.Same(everything, doc.ColouringSnapshot().Evaluate(doc.GetLineText(errorLine), errorLine).ColorFilter);
            Assert.Same(everything, doc.ColouringSnapshot().Evaluate(doc.GetLineText(infoLine), infoLine).ColorFilter);

            // Drag the whole ERROR branch to the top. Now it claims first, and its child refines it.
            Assert.True(doc.Filters.Move(error, null, 0));
            doc.ApplyFilters();
            WaitFilter(doc);

            Assert.Same(service, doc.ColouringSnapshot().Evaluate(doc.GetLineText(errorLine), errorLine).ColorFilter);
            Assert.Same(everything, doc.ColouringSnapshot().Evaluate(doc.GetLineText(infoLine), infoLine).ColorFilter);

            // Nest the catch-all under the service filter: three levels, and the deepest now answers.
            Assert.True(doc.Filters.Move(everything, service, 0));
            doc.ApplyFilters();
            WaitFilter(doc);

            Assert.Same(everything, doc.ColouringSnapshot().Evaluate(doc.GetLineText(errorLine), errorLine).ColorFilter);
            var info = doc.ColouringSnapshot().Evaluate(doc.GetLineText(infoLine), infoLine);
            Assert.False(info.Shown);       // an INFO line no longer satisfies the chain above the catch-all
            Assert.Null(info.ColorFilter);

            // ...and the bulk path the minimap uses has to agree with all of it, row for row.
            var lines = new long[512];
            var got = new Filter?[512];
            for (int i = 0; i < lines.Length; i++) lines[i] = i;
            doc.ColouringFilters(lines, lines.Length, got);
            var colouring = doc.ColouringSnapshot();
            for (int i = 0; i < lines.Length; i++)
                Assert.Same(colouring.Evaluate(doc.GetLineText(lines[i]), lines[i]).ColorFilter, got[i]);
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }
}
