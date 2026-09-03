using System.Text;
using Cascade.Core.IO;
using Cascade.Core.Indexing;

namespace Cascade.Core.Tests;

public class IndexingTests
{
    [Fact]
    public void Unix_newlines()
        => Assert.Equal(new[] { "a", "b", "c" }, Harness.Lines("a\nb\nc"));

    [Fact]
    public void Windows_newlines()
        => Assert.Equal(new[] { "a", "b", "c" }, Harness.Lines("a\r\nb\r\nc"));

    [Fact]
    public void Trailing_newline_does_not_add_empty_line()
        => Assert.Equal(new[] { "a", "b" }, Harness.Lines("a\nb\n"));

    [Fact]
    public void Trailing_crlf_does_not_add_empty_line()
        => Assert.Equal(new[] { "a", "b" }, Harness.Lines("a\r\nb\r\n"));

    [Fact]
    public void Empty_lines_are_preserved()
        => Assert.Equal(new[] { "a", "", "b" }, Harness.Lines("a\n\nb"));

    [Fact]
    public void No_final_newline()
        => Assert.Equal(new[] { "x", "y" }, Harness.Lines("x\ny"));

    [Fact]
    public void Single_line()
        => Assert.Equal(new[] { "only" }, Harness.Lines("only"));

    [Fact]
    public void Empty_file_has_no_lines()
        => Assert.Empty(Harness.Lines(""));

    // Every other test here uses a file of a few bytes, which is below the size at which the indexer
    // bothers to read ahead - so without these two the read-ahead never runs in the suite at all.

    [Fact]
    public void A_file_big_enough_to_read_ahead_indexes_to_the_same_thing()
    {
        string path = Harness.TempFile(BigLog(out int expected));
        try
        {
            using var src = new MemoryMappedTextSource(path);
            var index = new LineIndex();
            new LineIndexer(src, index, 0, 1, false).Run(null, CancellationToken.None);

            Assert.Equal(expected, index.Count);
            var reader = new LineReader(src, new UTF8Encoding(false));
            index.GetRange(0, src.Length, out long s, out long e);
            Assert.Equal("line 0", reader.GetString(s, e));
            index.GetRange(expected - 1, src.Length, out s, out e);
            Assert.Equal($"line {expected - 1}", reader.GetString(s, e));
            index.GetRange(expected / 2, src.Length, out s, out e);
            Assert.Equal($"line {expected / 2}", reader.GetString(s, e));

            // Losing the read-ahead costs a large file well over half its speed and breaks no result,
            // so without this the whole change could be deleted and every test would still pass.
            Assert.Equal(src.Length, src.PrefetchedBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_file_too_small_to_be_worth_it_is_not_read_ahead()
    {
        string path = Harness.TempFile(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma"));
        try
        {
            using var src = new MemoryMappedTextSource(path);
            var index = new LineIndex();
            new LineIndexer(src, index, 0, 1, false).Run(null, CancellationToken.None);

            Assert.Equal(3, index.Count);
            Assert.Equal(0, src.PrefetchedBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cancelling_a_scan_does_not_leave_the_read_ahead_running()
    {
        string path = Harness.TempFile(BigLog(out _));
        try
        {
            using var src = new MemoryMappedTextSource(path);
            using var cts = new CancellationTokenSource();
            var indexer = new LineIndexer(src, new LineIndex(), 0, 1, false);

            // Reaching the second chunk means the read-ahead is under way, so the cancel lands mid-scan.
            Assert.Throws<OperationCanceledException>(() => indexer.Run(
                p => { if (p.LineCount > 0) cts.Cancel(); }, cts.Token));

            // Run only returns once the read-ahead has been joined; anything else would hang here.
            Assert.False(indexer.IsComplete);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A log comfortably past the 32 MB the indexer starts reading ahead at.</summary>
    private static byte[] BigLog(out int lines)
    {
        var text = new StringBuilder(40 * 1024 * 1024);
        lines = 0;
        while (text.Length < 40 * 1024 * 1024) text.Append("line ").Append(lines++).Append('\n');
        text.Length--;   // no trailing newline, so the last line is a line of its own
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    [Fact]
    public void Just_a_newline_is_one_empty_line()
        => Assert.Equal(new[] { "" }, Harness.Lines("\n"));

    [Fact]
    public void Utf8_bom_is_stripped_from_first_line()
    {
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes("héllo\nwörld"));
        var (src, index, det) = Harness.BuildFromBytes(bytes.ToArray());
        try
        {
            Assert.Equal(3, det.PreambleLength);
            Assert.Equal(new[] { "héllo", "wörld" }, Harness.ReadAll(src, index, det));
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void Utf16_le_with_bom_indexes_correctly()
    {
        var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        byte[] bytes = enc.GetPreamble().Concat(enc.GetBytes("alpha\nbeta\ngamma")).ToArray();
        var (src, index, det) = Harness.BuildFromBytes(bytes, enc);
        try
        {
            Assert.Equal(2, det.UnitSize);
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, Harness.ReadAll(src, index, det));
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void Many_lines_count_is_exact()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 100_000; i++) sb.Append("line ").Append(i).Append('\n');
        var (src, index, _) = Harness.Build(sb.ToString());
        try { Assert.Equal(100_000, index.Count); }
        finally { src.Dispose(); }
    }

    [Theory]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public void Multi_byte_lines_are_found_across_chunk_boundaries(string name)
    {
        // The scan works through the file in 4 MB chunks and searches whole code units within each one, so
        // the seams are where a newline would go missing. Blank lines and a character CONTAINING a 0x0A byte
        // are in the mix, since those are what a search over the wrong unit gets wrong.
        Encoding enc = name switch
        {
            "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf-32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            _ => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        };

        const int lines = 400_000;
        var sb = new StringBuilder();
        var expected = new List<string>(lines);
        for (int i = 0; i < lines; i++)
        {
            string text = i % 1000 == 0 ? "" : $"line \u0a41{i}\u410a";
            expected.Add(text);
            sb.Append(text).Append('\n');
        }
        byte[] bytes = enc.GetPreamble().Concat(enc.GetBytes(sb.ToString())).ToArray();
        Assert.True(bytes.Length > 8 * 1024 * 1024, $"need several chunks to cross a seam, got {bytes.Length} bytes");

        var (src, index, det) = Harness.BuildFromBytes(bytes, enc);
        try
        {
            Assert.Equal(lines, index.Count);
            Assert.Equal(expected, Harness.ReadAll(src, index, det));
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void Progress_is_reported_in_bytes_as_the_scan_moves_through_the_file()
    {
        // The status bar used to show a marquee here because the LINE count is unknowable until the scan
        // ends. The byte position is not: the file's size is known before it starts, which is what lets the
        // bar show a real percentage. The scan works in chunks, so this has to report intermediate
        // positions and not merely jump from nothing to everything.
        var sb = new StringBuilder();
        for (int i = 0; i < 400_000; i++) sb.Append("a reasonably typical looking log line number ").Append(i).Append('\n');
        byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        Assert.True(bytes.Length > 16 * 1024 * 1024, $"need several chunks to see progress, got {bytes.Length} bytes");

        var src = MemoryMappedTextSource.FromBytes(bytes);
        try
        {
            var index = new LineIndex();
            var indexer = new LineIndexer(src, index, 0, 1, false);
            Assert.Equal(0, indexer.ProcessedByteCount);

            var seen = new List<long>();
            indexer.Run(_ => seen.Add(indexer.ProcessedByteCount), CancellationToken.None);

            Assert.True(seen.Count >= 4, $"expected several progress reports, got {seen.Count}");
            Assert.Equal(seen.OrderBy(v => v), seen);                     // never goes backwards
            Assert.True(seen[0] > 0 && seen[0] < bytes.Length, $"first report {seen[0]} of {bytes.Length}");
            Assert.Equal(bytes.Length, indexer.ProcessedByteCount);       // and arrives at the end exactly
        }
        finally { src.Dispose(); }
    }
}
