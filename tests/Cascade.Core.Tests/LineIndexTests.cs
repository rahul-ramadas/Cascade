using Cascade.Core.Indexing;

namespace Cascade.Core.Tests;

/// <summary>
/// The index stores line starts as 32-bit distances from the first offset on each page, so the things
/// worth pinning are that every offset still reads back exactly, that a line's span is right where one
/// page ends and the next begins, and that a page too wide for a 32-bit distance falls back correctly
/// instead of silently truncating.
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
}
