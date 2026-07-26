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
}
