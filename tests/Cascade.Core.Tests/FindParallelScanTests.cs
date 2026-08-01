using Cascade.Core.Document;
using Cascade.Core.Find;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>The sweep scans blocks across every core. Splitting work is exactly the change that can quietly
/// drop or double a line at a boundary, so what it finds is compared against the file it was generated
/// from - every match, in order, plus the totals.</summary>
public class FindParallelScanTests
{
    /// <summary>Big enough to cross the threshold where the scan goes parallel, and irregular enough that a
    /// boundary error cannot land neatly. Every 7th line matches once; every 91st matches three times.</summary>
    private const int Lines = 200_000;

    private static bool IsMatch(int line) => line % 7 == 0;
    private static int Occurrences(int line) => line % 7 != 0 ? 0 : line % 91 == 0 ? 3 : 1;

    private static string WriteFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_findpar_" + Guid.NewGuid().ToString("N") + ".log");
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        for (int i = 0; i < Lines; i++)
        {
            writer.Write("line ");
            writer.Write(i);
            for (int n = 0; n < Occurrences(i); n++) writer.Write(" NEEDLE");
            writer.Write('\n');
        }
        return path;
    }

    [Fact]
    public async Task A_parallel_sweep_finds_exactly_what_the_file_contains()
    {
        string path = WriteFile();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            var query = new FindQuery("NEEDLE", false, false);
            var expected = Enumerable.Range(0, Lines).Where(IsMatch).Select(i => (long)i).ToArray();

            // Walk every match forwards. Each answer has to be the next one in the file - not merely a
            // matching line, which a scan that skipped a block could still produce.
            long at = 0;
            var walked = new List<long>();
            while (true)
            {
                long found = await doc.FindNextAsync(query, at, forward: true, CancellationToken.None);
                if (found < 0) break;
                walked.Add(found);
                at = found + 1;
            }
            Assert.Equal(expected, walked);

            // ...and backwards, which is the other sweep.
            var back = new List<long>();
            long from = Lines - 1;
            while (true)
            {
                long found = await doc.FindNextAsync(query, from, forward: false, CancellationToken.None);
                if (found < 0) break;
                back.Add(found);
                from = found - 1;
                if (from < 0) break;
            }
            Assert.Equal(expected.Reverse(), back);

            var tally = doc.FindTally(-1);
            Assert.NotNull(tally);
            Assert.True(tally!.Value.Complete);
            Assert.Equal(expected.Length, tally.Value.VisibleLines);
            Assert.Equal(0, tally.Value.HiddenLines);
            Assert.Equal(Enumerable.Range(0, Lines).Sum(Occurrences), tally.Value.Occurrences);
            Assert.False(tally.Value.Approximate);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_term_that_is_not_there_is_reported_as_absent_rather_than_missed()
    {
        // The failure a parallel scan makes easy is "no more matches" arriving before the whole file has
        // been examined, so this asks for one that genuinely is not there.
        string path = WriteFile();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            long found = await doc.FindNextAsync(new FindQuery("HAYSTACK", false, false), 0, true, CancellationToken.None);
            Assert.Equal(-1, found);

            var tally = doc.FindTally(-1);
            Assert.True(tally!.Value.Complete);
            Assert.Equal(0, tally.Value.VisibleLines);
        }
        finally { File.Delete(path); }
    }
}
