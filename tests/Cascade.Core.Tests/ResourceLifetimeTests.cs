using System.Diagnostics;
using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Filtering;
using Cascade.Core.Find;
using Cascade.Core.Indexing;
using Cascade.Core.IO;
using Cascade.Core.Markers;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>
/// The file is handed to readers as a raw pointer into a memory mapping, so freeing it while one of them is
/// still inside a scan is an access violation rather than a failed assertion. Cancellation cannot be relied
/// on to have taken effect: a pass can be deep in work that never looks at the token - a regular expression
/// is the usual one - so these pin down that the mapping is released only once every reader has really
/// stopped, however long that takes.
/// </summary>
public class ResourceLifetimeTests
{
    private const int Lines = 100_000;   // more than one 32,768-line block, so a pass can be held mid-file

    private static string WriteLog(string tag = "MATCH")
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Lines; i++) sb.Append(i % 5 == 0 ? tag + " " : "other ").Append("line ").Append(i).Append('\n');
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static FilterCollection Filters()
    {
        var filters = new FilterCollection();
        filters.Add(new Filter { Enabled = true, Match = { Text = "MATCH" } });
        return filters;
    }

    private static void WaitFor(Func<bool> done, string what, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return;
            Thread.Sleep(2);
        }
        throw new TimeoutException(what);
    }

    /// <summary>Whether <paramref name="task"/> finishes within the grace period.</summary>
    private static async Task<bool> Finishes(Task task, int withinMs)
        => ReferenceEquals(await Task.WhenAny(task, Task.Delay(withinMs)).ConfigureAwait(false), task);

    [Fact]
    public async Task Reopening_does_not_free_the_file_while_a_pass_is_still_reading_it()
    {
        // The reported crash: opening another file cancelled the pass, waited a bounded time for it, and
        // then unmapped regardless. A pass that had not stopped went on to read the freed mapping and took
        // the process down. Holding the pass inside a block reproduces exactly that timing, deterministically.
        string a = WriteLog(), b = WriteLog();
        var gate = new SemaphoreSlim(0);
        int reached = 0;
        var doc = new CascadeDocument();
        try
        {
            doc.Open(a);
            doc.WaitForIndex();
            doc.FilterCheckpointForTesting = _ =>
            {
                if (Interlocked.Exchange(ref reached, 1) == 0) gate.Wait(TimeSpan.FromSeconds(30));
            };
            doc.SetFilters(Filters());
            WaitFor(() => Volatile.Read(ref reached) == 1, "the pass never reached its first block");

            doc.FilterCheckpointForTesting = null;   // the next file's pass must run freely
            doc.Open(b);                             // retires A while its pass is still inside a block

            Assert.False(await Finishes(doc.ReleasePending, 500),
                         "the mapping was released while a pass was still reading through it");

            gate.Release(1000);
            await doc.ReleasePending.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            gate.Release(1000);
            doc.FilterCheckpointForTesting = null;
            doc.Dispose();
            gate.Dispose();
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task Closing_leaves_the_mapping_alone_when_a_pass_will_not_stop()
    {
        // Same invariant on the way out, and the case that matters: closing may give up waiting - the
        // process is going anyway - but it must then leave the mapping to the kernel rather than free it
        // under a live reader.
        string path = WriteLog();
        var gate = new SemaphoreSlim(0);
        int reached = 0, released = 0;
        var doc = new CascadeDocument();
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            doc.FilterCheckpointForTesting = _ =>
            {
                if (Interlocked.Exchange(ref reached, 1) == 0) gate.Wait(TimeSpan.FromSeconds(30));
            };
            doc.SetFilters(Filters());
            WaitFor(() => Volatile.Read(ref reached) == 1, "the pass never reached its first block");

            doc.ReleaseWaitMs = 200;
            doc.ReleaseDelayForTesting = () => Interlocked.Increment(ref released);

            var closing = Task.Run(doc.Dispose);
            await closing.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(0, Volatile.Read(ref released));   // freeing it here is the access violation
            Assert.False(doc.ReleasePending.IsCompleted);
        }
        finally
        {
            gate.Release(1000);
            gate.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_settled_document_releases_the_file_it_let_go_of()
    {
        // The other half of the promise: when the readers really have stopped, the mapping must be freed
        // rather than held for the life of the process.
        string a = WriteLog(), b = WriteLog();
        var doc = new CascadeDocument();
        int released = 0;
        doc.ReleaseDelayForTesting = () => Interlocked.Increment(ref released);
        try
        {
            doc.SetFilters(Filters());
            doc.Open(a);
            doc.WaitForIndex();
            WaitFor(() => doc.IsFilterIdle, "the first pass never settled");

            int before = Volatile.Read(ref released);
            doc.Open(b);
            await doc.ReleasePending.WaitAsync(TimeSpan.FromSeconds(30));
            WaitFor(() => Volatile.Read(ref released) > before, "the file was never actually let go of");

            doc.WaitForIndex();
            Assert.Equal(Lines, doc.CompletedLineCount);
        }
        finally { doc.Dispose(); File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task Opening_files_back_to_back_while_they_filter_stays_sound()
    {
        // The gesture behind the report - open a file, change your mind, open another - repeated hard, with
        // every pass still running when the next file arrives.
        var paths = Enumerable.Range(0, 6).Select(_ => WriteLog()).ToArray();
        var doc = new CascadeDocument();
        try
        {
            doc.SetFilters(Filters());
            foreach (string path in paths)
            {
                doc.Open(path);
                Assert.Equal(path, doc.FilePath);
            }
            doc.WaitForIndex();
            WaitFor(() => doc.IsFilterIdle, "filtering never settled after the last file");
            await doc.ReleasePending.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(Lines, doc.CompletedLineCount);
            Assert.Equal(Lines / 5, doc.MatchedLineCount);
        }
        finally { doc.Dispose(); foreach (string p in paths) File.Delete(p); }
    }

    /// <summary>Lines that make a backtracking regular expression take a long time, and - the point - take it
    /// inside work that never looks at a cancellation token. This is what a reader can really do to us: a
    /// pattern is theirs to write, and .NET compiles it with no match timeout.</summary>
    private static string WriteSlowToScanLog(int lines = 8, int width = 22)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++) sb.Append('a', width).Append("!\n");
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private const string SlowPattern = "^(a+)+$";

    [Fact]
    public async Task Reopening_waits_for_a_search_whose_term_was_replaced()
    {
        // Replacing the term throws the old search away, but its sweep is still reading the file. It is no
        // longer reachable through the document's fields, so a release that only waits for what is still
        // registered frees the mapping out from under it.
        //
        // The sweep is held at its first block rather than given a pattern that ought to be slow. It used to
        // lean on "^(a+)+$" taking a long time to fail, which is true of a backtracking engine right up until
        // the runtime learns to reject it outright - and then the sweep is over before the reopen, the
        // mapping is free to go, and the test fails on a build where nothing is wrong. A gate is the same
        // scenario with the timing taken out of it.
        string a = WriteSlowToScanLog(), b = WriteLog();
        var gate = new SemaphoreSlim(0);
        int reached = 0;
        var doc = new CascadeDocument();
        try
        {
            doc.Open(a);
            doc.WaitForIndex();
            doc.FindCheckpointForTesting = _ =>
            {
                if (Interlocked.Exchange(ref reached, 1) == 0) gate.Wait(TimeSpan.FromSeconds(30));
            };

            _ = doc.FindNextAsync(new FindQuery(SlowPattern, Regex: true, CaseSensitive: false), 0, true);
            WaitFor(() => Volatile.Read(ref reached) == 1, "the sweep never reached its first block");

            doc.FindCheckpointForTesting = null;    // the replacement must sweep freely
            _ = doc.FindNextAsync(new FindQuery("zzz", Regex: false, CaseSensitive: false), 0, true);

            doc.Open(b);
            Assert.False(await Finishes(doc.ReleasePending, 400),
                         "the mapping was released while a replaced search was still sweeping it");

            gate.Release(1000);
            await doc.ReleasePending.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            gate.Release(1000);
            doc.FindCheckpointForTesting = null;
            doc.Dispose();
            gate.Dispose();
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task Reopening_waits_for_a_find_that_a_newer_one_took_over_from()
    {
        // Same shape for per-filter find: starting another one supersedes the first, which goes on reading.
        // Wider and more of them than the search case: the filter engine compiles its patterns, so the same
        // line costs it less.
        string a = WriteSlowToScanLog(lines: 12, width: 24), b = WriteLog();
        var doc = new CascadeDocument();
        try
        {
            var filters = new FilterCollection();
            var slow = new Filter { Enabled = false, Match = { Text = SlowPattern, Regex = true } };
            filters.Add(slow);
            filters.Add(new Filter { Enabled = false, Match = { Text = "nothing-doing" } });
            doc.SetFilters(filters);
            doc.Open(a);
            doc.WaitForIndex();

            _ = doc.FindLineMatchingFilterAsync(slow, 0, true);
            await Task.Delay(50);
            _ = doc.FindLineMatchingFilterAsync(filters.Roots[1], 0, true);

            doc.Open(b);
            Assert.False(await Finishes(doc.ReleasePending, 400),
                         "the mapping was released while a superseded find was still reading it");

            await doc.ReleasePending.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { doc.Dispose(); File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task A_pass_that_fails_gives_up_its_generation_instead_of_ending_the_process()
    {
        // A pass runs on a background thread, where an escaping exception ends the process with no crash log.
        // Anything unexpected must therefore be recorded and the generation abandoned.
        var (src, index, det) = Harness.Build(string.Concat(Enumerable.Range(0, 1000).Select(i => $"MATCH {i}\n")));
        var service = new FilterService(src, index, src.Length, new MarkerStore(), det.Encoding,
                                        () => index.Count, () => true);
        try
        {
            var boom = new InvalidOperationException("the pass blew up");
            service.AfterBlockForTesting = _ => throw boom;
            service.Restart(FilterSnapshot.Build(Filters()), seedAllVisible: true);
            service.Notify();

            WaitFor(() => service.LastFailure is not null, "the failure was never recorded");
            Assert.Same(boom, service.LastFailure);
            Assert.True(service.IsIdle, "a failed pass must not leave the view reporting itself busy");
            Assert.False(service.Stopped.IsCompleted, "the worker must survive a failed pass");
        }
        finally
        {
            // The worker reads the file, so it has to have stopped before the mapping goes.
            service.Dispose();
            await service.Stopped.WaitAsync(TimeSpan.FromSeconds(30));
            src.Dispose();
        }
    }

    [Fact]
    public async Task A_filter_worker_reports_when_it_has_really_stopped()
    {
        var (src, index, det) = Harness.Build(string.Concat(Enumerable.Range(0, 1000).Select(i => $"MATCH {i}\n")));
        var gate = new SemaphoreSlim(0);
        int reached = 0;
        var service = new FilterService(src, index, src.Length, new MarkerStore(), det.Encoding,
                                        () => index.Count, () => true);
        try
        {
            service.AfterBlockForTesting = _ =>
            {
                if (Interlocked.Exchange(ref reached, 1) == 0) gate.Wait(TimeSpan.FromSeconds(30));
            };
            service.Restart(FilterSnapshot.Build(Filters()), seedAllVisible: true);
            service.Notify();
            WaitFor(() => Volatile.Read(ref reached) == 1, "the pass never started");

            service.Dispose();
            Assert.False(await Finishes(service.Stopped, 500), "a worker inside a block has not stopped");

            gate.Release(1000);
            await service.Stopped.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            gate.Release(1000);
            service.Dispose();
            await service.Stopped.WaitAsync(TimeSpan.FromSeconds(30));
            gate.Dispose();
            src.Dispose();
        }
    }

    [Fact]
    public async Task A_search_reports_when_its_sweep_has_really_stopped()
    {
        var gate = new SemaphoreSlim(0);
        int reached = 0;
        var search = new FindSearch(new FindQuery("x", false, false), 100_000, 50_000,
            (long from, long count, List<FindHit> hits, CancellationToken ct) =>
            {
                if (Interlocked.Exchange(ref reached, 1) == 0) gate.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
            });
        try
        {
            search.Start();
            WaitFor(() => Volatile.Read(ref reached) == 1, "the sweep never started");

            search.Dispose();
            Assert.False(await Finishes(search.Stopped, 500), "a sweep inside a scan has not stopped");

            gate.Release(1000);
            await search.Stopped.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { gate.Release(1000); gate.Dispose(); }
    }
}
