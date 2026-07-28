using Cascade.Core.IO;

namespace Cascade.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cascade_atomic_" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Writes_the_content_and_leaves_no_temporary_file_behind()
    {
        string file = Path_("settings.json");
        AtomicFile.WriteAllText(file, "first");
        AtomicFile.WriteAllText(file, "second");

        Assert.Equal("second", File.ReadAllText(file));
        Assert.Equal(new[] { "settings.json" },
                     Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_write_that_cannot_be_swapped_in_leaves_the_original_untouched()
    {
        // The point of the temp-then-rename: whatever goes wrong, the file that was already there is still
        // the file that is there, whole. A reader holding it exclusively is enough to block the swap.
        string file = Path_("settings.json");
        AtomicFile.WriteAllText(file, "the good content");

        using (var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.NotNull(Record.Exception(() => AtomicFile.WriteAllText(file, "the replacement")));

        Assert.Equal("the good content", File.ReadAllText(file));
        Assert.Equal(new[] { "settings.json" },
                     Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_reader_never_sees_a_half_written_file()
    {
        // What a plain WriteAllText gets wrong: it truncates first, so anyone reading during the write - or
        // any process killed during it - is left with a prefix. Rewrite the same file repeatedly while
        // reading it, and every read must return one of the two whole versions.
        string file = Path_("filters.cascade");
        string small = new('a', 200_000);
        string large = new('b', 400_000);
        AtomicFile.WriteAllText(file, small);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int writes = 0;
        var writer = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // A reader with the file open denies the swap; that is a failed write, not a torn one.
                try
                {
                    AtomicFile.WriteAllText(file, large);
                    AtomicFile.WriteAllText(file, small);
                    writes += 2;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        });
        writer.Start();

        int reads = 0;
        var bad = new List<int>();
        while (!stop.IsCancellationRequested)
        {
            string seen;
            try { seen = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            reads++;
            if (seen != small && seen != large) bad.Add(seen.Length);
        }
        writer.Join();

        Assert.True(reads > 0, "the reader never managed to open the file");
        Assert.True(writes > 0, "the writer never managed to swap a file in");
        Assert.Empty(bad);
    }
}
