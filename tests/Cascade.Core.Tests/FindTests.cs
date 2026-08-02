using Cascade.Core.IO;
using Cascade.Core.Find;

namespace Cascade.Core.Tests;

public class FindTests
{
    [Fact]
    public void Literal_forward_and_backward()
    {
        var (src, index, det) = Harness.Build("apple\nbanana\ncherry\nbanana split\ndate");
        try
        {
            var reader = new LineReader(src, det.Encoding);
            var q = new FindQuery("banana", Regex: false, CaseSensitive: false);

            Assert.Equal(1, FindEngine.Find(reader, index, src.Length, index.Count, q, 0, forward: true, CancellationToken.None));
            Assert.Equal(3, FindEngine.Find(reader, index, src.Length, index.Count, q, 2, forward: true, CancellationToken.None));
            Assert.Equal(1, FindEngine.Find(reader, index, src.Length, index.Count, q, 2, forward: false, CancellationToken.None));
            Assert.Equal(-1, FindEngine.Find(reader, index, src.Length, index.Count, q, 4, forward: true, CancellationToken.None));
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void Regex_and_case_insensitive()
    {
        var (src, index, det) = Harness.Build("Error 1\ninfo\nERROR 2\nwarn");
        try
        {
            var reader = new LineReader(src, det.Encoding);
            var q = new FindQuery(@"error\s\d", Regex: true, CaseSensitive: false);
            Assert.Equal(0, FindEngine.Find(reader, index, src.Length, index.Count, q, 0, true, CancellationToken.None));
            Assert.Equal(2, FindEngine.Find(reader, index, src.Length, index.Count, q, 1, true, CancellationToken.None));
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void FindInRows_searches_only_the_visible_rows()
    {
        // Lines with "banana" are the ODD-numbered lines (1, 3, 5).
        var (src, index, det) = Harness.Build("apple\nbanana\ncherry\nbanana split\ndate\nbanana bread");
        try
        {
            var reader = new LineReader(src, det.Encoding);
            var q = new FindQuery("banana", Regex: false, CaseSensitive: false);

            // A view that shows only the EVEN lines (0,2,4) must NOT find "banana", even though the file
            // contains it on hidden lines — this is the filtered-mode "don't jump to a hidden match" rule.
            long[] evenView = { 0, 2, 4 };
            Assert.Equal(-1, FindEngine.FindInRows(reader, index, src.Length, evenView.Length, r => evenView[r], q, 0, true, CancellationToken.None));

            // A view of the odd lines (1,3,5) finds them, and the returned value is the FILE line.
            long[] oddView = { 1, 3, 5 };
            Func<long, long> map = r => oddView[r];
            Assert.Equal(1, FindEngine.FindInRows(reader, index, src.Length, oddView.Length, map, q, 0, true, CancellationToken.None));
            Assert.Equal(3, FindEngine.FindInRows(reader, index, src.Length, oddView.Length, map, q, 1, true, CancellationToken.None));
            Assert.Equal(5, FindEngine.FindInRows(reader, index, src.Length, oddView.Length, map, q, 2, true, CancellationToken.None));
            Assert.Equal(3, FindEngine.FindInRows(reader, index, src.Length, oddView.Length, map, q, 1, false, CancellationToken.None));
        }
        finally { src.Dispose(); }
    }

    [Fact]
    public void Reports_progress_and_honors_cancellation()
    {
        var (src, index, det) = Harness.Build(string.Join('\n', Enumerable.Range(0, 200_000).Select(i => "nope " + i)));
        try
        {
            var reader = new LineReader(src, det.Encoding);
            var q = new FindQuery("absent-text", Regex: false, CaseSensitive: false);

            // A full no-match scan reports progress as it goes and returns -1.
            int reports = 0;
            double last = -1;
            long r = FindEngine.Find(reader, index, src.Length, index.Count, q, 0, forward: true,
                CancellationToken.None, f => { reports++; last = f; });
            Assert.Equal(-1, r);
            Assert.True(reports > 1, $"expected multiple progress callbacks, got {reports}");
            Assert.InRange(last, 0.0, 1.0);

            // An already-cancelled token aborts promptly instead of scanning the whole file.
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                FindEngine.Find(reader, index, src.Length, index.Count, q, 0, forward: true, cts.Token));
        }
        finally { src.Dispose(); }
    }

    /// <summary>Highlighting walks a line one match at a time rather than asking the engine for all of them
    /// at once, so the walk has to land on exactly the matches the engine would report - lazy quantifiers
    /// included, since a shortest match is the whole reason to write one.</summary>
    [Theory]
    [InlineData(@"\[.+?\]")]     // lazy: one bracketed field at a time
    [InlineData(@"\[.+\]")]      // greedy: the first bracket to the last
    [InlineData(@"\[[^\]]+\]")]
    [InlineData(@"\d+")]
    [InlineData("^.")]           // anchored: must match once, not once per step
    [InlineData(@"\bdevice\b")]
    [InlineData("(?<=T)[0-9:]+")]
    public void Walking_a_line_finds_exactly_what_the_regex_engine_finds(string pattern)
    {
        const string line = "[2026-07-16T18:06:56][Kernel-PnP][2][0004] processing device 12";
        var matcher = FindEngine.CompileQuery(new FindQuery(pattern, Regex: true, CaseSensitive: true));
        Assert.NotNull(matcher);

        var walked = new List<string>();
        int from = 0;
        while (matcher!.NextMatch(line, from, out int at, out int len))
        {
            walked.Add($"{at}:{len}");
            from = at + Math.Max(1, len);
        }

        var expected = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Matches(line).Select(m => $"{m.Index}:{Math.Max(1, m.Length)}").ToList();

        Assert.Equal(expected, walked);
        Assert.Equal(expected.Count, matcher.CountIn(line));
    }
}
