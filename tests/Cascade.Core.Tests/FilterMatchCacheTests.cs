using System.Diagnostics;
using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Filtering;
using Cascade.Core.Indexing;
using Cascade.Core.IO;
using Cascade.Core.Markers;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>
/// The match cache remembers which lines each filter matched so that enabling, disabling or removing filters
/// recombines cached sets instead of re-reading the file. A stale or wrong cache would silently produce the
/// wrong lines, so every test here compares the cached result against a freshly evaluated one.
/// </summary>
public class FilterMatchCacheTests
{
    private const int Lines = 120_000;

    /// <summary>A log whose lines match a known, varied mix of the filters used below.</summary>
    private static string WriteLog()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Lines; i++)
        {
            sb.Append(i % 3 == 0 ? "ERROR " : i % 3 == 1 ? "WARN " : "INFO ");
            if (i % 5 == 0) sb.Append("disk ");
            if (i % 7 == 0) sb.Append("net ");
            if (i % 11 == 0) sb.Append("noise ");
            if (i % 13 == 0) sb.Append("[a]mid[b] ");
            sb.Append("line ").Append(i).Append('\n');
        }
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static FilterCollection BuildFilters(out List<Filter> flat)
    {
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        var list = new List<Filter>();
        Filter Add(string text, bool regex = false, FilterKind kind = FilterKind.Include, Filter? parent = null)
        {
            var f = new Filter { Enabled = true, Kind = kind, Match = { Text = text, Regex = regex } };
            filters.Add(f, parent);
            list.Add(f);
            return f;
        }

        var error = Add("ERROR");
        Add("disk", parent: error);          // nested: parent constrains it
        Add("WARN");
        Add("net");
        Add(@"\[a\].+\[b\]", regex: true);   // rewritten to a literal sequence
        Add("noise", kind: FilterKind.Exclude);
        flat = list;
        return filters;
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

    /// <summary>Every visible line, plus the per-filter counts, i.e. the entire observable result.</summary>
    private static (List<long> Visible, long[] Counts) Capture(CascadeDocument doc, List<Filter> flat)
    {
        var visible = new List<long>();
        for (long row = 0; row < doc.RowCount; row++) visible.Add(doc.RowToLine(row));
        return (visible, flat.Select(doc.MatchCountFor).ToArray());
    }

    /// <summary>Evaluates <paramref name="configure"/>'s filter state on a brand-new document, so it can never
    /// have been served from a cache.</summary>
    private static (List<long> Visible, long[] Counts) Fresh(string path, Action<List<Filter>> configure)
    {
        using var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        var filters = BuildFilters(out var flat);
        configure(flat);
        doc.SetFilters(filters);
        WaitIdle(doc);
        Assert.Equal(0, doc.FilterCacheHits);   // a fresh document must have scanned
        return Capture(doc, flat);
    }

    /// <summary>Same, but for changes to the filter <i>tree</i> rather than to individual filters.</summary>
    private static (List<long> Visible, long[] Counts) Fresh(string path, Action<FilterCollection, List<Filter>> restructure)
    {
        using var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        var filters = BuildFilters(out var flat);
        restructure(filters, flat);
        doc.SetFilters(filters);
        WaitIdle(doc);
        Assert.Equal(0, doc.FilterCacheHits);
        return Capture(doc, flat);
    }

    /// <summary>Opens the log with the standard filter set applied and the cache warmed by a full scan.</summary>
    private static CascadeDocument Warmed(string path, out FilterCollection filters, out List<Filter> flat)
    {
        var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        filters = BuildFilters(out flat);
        doc.SetFilters(filters);
        WaitIdle(doc);
        Assert.True(doc.FilterCacheBytes > 0, "the first pass should have populated the cache");
        return doc;
    }

    [Fact]
    public async Task A_pass_does_not_report_itself_idle_before_it_has_recorded_what_it_worked_out()
    {
        // The sweep sets its processed count to the total and only THEN hands the per-filter results over,
        // and that window was reported as idle: a filter change arriving in it missed the cache and paid for
        // a whole pass again, and anything waiting for the pass could find the cache empty. It showed up as a
        // one-run-in-eight failure of the checks below on a loaded machine, and on a slow CI runner.
        // Watched from another thread, the instant it calls itself idle the results must already be there.
        string path = WriteLog();
        using var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            var filters = BuildFilters(out _);

            bool sawItWorking = false;
            var watcher = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                while (doc.IsFilterIdle && sw.ElapsedMilliseconds < 10_000) { }
                sawItWorking = !doc.IsFilterIdle;
                while (!doc.IsFilterIdle && sw.ElapsedMilliseconds < 30_000) { }
                return doc.FilterCacheBytes;   // read the moment it says it has finished
            });

            doc.SetFilters(filters);
            long bytes = await watcher;

            Assert.True(sawItWorking, "the pass was over before it could be watched, so this proves nothing");
            Assert.True(bytes > 0, "called itself idle before recording what the pass worked out");
        }
        finally
        {
            doc.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Toggling_filters_from_cache_matches_a_fresh_evaluation()
    {
        string path = WriteLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            var filters = BuildFilters(out var flat);
            doc.SetFilters(filters);
            WaitIdle(doc);
            Assert.True(doc.FilterCacheBytes > 0, "the first pass should have populated the cache");

            // A series of enable/disable changes, each of which should now be served from the cache.
            var configurations = new Action<List<Filter>>[]
            {
                f => f[0].Enabled = false,                      // disable a parent (its child goes too)
                f => { f[0].Enabled = false; f[2].Enabled = false; },
                f => f[5].Enabled = false,                      // disable the exclude
                f => { for (int i = 0; i < f.Count; i++) f[i].Enabled = i == 3; },
                f => { for (int i = 0; i < f.Count; i++) f[i].Enabled = true; },
                f => f[4].Enabled = false,                      // the rewritten regex
                f => { f[1].Enabled = false; f[5].Enabled = false; },
            };

            long hitsBefore = doc.FilterCacheHits;
            foreach (var configure in configurations)
            {
                foreach (var f in flat) f.Enabled = true;
                configure(flat);
                doc.ApplyFilters();
                WaitIdle(doc);

                var cached = Capture(doc, flat);
                var fresh = Fresh(path, configure);
                Assert.Equal(fresh.Visible, cached.Visible);
                Assert.Equal(fresh.Counts, cached.Counts);
            }
            Assert.True(doc.FilterCacheHits > hitsBefore,
                "toggles should have been served from the cache, not re-scanned");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Removing_a_filter_uses_the_cache_and_stays_correct()
    {
        string path = WriteLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            var filters = BuildFilters(out var flat);
            doc.SetFilters(filters);
            WaitIdle(doc);

            filters.Remove(flat[2]);      // drop "WARN"
            doc.ApplyFilters();
            WaitIdle(doc);
            var cached = Capture(doc, flat.Where(f => f != flat[2]).ToList());

            using var freshDoc = new CascadeDocument();
            freshDoc.Open(path);
            freshDoc.WaitForIndex();
            var freshFilters = BuildFilters(out var freshFlat);
            freshFilters.Remove(freshFlat[2]);
            freshDoc.SetFilters(freshFilters);
            WaitIdle(freshDoc);

            Assert.Equal(Capture(freshDoc, freshFlat.Where(f => f != freshFlat[2]).ToList()).Visible, cached.Visible);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Editing_a_filter_invalidates_its_cached_result()
    {
        string path = WriteLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            var filters = BuildFilters(out var flat);
            doc.SetFilters(filters);
            WaitIdle(doc);
            long before = doc.MatchedLineCount;

            // Changing the text must NOT reuse the old result.
            flat[2].Match.Text = "INFO";
            doc.ApplyFilters();
            WaitIdle(doc);

            using var fresh = new CascadeDocument();
            fresh.Open(path);
            fresh.WaitForIndex();
            var freshFilters = BuildFilters(out var freshFlat);
            freshFlat[2].Match.Text = "INFO";
            fresh.SetFilters(freshFilters);
            WaitIdle(fresh);

            Assert.NotEqual(before, doc.MatchedLineCount);
            Assert.Equal(fresh.MatchedLineCount, doc.MatchedLineCount);
            Assert.Equal(fresh.MatchCountFor(freshFlat[2]), doc.MatchCountFor(flat[2]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Editing_a_parent_invalidates_its_children()
    {
        string path = WriteLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            var filters = BuildFilters(out var flat);
            doc.SetFilters(filters);
            WaitIdle(doc);
            var before = Capture(doc, flat);

            // The child's deep match includes its parent's predicate, so changing the parent must change which
            // lines the child matches. (Its COUNT can stay the same by coincidence - the lines must not.)
            flat[0].Match.Text = "WARN";
            doc.ApplyFilters();
            WaitIdle(doc);
            var after = Capture(doc, flat);

            using var fresh = new CascadeDocument();
            fresh.Open(path);
            fresh.WaitForIndex();
            var freshFilters = BuildFilters(out var freshFlat);
            freshFlat[0].Match.Text = "WARN";
            fresh.SetFilters(freshFilters);
            WaitIdle(fresh);

            Assert.NotEqual(before.Visible, after.Visible);          // the edit really took effect
            Assert.Equal(Capture(fresh, freshFlat).Visible, after.Visible);
            Assert.Equal(fresh.MatchCountFor(freshFlat[1]), doc.MatchCountFor(flat[1]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Marker_filters_are_never_served_from_the_cache()
    {
        // Marker membership changes independently of the filters, so a cached result would go stale.
        string path = WriteLog();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            var marker = new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 1 } };
            filters.Add(marker);
            doc.Markers.Toggle(10, 1);
            doc.SetFilters(filters);
            WaitIdle(doc);
            Assert.Equal(1, doc.MatchedLineCount);

            doc.Markers.Toggle(20, 1);
            doc.ApplyFilters();
            WaitIdle(doc);

            Assert.Equal(0, doc.FilterCacheHits);      // must always re-evaluate
            Assert.Equal(2, doc.MatchedLineCount);
            Assert.Equal(10, doc.RowToLine(0));
            Assert.Equal(20, doc.RowToLine(1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reordering_filters_reuses_the_cache_and_changes_nothing()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            var before = Capture(doc, flat);
            long hits = doc.FilterCacheHits;

            // Moving filters among their siblings renumbers every node, but changes no filter's predicate
            // chain - so the cache still applies, and OR/AND-NOT being order-free the result cannot change.
            Assert.True(filters.Move(flat[3], null, 0));      // "net" to the front
            Assert.True(filters.Move(flat[0], null, 3));      // "ERROR" (carrying its child) further down
            doc.ApplyFilters();
            WaitIdle(doc);
            var after = Capture(doc, flat);

            Assert.True(doc.FilterCacheHits > hits, "reordering should not need a re-scan");
            Assert.Equal(before.Visible, after.Visible);
            Assert.Equal(before.Counts, after.Counts);
            var fresh = Fresh(path, (c, f) => { c.Move(f[3], null, 0); c.Move(f[0], null, 3); });
            Assert.Equal(fresh.Visible, after.Visible);
            Assert.Equal(fresh.Counts, after.Counts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Nesting_a_filter_under_another_re_evaluates_it()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            Assert.Equal(17_143, doc.MatchCountFor(flat[3]));   // "net" at root: every 7th line

            // Nesting adds the parent's predicate to the child's chain, so its cached set must not be reused.
            Assert.True(filters.Move(flat[3], flat[0], 0));     // "net" under "ERROR"
            doc.ApplyFilters();
            WaitIdle(doc);

            Assert.Equal(5_715, doc.MatchCountFor(flat[3]));    // now only lines that are ERROR *and* net
            var after = Capture(doc, flat);
            var fresh = Fresh(path, (c, f) => c.Move(f[3], f[0], 0));
            Assert.Equal(fresh.Visible, after.Visible);
            Assert.Equal(fresh.Counts, after.Counts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Un_nesting_a_filter_re_evaluates_it()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            Assert.Equal(8_000, doc.MatchCountFor(flat[1]));    // "disk" under "ERROR": every 15th line

            // Dropping the parent's predicate widens the child; the narrower cached set must not be reused.
            Assert.True(filters.Move(flat[1], null, 0));        // "disk" out to the top level
            doc.ApplyFilters();
            WaitIdle(doc);

            Assert.Equal(24_000, doc.MatchCountFor(flat[1]));   // now every 5th line
            var after = Capture(doc, flat);
            var fresh = Fresh(path, (c, f) => c.Move(f[1], null, 0));
            Assert.Equal(fresh.Visible, after.Visible);
            Assert.Equal(fresh.Counts, after.Counts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Adding_a_filter_re_evaluates_only_when_its_chain_is_new()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            long hits = doc.FilterCacheHits;

            // A predicate the cache has never seen forces a scan.
            var added = new Filter { Enabled = true, Match = { Text = "noise" } };
            filters.Add(added, flat[2]);                        // "noise" under "WARN"
            doc.ApplyFilters();
            WaitIdle(doc);
            Assert.Equal(hits, doc.FilterCacheHits);            // no hit: it had to scan
            Assert.Equal(3_636, doc.MatchCountFor(added));      // WARN and noise: i%3==1 && i%11==0
            var expected = Fresh(path, (c, f) => c.Add(new Filter { Enabled = true, Match = { Text = "noise" } }, f[2]));
            Assert.Equal(expected.Visible, Capture(doc, flat).Visible);

            // A duplicate of a chain that is already cached needs no scan at all.
            hits = doc.FilterCacheHits;
            var duplicate = new Filter { Enabled = true, Match = { Text = "net" } };
            filters.Add(duplicate);
            doc.ApplyFilters();
            WaitIdle(doc);
            Assert.True(doc.FilterCacheHits > hits, "a repeated predicate chain should come from the cache");
            Assert.Equal(doc.MatchCountFor(flat[3]), doc.MatchCountFor(duplicate));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Switching_a_filter_between_include_and_exclude_reuses_the_cache()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            long hits = doc.FilterCacheHits;

            // Include/exclude decides how a filter's lines are *combined*, not which lines it matches, so the
            // cached set stays valid - but the visible result must still change.
            var before = Capture(doc, flat).Visible;
            flat[2].Kind = FilterKind.Exclude;                  // "WARN" now hides its lines
            doc.ApplyFilters();
            WaitIdle(doc);
            var after = Capture(doc, flat).Visible;

            Assert.True(doc.FilterCacheHits > hits, "flipping the kind should not need a re-scan");
            Assert.NotEqual(before, after);
            Assert.Equal(Fresh(path, f => f[2].Kind = FilterKind.Exclude).Visible, after);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Opening_a_file_with_filters_already_applied_fills_the_cache()
    {
        // The real startup order: the filter list is restored before any file is open, so evaluation begins
        // while the file is still being indexed. That pass is the most important one of all to remember - if
        // it is skipped, the user's very first toggle re-scans the whole file.
        string path = WriteLog();
        try
        {
            using var doc = new CascadeDocument();
            var filters = BuildFilters(out var flat);
            doc.SetFilters(filters);
            doc.Open(path);              // filtering starts at 0% indexed
            WaitIdle(doc);

            Assert.True(doc.FilterCacheBytes > 0, "the streaming pass should have filled the cache");
            Assert.Equal(Fresh(path, f => { }).Visible, Capture(doc, flat).Visible);

            long hits = doc.FilterCacheHits;
            foreach (var f in flat) f.Enabled = false;
            flat[3].Enabled = true;      // "disable everything, then turn one back on"
            doc.ApplyFilters();
            WaitIdle(doc);

            Assert.True(doc.FilterCacheHits > hits, "the first toggle should be served from the cache");
            Assert.Equal(17_143, doc.MatchCountFor(flat[3]));
            var expected = Fresh(path, f => { for (int i = 0; i < f.Count; i++) f[i].Enabled = i == 3; });
            Assert.Equal(expected.Visible, Capture(doc, flat).Visible);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Filtering_while_indexing_still_records_a_correct_cache()
    {
        // Indexing releases an arbitrary number of lines at a time, but the cache records whole 64-line
        // words. A round that ends mid-word must not shift every word recorded after it. Driving the service
        // directly makes the unaligned releases deterministic instead of a race with a real indexer.
        string path = WriteLog();
        try
        {
            using var src = new MemoryMappedTextSource(path);
            var index = new LineIndex();
            new LineIndexer(src, index, 0, 1, false).Run(null, CancellationToken.None);
            long total = index.Count;

            long available = 0;
            bool complete = false;
            using var service = new FilterService(src, index, src.Length, new MarkerStore(), Encoding.UTF8,
                () => Volatile.Read(ref available), () => Volatile.Read(ref complete));

            var filters = BuildFilters(out var flat);
            var gen = service.Restart(FilterSnapshot.Build(filters), seedAllVisible: true);

            foreach (long step in new long[] { 1_000, 37, 4_095, 1, 64, 50_000, 100 })
            {
                Volatile.Write(ref available, Math.Min(total, Volatile.Read(ref available) + step));
                service.Notify();
                // Everything but the trailing partial word must be consumed before the next release.
                WaitUntil(() => gen.View.KnownLines >= Volatile.Read(ref available) - 63);
            }
            Volatile.Write(ref available, total);
            Volatile.Write(ref complete, true);
            service.Notify();
            WaitUntil(() => service.IsIdle);

            Assert.True(service.CacheBytes > 0, "the streaming pass should have filled the cache");
            Assert.Equal(Fresh(path, f => { }).Visible, Rows(gen));

            // The cache built from those unaligned releases must serve the next change correctly.
            long hits = service.CacheHits;
            foreach (var f in flat) f.Enabled = false;
            flat[3].Enabled = true;
            var toggled = service.Restart(FilterSnapshot.Build(filters));
            WaitUntil(() => service.IsIdle);

            Assert.True(service.CacheHits > hits, "the streamed cache should serve the next change");
            var expected = Fresh(path, f => { for (int i = 0; i < f.Count; i++) f[i].Enabled = i == 3; });
            Assert.Equal(expected.Visible, Rows(toggled));
        }
        finally { File.Delete(path); }
    }

    private static List<long> Rows(FilterService.Generation gen)
    {
        var lines = new List<long>();
        for (long row = 0; row < gen.View.Count; row++) lines.Add(gen.View.LineAt(row));
        return lines;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            if (condition()) return;
            Thread.Sleep(1);
        }
        throw new TimeoutException("the filter service did not catch up");
    }

    // ---- storage-level tests ----

    [Fact]
    public void Set_builder_grows_when_lines_arrive_after_it_starts()
    {
        // A builder created mid-indexing knows only a fraction of the file, so it must resize as later words
        // arrive rather than quietly dropping them.
        var builder = new FilterMatchCache.SetBuilder(64);   // only one word indexed so far
        const long Words = 5_000;
        for (long w = 0; w < Words; w++) builder.AddWord(w, ulong.MaxValue);   // dense: every line matches

        var set = builder.Build(Words * 64);
        Assert.Equal(Words * 64, set.Matches);
        for (long line = 0; line < Words * 64; line += 7) Assert.True(set.Contains(line), $"lost line {line}");
        Assert.True(set.Contains(Words * 64 - 1), "lost the last line");
    }

    [Theory]
    [InlineData(3)]      // dense: past one match in 32 lines the builder switches to a bit per line
    [InlineData(5_000)]  // sparse: a sorted list of matching lines
    public void Set_builder_round_trips_matches(int matchEvery)
    {
        const long lines = 300_000;
        var builder = new FilterMatchCache.SetBuilder(lines);
        var expected = new List<long>();
        for (long word = 0; word < (lines + 63) / 64; word++)
        {
            ulong bits = 0;
            for (int b = 0; b < 64; b++)
            {
                long line = word * 64 + b;
                if (line < lines && line % matchEvery == 0) { bits |= 1UL << b; expected.Add(line); }
            }
            builder.AddWord(word, bits);
        }

        var set = builder.Build(lines);
        Assert.Equal(expected.Count, set.Matches);
        foreach (long line in expected) Assert.True(set.Contains(line), $"line {line} should be present");
        for (long line = 0; line < lines; line += 997)
            Assert.Equal(line % matchEvery == 0, set.Contains(line));
        Assert.False(set.Contains(-1));
        Assert.False(set.Contains(lines));
    }

    [Theory]
    [InlineData(3)]        // dense storage
    [InlineData(5_000)]    // sparse storage
    [InlineData(0)]        // nothing matches at all
    public void Half_built_results_can_be_scanned_for_the_next_match(int matchEvery)
    {
        // A find reads a pass that is still running, so the builder has to answer "where is the next match"
        // over the part of the file swept so far - and never about a line beyond it, however much it happens
        // to know. Checked against a brute-force walk of the same data.
        const long lines = 300_000;
        var builder = new FilterMatchCache.SetBuilder(lines);
        var expected = new List<long>();
        for (long word = 0; word < (lines + 63) / 64; word++)
        {
            ulong bits = 0;
            for (int b = 0; b < 64; b++)
            {
                long line = word * 64 + b;
                if (line < lines && matchEvery > 0 && line % matchEvery == 0) { bits |= 1UL << b; expected.Add(line); }
            }
            builder.AddWord(word, bits);
        }

        long Reference(long from, bool forward, long covered)
        {
            if (forward)
            {
                foreach (long l in expected) if (l >= from && l < covered) return l;
                return -1;
            }
            long best = -1;
            foreach (long l in expected) { if (l > from || l >= covered) break; best = l; }
            return best;
        }

        foreach (long covered in new long[] { 0, 1, 64, 65, 12_345, 199_999, lines })
            foreach (long from in new long[] { -5, 0, 1, 63, 64, 100, 12_344, 12_345, 199_998, lines - 1, lines, lines + 10 })
            {
                Assert.Equal(Reference(from, true, covered), builder.Next(from, covered));
                Assert.Equal(Reference(from, false, covered), builder.Previous(from, covered));
            }
    }

    [Fact]
    public void Scanning_a_half_built_set_never_reports_a_line_the_pass_has_not_reached()
    {
        // The builder grows a word at a time and its storage runs ahead of the sweep in whole words, so the
        // extent has to be honoured exactly or a find would answer with a line the pass has not settled.
        var builder = new FilterMatchCache.SetBuilder(64);
        builder.AddWord(0, 1UL << 10);      // line 10
        builder.AddWord(9, 1UL << 3);       // line 579

        Assert.Equal(10, builder.Next(0, 64));
        Assert.Equal(-1, builder.Next(11, 64));          // 579 is known but not yet covered
        Assert.Equal(-1, builder.Next(11, 579));         // covered stops one short of it
        Assert.Equal(579, builder.Next(11, 580));
        Assert.Equal(10, builder.Previous(1000, 64));    // clamped to the covered extent
        Assert.Equal(579, builder.Previous(1000, 580));
        Assert.Equal(-1, builder.Previous(9, 64));
        Assert.Equal(-1, builder.Next(0, 0));
        Assert.Equal(-1, builder.Previous(0, 0));
    }

    [Fact]
    public void Combine_applies_include_and_exclude_rules()
    {
        const long lines = 500;
        FilterMatchCache.MatchSet Make(Func<long, bool> predicate)
        {
            var b = new FilterMatchCache.SetBuilder(lines);
            for (long w = 0; w < (lines + 63) / 64; w++)
            {
                ulong bits = 0;
                for (int i = 0; i < 64; i++)
                {
                    long line = w * 64 + i;
                    if (line < lines && predicate(line)) bits |= 1UL << i;
                }
                b.AddWord(w, bits);
            }
            return b.Build(lines);
        }

        var evens = Make(l => l % 2 == 0);
        var thirds = Make(l => l % 3 == 0);
        var shown = new ulong[(lines + 63) / 64];

        FilterMatchCache.Combine(new[] { evens }, new[] { thirds }, hasEnabledInclude: true, lines, shown);
        for (long l = 0; l < lines; l++)
            Assert.Equal(l % 2 == 0 && l % 3 != 0, (shown[l >> 6] & (1UL << (int)(l & 63))) != 0);

        // No enabled includes: everything except the excludes.
        FilterMatchCache.Combine(Array.Empty<FilterMatchCache.MatchSet>(), new[] { thirds },
            hasEnabledInclude: false, lines, shown);
        for (long l = 0; l < lines; l++)
            Assert.Equal(l % 3 != 0, (shown[l >> 6] & (1UL << (int)(l & 63))) != 0);

        // Nothing past the end of the file may be marked visible.
        int tail = (int)(lines & 63);
        if (tail != 0) Assert.Equal(0UL, shown[^1] >> tail);
    }

    [Fact]
    public void Combine_agrees_with_a_line_by_line_reference_whatever_shape_the_sets_are()
    {
        // Combine treats the shapes completely differently - a set that matches nothing is skipped, a sparse
        // one is scattered bit by bit, a dense one is walked a word at a time - so the mix is the whole test.
        // A real filter file is mostly the first two: of 156 filters over a 72 M-line log, 43 matched nothing,
        // 108 matched under a tenth of a percent each, and 5 were dense.
        var rng = new Random(20260813);

        (FilterMatchCache.MatchSet Set, HashSet<long> Members) Make(double density, long covered)
        {
            var members = new HashSet<long>();
            var b = new FilterMatchCache.SetBuilder(covered);
            for (long w = 0; w < (covered + 63) / 64; w++)
            {
                ulong bits = 0;
                for (int i = 0; i < 64; i++)
                {
                    long line = w * 64 + i;
                    if (line < covered && rng.NextDouble() < density) { bits |= 1UL << i; members.Add(line); }
                }
                b.AddWord(w, bits);
            }
            return (b.Build(covered), members);
        }

        // Sets may cover more of the file than the combine is asked about (indexing runs ahead), so every
        // shape is also tried with lines past the end.
        foreach (long lines in new long[] { 1, 63, 64, 65, 4_097, 100_000, 100_001 })
            foreach (long extra in new long[] { 0, 500 })
                foreach (bool hasEnabledInclude in new[] { true, false })
                {
                    long covered = lines + extra;
                    var inc = new[] { 0.0, 0.00001, 0.002, 0.6 }.Select(d => Make(d, covered)).ToArray();
                    var exc = new[] { 0.0, 0.003, 0.4 }.Select(d => Make(d, covered)).ToArray();

                    var shown = new ulong[(lines + 63) / 64 + 3];        // headroom must be left alone
                    Array.Fill(shown, 0xDEADBEEFDEADBEEFUL);
                    FilterMatchCache.Combine(inc.Select(x => x.Set).ToArray(), exc.Select(x => x.Set).ToArray(),
                                             hasEnabledInclude, lines, shown);

                    for (long l = 0; l < lines; l++)
                    {
                        bool included = !hasEnabledInclude || inc.Any(x => x.Members.Contains(l));
                        bool expected = included && !exc.Any(x => x.Members.Contains(l));
                        bool actual = (shown[l >> 6] & (1UL << (int)(l & 63))) != 0;
                        Assert.True(expected == actual,
                            $"line {l} of {lines} (covered {covered}, includes on {hasEnabledInclude}): " +
                            $"expected {expected}, got {actual}");
                    }

                    int words = (int)((lines + 63) / 64);
                    int tail = (int)(lines & 63);
                    if (tail != 0) Assert.Equal(0UL, shown[words - 1] >> tail);   // nothing past the last line
                    for (int w = words; w < shown.Length; w++) Assert.Equal(0xDEADBEEFDEADBEEFUL, shown[w]);
                }
    }

    /// <summary>Deleting a filter has to take its cached results with it. The key is the whole predicate
    /// chain, so a deleted filter's results can never be asked for again - kept, they would simply
    /// accumulate for as long as the file stayed open.</summary>
    [Fact]
    public void Deleting_a_filter_frees_what_was_cached_for_it()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            long bytesBefore = doc.FilterCacheBytes;
            int entriesBefore = doc.FilterCacheCount;

            filters.Remove(flat[3]);        // "net" at the root: every 7th line
            doc.SetFilters(filters);
            WaitIdle(doc);

            Assert.True(doc.FilterCacheCount < entriesBefore,
                        $"the removed filter's entry survived ({entriesBefore} -> {doc.FilterCacheCount})");
            Assert.True(doc.FilterCacheBytes < bytesBefore,
                        $"its bytes were never given back ({bytesBefore} -> {doc.FilterCacheBytes})");
        }
        finally { File.Delete(path); }
    }

    /// <summary>Editing one filter repeatedly is the case that used to grow without bound: every edit makes
    /// a new key, and the key it replaces can never be reached again.</summary>
    [Fact]
    public void Editing_a_filter_repeatedly_does_not_pile_up_dead_entries()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            int settled = doc.FilterCacheCount;

            for (int i = 0; i < 20; i++)
            {
                flat[3].Match.Text = "net" + i;
                doc.SetFilters(filters);
                WaitIdle(doc);
            }

            Assert.Equal(settled, doc.FilterCacheCount);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Turning a filter off must NOT discard its results - keeping them is exactly what makes
    /// turning it back on instant.</summary>
    [Fact]
    public void Disabling_a_filter_keeps_its_results()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            int settled = doc.FilterCacheCount;

            flat[3].Enabled = false;
            doc.SetFilters(filters);
            WaitIdle(doc);

            Assert.Equal(settled, doc.FilterCacheCount);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Removing every filter at once - the Remove All command - is the change that makes the most
    /// cached results dead, and it is the one that used to keep them: with nothing enabled there is no pass
    /// to start, and the pruning lived inside starting one.</summary>
    [Fact]
    public void Removing_every_filter_frees_everything_cached_for_them()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out _);
            Assert.True(doc.FilterCacheCount > 0);

            filters.Roots.Clear();
            doc.SetFilters(filters);
            WaitIdle(doc);

            Assert.Equal(0, doc.FilterCacheCount);
            Assert.Equal(0, doc.FilterCacheBytes);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Disabling every filter reaches the same "nothing to run" state as removing them, but the
    /// filters are still there - so their results must survive, or turning them back on would rescan.</summary>
    [Fact]
    public void Disabling_every_filter_keeps_their_results()
    {
        string path = WriteLog();
        try
        {
            using var doc = Warmed(path, out var filters, out var flat);
            int settled = doc.FilterCacheCount;

            foreach (var f in flat) f.Enabled = false;
            doc.SetFilters(filters);
            WaitIdle(doc);

            Assert.Equal(settled, doc.FilterCacheCount);

            // And they really are still usable: switching one back on is served without a scan.
            long hits = doc.FilterCacheHits;
            flat[0].Enabled = true;
            doc.SetFilters(filters);
            WaitIdle(doc);
            Assert.True(doc.FilterCacheHits > hits, "turning a filter back on should be served from the cache");
        }
        finally { File.Delete(path); }
    }
}
