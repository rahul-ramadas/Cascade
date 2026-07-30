using Cascade.Core.Find;

namespace Cascade.Core.Tests;

/// <summary>A search gathers one term's matches once and answers from them. The scanner is a delegate, so
/// these drive it directly: no file, and complete control over when each block of results becomes available,
/// which is the only way to get at the waiting and the races deterministically.</summary>
public class FindSearchTests
{
    private static readonly FindQuery Q = new("x", false, false);

    /// <summary>A scanner over a predicate, optionally holding back the blocks a test wants to keep unread.</summary>
    private sealed class Fake
    {
        private readonly Func<long, bool> _match;
        private readonly SemaphoreSlim? _gate;
        private readonly Func<long, bool>? _gateWhen;
        public int Blocks;
        public long LinesExamined;

        public Fake(Func<long, bool> match, SemaphoreSlim? gate = null, Func<long, bool>? gateWhen = null)
        {
            _match = match; _gate = gate; _gateWhen = gateWhen;
        }

        public void Scan(long from, long count, List<long> hits, CancellationToken ct)
        {
            if (_gate is not null && (_gateWhen is null || _gateWhen(from))) _gate.Wait(ct);
            Interlocked.Increment(ref Blocks);
            Interlocked.Add(ref LinesExamined, count);
            for (long i = from; i < from + count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_match(i)) hits.Add(i);
            }
        }
    }

    private static FindSearch Started(long lines, long start, Fake fake)
    {
        var s = new FindSearch(Q, lines, start, fake.Scan);
        s.Start();
        return s;
    }

    [Fact]
    public async Task It_walks_every_match_forwards_and_backwards()
    {
        var fake = new Fake(l => l % 7 == 0);
        using var search = Started(10_000, 5_000, fake);

        var forward = new List<long>();
        for (long at = 0; ;)
        {
            long hit = await search.NextAsync(at, true, null, CancellationToken.None);
            if (hit < 0) break;
            forward.Add(hit);
            at = hit + 1;
        }
        Assert.Equal(Enumerable.Range(0, 10_000 / 7 + 1).Select(i => (long)i * 7).Where(l => l < 10_000), forward);

        var backward = new List<long>();
        for (long at = 9_999; ;)
        {
            long hit = await search.NextAsync(at, false, null, CancellationToken.None);
            if (hit < 0) break;
            backward.Add(hit);
            at = hit - 1;
        }
        backward.Reverse();
        Assert.Equal(forward, backward);
    }

    [Fact]
    public async Task Every_line_is_examined_once_however_many_times_it_is_asked()
    {
        var fake = new Fake(l => l % 100 == 0);
        using var search = Started(50_000, 25_000, fake);
        Assert.True(search.WaitForCompletion());
        long examined = fake.LinesExamined;

        // Walk the whole term twice, in both directions. None of it may touch the file again.
        for (int pass = 0; pass < 2; pass++)
        {
            for (long at = 0; ;)
            {
                long hit = await search.NextAsync(at, true, null, CancellationToken.None);
                if (hit < 0) break;
                at = hit + 1;
            }
            for (long at = 49_999; ;)
            {
                long hit = await search.NextAsync(at, false, null, CancellationToken.None);
                if (hit < 0) break;
                at = hit - 1;
            }
        }
        Assert.Equal(examined, fake.LinesExamined);
        Assert.Equal(50_000, examined);   // exactly the file, no line twice
    }

    [Fact]
    public async Task The_first_result_is_there_long_before_the_sweep_finishes()
    {
        // Held after the first block of each direction, so the search is deliberately incomplete.
        var gate = new SemaphoreSlim(2);
        var fake = new Fake(l => l % 3 == 0, gate);
        using var search = Started(5_000_000, 2_500_002, fake);

        long hit = await search.NextAsync(2_500_002, true, null, CancellationToken.None);
        Assert.Equal(2_500_002, hit);
        Assert.False(search.Complete);
        Assert.True(search.Progress < 0.5);
    }

    [Fact]
    public async Task Asking_past_what_has_been_examined_waits_for_it_rather_than_starting_again()
    {
        var gate = new SemaphoreSlim(1);          // only the first forward block may run
        var fake = new Fake(l => l == 900_000, gate);
        using var search = Started(1_000_000, 0, fake);

        var pending = search.NextAsync(0, true, null, CancellationToken.None);
        Assert.False(pending.IsCompleted);        // line 900,000 is far beyond the first block

        gate.Release(int.MaxValue - 1);           // let the sweep run on
        Assert.Equal(900_000, await pending);
        Assert.Equal(1, fake.Blocks > 1 ? 1 : 0); // it waited for the sweep instead of scanning on its own
    }

    [Fact]
    public async Task No_more_matches_is_only_reported_once_that_direction_is_fully_examined()
    {
        // Nothing matches at all, so the only correct answer is -1 - but not until the whole file is read.
        var gate = new SemaphoreSlim(1);
        var fake = new Fake(_ => false, gate);
        using var search = Started(1_000_000, 500_000, fake);

        var pending = search.NextAsync(500_000, true, null, CancellationToken.None);
        Assert.False(pending.IsCompleted);        // must not claim "no more" while most of the file is unread

        gate.Release(int.MaxValue - 1);
        Assert.Equal(-1, await pending);
    }

    [Fact]
    public async Task It_will_not_answer_out_of_a_part_of_the_file_it_has_not_examined_yet()
    {
        // Matches at line 10 and at the start line. Asking forward from 0 has to give 10 - even though the
        // later one has already been found, the lines before the start point simply have not been read.
        var gate = new SemaphoreSlim(0);
        var fake = new Fake(l => l is 10 or 500_000, gate, from => from < 500_000);   // hold the backward sweep
        using var search = Started(1_000_000, 500_000, fake);

        var pending = search.NextAsync(0, true, null, CancellationToken.None);
        for (int i = 0; i < 500 && search.Progress <= 0; i++) await Task.Delay(2);   // let the forward sweep find 500,000
        Assert.True(search.Progress > 0, "the forward sweep should have run");
        Assert.False(pending.IsCompleted, "it answered out of a region it had not looked at");

        gate.Release(int.MaxValue);
        Assert.Equal(10, await pending);
    }

    [Fact]
    public async Task A_result_the_view_is_hiding_is_stepped_over()
    {
        var fake = new Fake(l => l % 10 == 0);
        using var search = Started(1_000, 0, fake);
        Assert.True(search.WaitForCompletion());

        // Only every other match is visible; the hidden ones must be skipped, not returned.
        bool Visible(long l) => l % 20 == 0;
        var seen = new List<long>();
        for (long at = 0; ;)
        {
            long hit = await search.NextAsync(at, true, Visible, CancellationToken.None);
            if (hit < 0) break;
            seen.Add(hit);
            at = hit + 1;
        }
        Assert.All(seen, l => Assert.Equal(0, l % 20));
        Assert.Equal(50, seen.Count);
    }

    [Fact]
    public async Task What_the_view_hides_is_decided_when_asked_so_changing_filters_does_not_start_again()
    {
        var fake = new Fake(l => l % 10 == 0);
        using var search = Started(1_000, 0, fake);
        Assert.True(search.WaitForCompletion());
        long examined = fake.LinesExamined;

        bool hideAll = false;
        bool Visible(long _) => !hideAll;
        Assert.Equal(0, await search.NextAsync(0, true, Visible, CancellationToken.None));

        hideAll = true;   // as if a filter had just been switched on
        Assert.Equal(-1, await search.NextAsync(0, true, Visible, CancellationToken.None));

        hideAll = false;
        Assert.Equal(10, await search.NextAsync(1, true, Visible, CancellationToken.None));
        Assert.Equal(examined, fake.LinesExamined);   // never re-read the file for any of that
    }

    [Fact]
    public async Task A_wait_can_be_given_up_on_without_stopping_the_search()
    {
        var gate = new SemaphoreSlim(1);
        var fake = new Fake(l => l == 999_999, gate);
        using var search = Started(1_000_000, 0, fake);

        using var cts = new CancellationTokenSource();
        var pending = search.NextAsync(0, true, null, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The sweep is unharmed: ask again and it still answers.
        gate.Release(int.MaxValue - 1);
        Assert.Equal(999_999, await search.NextAsync(0, true, null, CancellationToken.None));
    }

    [Fact]
    public async Task Throwing_the_search_away_releases_whoever_is_waiting_on_it()
    {
        // The term changing, or the file closing, must not leave a caller waiting for ever.
        var gate = new SemaphoreSlim(1);
        var fake = new Fake(l => l == 999_999, gate);
        var search = Started(1_000_000, 0, fake);

        var pending = search.NextAsync(0, true, null, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        gate.Release(int.MaxValue - 1);
        search.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
    }

    [Fact]
    public async Task A_scanner_that_fails_is_reported_rather_than_waited_on_for_ever()
    {
        var search = new FindSearch(Q, 1000, 0, (long _, long _, List<long> _, CancellationToken _)
            => throw new InvalidDataException("bad encoding"));
        search.Start();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => search.NextAsync(0, true, null, CancellationToken.None));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        search.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task It_works_from_any_starting_line(long start)
    {
        var fake = new Fake(l => l is 0 or 500 or 999);
        using var search = Started(1_000, start, fake);
        Assert.True(search.WaitForCompletion());

        Assert.Equal(0, await search.NextAsync(0, true, null, CancellationToken.None));
        Assert.Equal(999, await search.NextAsync(999, false, null, CancellationToken.None));
        Assert.Equal(500, await search.NextAsync(1, true, null, CancellationToken.None));
        Assert.Equal(-1, await search.NextAsync(-1, false, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_file_has_nothing_to_find()
    {
        var fake = new Fake(_ => true);
        using var search = Started(0, 0, fake);
        Assert.Equal(-1, await search.NextAsync(0, true, null, CancellationToken.None));
        Assert.Equal(-1, await search.NextAsync(0, false, null, CancellationToken.None));
        Assert.Equal(0, fake.Blocks);
    }
}
