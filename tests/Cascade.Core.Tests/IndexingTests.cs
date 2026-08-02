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
