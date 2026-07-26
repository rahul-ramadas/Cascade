using System.Text;

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
}
