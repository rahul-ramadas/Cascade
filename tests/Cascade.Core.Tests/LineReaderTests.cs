using System.Text;
using Cascade.Core.IO;

namespace Cascade.Core.Tests;

/// <summary>
/// A reader decodes one line at a time into scratch it reuses. What it is allowed to KEEP matters as much
/// as what it decodes: readers are per thread - a search alone holds one per core - so a buffer sized for
/// the longest line ever seen is that size multiplied by however many readers are alive.
/// </summary>
public class LineReaderTests
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>A file of ordinary lines with one very long line in the middle, and where that line sits.</summary>
    private static (byte[] Bytes, long Start, long End) WithLongLine(int longChars)
    {
        var sb = new StringBuilder();
        sb.Append("first\n");
        long start = sb.Length;
        sb.Append('x', longChars).Append('\n');
        long end = sb.Length;
        sb.Append("last\n");
        return (Utf8.GetBytes(sb.ToString()), start, end);
    }

    [Fact]
    public void A_very_long_line_is_decoded_in_full()
    {
        const int longChars = 2_000_000;
        var (bytes, start, end) = WithLongLine(longChars);
        var src = MemoryMappedTextSource.FromBytes(bytes);
        try
        {
            var reader = new LineReader(src, Utf8);
            Assert.Equal(longChars, reader.GetString(start, end).Length);
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void A_very_long_line_does_not_leave_the_reader_holding_scratch_for_it()
    {
        // One 2 M-character line used to leave ~4 MB of chars held per reader for good - and with the 8 MB
        // line limit, up to about 16 MB. Readers are per thread, so that is multiplied by however many
        // are alive: a search alone keeps one per core.
        const int longChars = 2_000_000;
        var (bytes, start, end) = WithLongLine(longChars);
        var src = MemoryMappedTextSource.FromBytes(bytes);
        try
        {
            var reader = new LineReader(src, Utf8);
            reader.GetChars(start, end);

            // Reading ordinary lines again is what hands the big buffer back.
            Assert.Equal("first", reader.GetString(0, 6));
            Assert.True(reader.HeldChars <= LineReader.KeptBufferChars,
                        $"reader kept {reader.HeldChars:N0} chars of scratch after moving on from one long line");

            // ...and the long line still reads in full when it is asked for again.
            Assert.Equal(longChars, reader.GetString(start, end).Length);
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void Repeatedly_reading_long_lines_does_not_allocate_a_buffer_each_time()
    {
        // The other half: a file MADE of long lines must not pay an allocation per line. The scratch for
        // those is pooled, so it is reused rather than rented afresh.
        const int longChars = 600_000;
        var (bytes, start, end) = WithLongLine(longChars);
        var src = MemoryMappedTextSource.FromBytes(bytes);
        try
        {
            var reader = new LineReader(src, Utf8);
            reader.GetChars(start, end);
            // Per THREAD, not process-wide: the suite runs test classes in parallel, so a process-wide
            // counter measures whatever else happens to be running.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) reader.GetChars(start, end);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < longChars, $"{allocated:N0} bytes allocated over 20 reads of one long line");
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void Ordinary_lines_reuse_one_buffer_rather_than_growing_it_every_time()
    {
        // The flip side: the scratch exists so that reading a screenful does not allocate per line.
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++) sb.Append("a reasonably typical log line, number ").Append(i).Append('\n');
        byte[] bytes = Utf8.GetBytes(sb.ToString());
        var src = MemoryMappedTextSource.FromBytes(bytes);
        try
        {
            var reader = new LineReader(src, Utf8);
            long at = 0;
            int settled = 0;
            for (int i = 0; i < 500; i++)
            {
                long next = at;
                while (bytes[next] != (byte)'\n') next++;
                reader.GetChars(at, next + 1);
                if (i == 0) settled = reader.HeldChars;
                at = next + 1;
            }
            Assert.Equal(settled, reader.HeldChars);
        }
        finally { src.Dispose(); }
    }
}
