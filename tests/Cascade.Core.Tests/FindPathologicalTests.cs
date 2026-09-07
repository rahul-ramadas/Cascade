using System.Text.RegularExpressions;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>
/// A search has to stay affordable on files that are nothing like a log. Every case here is one whose cost
/// is not bounded by the size of the file: a line so long that counting lines says nothing about how much
/// work there is, a term that matches thousands of times on one line, and a pattern the regular-expression
/// engine backtracks over. What is asserted is the ANSWERS - the shapes that make them cheap are measured
/// in a benchmark, not here - plus the one thing a bound can quietly get wrong, which is losing a match.
/// </summary>
public class FindPathologicalTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string f in _files) try { File.Delete(f); } catch { /* a stray temp file is not a failure */ }
        GC.SuppressFinalize(this);
    }

    private string Write(Action<StreamWriter> body)
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_findpath_" + Guid.NewGuid().ToString("N") + ".log");
        _files.Add(path);
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        body(w);
        return path;
    }

    private static async Task<List<long>> WalkAsync(CascadeDocument doc, FindQuery q)
    {
        var found = new List<long>();
        for (long at = 0; ;)
        {
            long line = await doc.FindNextAsync(q, at, forward: true, CancellationToken.None);
            if (line < 0) break;
            found.Add(line);
            at = line + 1;
        }
        return found;
    }

    /// <summary>Blocks and thread groups are sized in bytes, not lines, so a file with very few very long
    /// lines is still cut up. The failure that makes easy is a line dropped at a boundary.</summary>
    [Fact]
    public async Task Every_line_of_a_file_made_of_very_long_lines_is_examined()
    {
        // 400 lines of ~32 KB each: far below any line-counted threshold, and a group of work per line.
        const int Lines = 400;
        string filler = new('.', 4096);
        string path = Write(w =>
        {
            for (int i = 0; i < Lines; i++)
            {
                w.Write("line "); w.Write(i); w.Write(' ');
                for (int k = 0; k < 8; k++)
                {
                    w.Write(filler);
                    if (k == 4 && i % 3 == 0) w.Write("NEEDLE");
                }
                w.Write('\n');
            }
        });

        using var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();

        var expected = Enumerable.Range(0, Lines).Where(i => i % 3 == 0).Select(i => (long)i).ToArray();
        Assert.Equal(expected, await WalkAsync(doc, new FindQuery("NEEDLE", false, false)));

        var tally = doc.FindTally(-1)!.Value;
        Assert.True(tally.Complete);
        Assert.Equal(expected.Length, tally.VisibleLines);
        Assert.Equal(HitCount.Exact, tally.Hits);
    }

    /// <summary>A block is bounded in BYTES, so a file of very long lines is still cut into several of
    /// them. Counted in lines the first block of a file like this one is all of it, which is what made the
    /// first result cost the whole sweep however near the top it was.</summary>
    [Fact]
    public async Task A_file_of_very_long_lines_is_swept_in_more_than_one_block()
    {
        const int Lines = 200;
        string filler = new('.', 8192);
        string path = Write(w =>
        {
            for (int i = 0; i < Lines; i++)
            {
                w.Write("NEEDLE line "); w.Write(i); w.Write(' ');
                for (int k = 0; k < 4; k++) w.Write(filler);
                w.Write('\n');
            }
        });

        using var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        Assert.Equal(Lines, doc.CompletedLineCount);

        var blocks = new System.Collections.Concurrent.ConcurrentBag<long>();
        doc.FindCheckpointForTesting = from => blocks.Add(from);

        Assert.Equal(0, await doc.FindNextAsync(new FindQuery("NEEDLE", false, false), 0, true, CancellationToken.None));
        while (!doc.FindComplete) await Task.Delay(5);

        // Every line matched, so the answer says nothing on its own; what says the file was cut up is that
        // the forward sweep began a block somewhere other than line 0.
        Assert.Contains(blocks, from => from > 0);
    }

    /// <summary>Counting a line's occurrences is the one per-line cost a single line can run away with, so
    /// it stops - and says so, rather than reporting a floor as a fact.</summary>
    [Fact]
    public async Task A_line_that_matches_thousands_of_times_is_counted_up_to_a_point_and_said_to_be_a_floor()
    {
        const int Lines = 64;
        const int Per = 5000;
        string path = Write(w =>
        {
            for (int i = 0; i < Lines; i++)
            {
                w.Write("line "); w.Write(i);
                for (int k = 0; k < Per; k++) w.Write(" needle");
                w.Write('\n');
            }
        });

        using var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();

        var q = new FindQuery("needle", false, false);
        Assert.Equal(0, await doc.FindNextAsync(q, 0, true, CancellationToken.None));
        while (!doc.FindComplete) await Task.Delay(5);

        var tally = doc.FindTally(-1)!.Value;
        Assert.Equal(Lines, tally.VisibleLines);                    // every line, exactly
        Assert.Equal(HitCount.AtLeast, tally.Hits);                 // and the hits are a floor
        Assert.InRange(tally.Occurrences, Lines, (long)Lines * Per); // ...which is still a real number
        Assert.True(tally.Occurrences > Lines, $"floor {tally.Occurrences} says nothing about a line matching often");
    }

    /// <summary>The literal prefilter for "literal .+ literal" has to answer what the regular-expression
    /// engine answers, whatever the line looks like - it decides which lines a search will never revisit.
    /// Compared against <see cref="Regex"/> itself rather than against a table of expected answers.</summary>
    [Theory]
    [InlineData(@"a.+b")]
    [InlineData(@"\[x\].+\[y\]")]
    [InlineData(@"A.+?B")]
    [InlineData(@"ab.+cd.+ef")]
    [InlineData(@"\..+\.")]
    public void A_rewritten_pattern_matches_exactly_what_the_regex_engine_matches(string pattern)
    {
        var rng = new Random(pattern.Length * 7919);
        const string Alphabet = "aAbBcCdDeEfF.[]xXyY \t";
        foreach (bool caseSensitive in (bool[])[true, false])
        {
            var matcher = FindEngine.CompileQuery(new FindQuery(pattern, Regex: true, caseSensitive))!;
            var rx = new Regex(pattern, RegexOptions.CultureInvariant |
                                        (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));
            for (int i = 0; i < 4000; i++)
            {
                var line = new char[rng.Next(0, 40)];
                for (int c = 0; c < line.Length; c++) line[c] = Alphabet[rng.Next(Alphabet.Length)];
                string s = new(line);
                Assert.Equal(rx.IsMatch(s), matcher.Matches(s));
            }
        }
    }

    /// <summary>A pattern the rewriter cannot take is still answered by the engine, so nothing is lost by
    /// having a fast path at all.</summary>
    [Fact]
    public void A_pattern_the_rewriter_refuses_still_matches()
    {
        var matcher = FindEngine.CompileQuery(new FindQuery(@"(alpha|beta)\d+", Regex: true, CaseSensitive: false))!;
        Assert.True(matcher.Matches("says BETA42 here"));
        Assert.False(matcher.Matches("says gamma42 here"));
    }
}
