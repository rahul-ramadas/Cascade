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
}
