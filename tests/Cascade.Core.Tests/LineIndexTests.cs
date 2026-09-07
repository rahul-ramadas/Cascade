using Cascade.Core.Indexing;

namespace Cascade.Core.Tests;

/// <summary>
/// The index stores a line start as a 16-bit distance from its block of 32, a block's start as a 32-bit
/// distance from its page of 65,536, and a page's start whole — so the things worth pinning are that every
/// offset still reads back exactly, that a line's span is right where one block ends and the next begins
/// and where one page ends and the next begins, and that a block or a page too wide for its distance falls
/// back correctly instead of silently truncating.
/// </summary>
public class LineIndexTests
{
    /// <summary>Enough lines to fill several pages, with uneven spacing so no offset is a round number.</summary>
    private static (LineIndex Index, List<long> Offsets) Build(int count, int maxGap = 500, int seed = 1234)
    {
        var index = new LineIndex();
        var offsets = new List<long>(count);
        var rnd = new Random(seed);
        long at = 0;
        for (int i = 0; i < count; i++)
        {
            offsets.Add(at);
            index.Add(at);
            at += rnd.Next(1, maxGap);
        }
        return (index, offsets);
    }

    [Fact]
    public void Reads_back_every_offset_it_was_given_across_many_pages()
    {
        var (index, offsets) = Build(200_000);

        Assert.Equal(offsets.Count, index.Count);
        for (int i = 0; i < offsets.Count; i++)
            Assert.Equal(offsets[i], index.Get(i));
    }

    [Fact]
    public void A_line_runs_from_its_own_start_to_the_next_lines_start()
    {
        var (index, offsets) = Build(200_000);
        long fileLength = offsets[^1] + 77;

        for (int i = 0; i < offsets.Count; i++)
        {
            index.GetRange(i, fileLength, out long start, out long end);
            Assert.Equal(offsets[i], start);
            // The last line has no successor and must run to the end of the file. Every other line ends
            // where the next begins - including the one whose successor lives on the following page.
            Assert.Equal(i + 1 < offsets.Count ? offsets[i + 1] : fileLength, end);
        }
    }

    [Fact]
    public void A_page_spanning_more_than_four_gigabytes_still_reads_back_exactly()
    {
        // A 32-bit distance cannot express this jump, so the index has to fall back to 64-bit offsets -
        // and the lines recorded before the jump have to survive that switch.
        var index = new LineIndex();
        var offsets = new List<long> { 0, 10, 20 };
        foreach (long o in offsets) index.Add(o);

        long huge = 6L * 1024 * 1024 * 1024;   // 6 GB past this page's first offset
        foreach (long o in new[] { huge, huge + 5, huge + 900 })
        {
            offsets.Add(o);
            index.Add(o);
        }

        Assert.Equal(offsets.Count, index.Count);
        for (int i = 0; i < offsets.Count; i++)
            Assert.Equal(offsets[i], index.Get(i));

        index.GetRange(0, huge + 1000, out long s, out long e);
        Assert.Equal(0, s);
        Assert.Equal(10, e);

        index.GetRange(offsets.Count - 1, huge + 1000, out s, out e);
        Assert.Equal(huge + 900, s);
        Assert.Equal(huge + 1000, e);
    }

    [Fact]
    public void Widening_partway_through_keeps_the_pages_already_written()
    {
        // The overflow lands well past the first page, so the fallback has to carry over everything
        // already recorded rather than just the page it happened on.
        var (index, offsets) = Build(200_000);
        long huge = offsets[^1] + 5L * 1024 * 1024 * 1024;
        offsets.Add(huge);
        index.Add(huge);
        offsets.Add(huge + 3);
        index.Add(huge + 3);

        Assert.Equal(offsets.Count, index.Count);
        for (int i = 0; i < offsets.Count; i++)
            Assert.Equal(offsets[i], index.Get(i));

        // And it keeps working for lines added after the switch, including onto fresh pages.
        long at = huge + 3;
        for (int i = 0; i < 70_000; i++)
        {
            at += 11;
            offsets.Add(at);
            index.Add(at);
        }
        for (int i = 0; i < offsets.Count; i++)
            Assert.Equal(offsets[i], index.Get(i));
    }

    [Fact]
    public void A_reader_sees_every_offset_the_writer_has_published()
    {
        // The documented contract: one writer appends while readers work off Count. A reader that has
        // observed Count > i must be able to read line i, including while pages are being added.
        var index = new LineIndex();
        const int total = 300_000;
        var expected = new long[total];
        for (int i = 0; i < total; i++) expected[i] = i * 37L + 5;

        Exception? failure = null;
        var readers = new Thread[4];
        var stop = new ManualResetEventSlim(false);
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = new Thread(() =>
            {
                try
                {
                    while (!stop.IsSet)
                    {
                        long known = index.Count;
                        for (long i = 0; i < known; i += 997)
                            Assert.Equal(expected[i], index.Get(i));
                        if (known > 0) Assert.Equal(expected[known - 1], index.Get(known - 1));
                    }
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            });
            readers[r].Start();
        }

        for (int i = 0; i < total; i++) index.Add(expected[i]);
        stop.Set();
        foreach (var t in readers) t.Join();

        Assert.Null(failure);
        Assert.Equal(total, index.Count);
    }

    /// <summary>The whole point of the shape. Nothing else can notice it quietly going back to four bytes
    /// a line: every answer would still be right, and only a heap dump would show the difference - which on
    /// a 15.8 GB log is 265 MB against 141 MB, the largest single thing the process holds.</summary>
    [Fact]
    public void An_ordinary_log_costs_two_and_an_eighth_bytes_a_line()
    {
        var (index, _) = Build(200_000);
        Assert.Equal(0, index.WidePageCountForTesting);
        Assert.Equal(2.125, index.BytesPerLineForTesting, 3);
    }

    /// <summary>A block of 32 lines that spans 64 KB or more cannot be written as 16-bit distances. Real
    /// traces never do it - MEASURED over 139 million lines of two 14-16 GB logs, not one block of 32 came
    /// close - but one enormous line is all it takes, and it must cost that page and no more.</summary>
    [Fact]
    public void A_block_too_wide_for_a_16_bit_distance_falls_back_for_that_page_alone()
    {
        var index = new LineIndex();
        var offsets = new List<long>();
        long at = 0;
        // Three pages of ordinary lines, with one line of 200 KB partway through the middle page.
        for (int i = 0; i < 3 * 65_536; i++)
        {
            offsets.Add(at);
            index.Add(at);
            at += i == 100_000 ? 200_000 : 120;
        }

        Assert.Equal(1, index.WidePageCountForTesting);
        for (int i = 0; i < offsets.Count; i++)
            Assert.Equal(offsets[i], index.Get(i));

        long fileLength = offsets[^1] + 9;
        for (int i = 0; i < offsets.Count; i++)
        {
            index.GetRange(i, fileLength, out long s, out long e);
            Assert.Equal(offsets[i], s);
            Assert.Equal(i + 1 < offsets.Count ? offsets[i + 1] : fileLength, e);
        }

        // Two of the three pages are still narrow, so the cost is nearer 2 than 4 bytes a line.
        Assert.InRange(index.BytesPerLineForTesting, 2.7, 3.5);
    }

    /// <summary>The two fallbacks one after the other: a page that has already dropped to 32-bit distances
    /// has to survive the whole index going to 64-bit ones, which reads it back by a different route.</summary>
    [Fact]
    public void A_page_that_already_fell_back_still_widens_to_64_bits_correctly()
    {
        var index = new LineIndex();
        var offsets = new List<long>();
        long at = 0;
        for (int i = 0; i < 70_000; i++)
        {
            offsets.Add(at);
            index.Add(at);
            at += i == 1_000 ? 300_000 : 90;   // forces one page onto the 32-bit path
        }
        Assert.Equal(1, index.WidePageCountForTesting);

        long huge = at + 5L * 1024 * 1024 * 1024;   // and now past what 32 bits can express
        offsets.Add(huge);
        index.Add(huge);
        offsets.Add(huge + 4);
        index.Add(huge + 4);

        Assert.Equal(8, index.BytesPerLineForTesting);
        Assert.Equal(offsets.Count, index.Count);
        for (int i = 0; i < offsets.Count; i++)
            Assert.Equal(offsets[i], index.Get(i));
    }

    /// <summary>A line's end is read from its successor, and the successor is in a different block once in
    /// every thirty-two - the one case a walk that assumed "same block" would get wrong, and the one the
    /// bulk tests above would only notice by accident.</summary>
    [Fact]
    public void A_line_whose_successor_starts_the_next_block_still_ends_where_that_line_begins()
    {
        var (index, offsets) = Build(1_000);
        long fileLength = offsets[^1] + 13;
        for (int i = 31; i + 1 < offsets.Count; i += 32)
        {
            index.GetRange(i, fileLength, out long s, out long e);
            Assert.Equal(offsets[i], s);
            Assert.Equal(offsets[i + 1], e);
        }
    }
}
