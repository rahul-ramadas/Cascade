using System.Diagnostics;
using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>
/// Writing the rows on show out to a file. It is minutes of reading and writing on a large log, so it runs
/// off the thread that draws - which means it has to keep reading the file it started on, and has to leave
/// the chosen file alone when it is stopped part-way.
/// </summary>
public class SaveRowsTests
{
    private static void WaitFilter(CascadeDocument doc, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException("Filtering did not become idle in time.");
    }

    private static string Log(int count, Func<int, string> line)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(line(i)).Append('\n');
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Target() =>
        Path.Combine(Path.GetTempPath(), "cascade_save_" + Guid.NewGuid().ToString("N") + ".txt");

    /// <summary>Records on the thread that reports. <see cref="Progress{T}"/> hands its callbacks to the
    /// pool when there is no context to post to, so it can deliver them out of order - which would make
    /// "the fraction never goes backwards" a test of the plumbing rather than of the export.</summary>
    private sealed class Recorder : IProgress<double>
    {
        public readonly List<double> Seen = new();
        private readonly Action<double>? _also;
        public Recorder(Action<double>? also = null) => _also = also;
        public void Report(double value) { lock (Seen) Seen.Add(value); _also?.Invoke(value); }
    }

    [Fact]
    public async Task Every_line_is_written_when_nothing_is_filtered()
    {
        string log = Log(500, i => $"line {i}");
        string outPath = Target();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(log);
            doc.WaitForIndex();

            await doc.SaveRowsAsync(outPath, null, CancellationToken.None);

            string[] written = File.ReadAllLines(outPath);
            Assert.Equal(500, written.Length);
            Assert.Equal("line 0", written[0]);
            Assert.Equal("line 499", written[499]);
        }
        finally { File.Delete(log); File.Delete(outPath); }
    }

    [Fact]
    public async Task Only_the_rows_on_show_are_written()
    {
        // The point of the export: what lands in the file is what the filters left on screen, in order.
        string log = Log(300, i => i % 3 == 0 ? $"keep {i}" : $"drop {i}");
        string outPath = Target();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(log);
            doc.WaitForIndex();
            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            filters.Add(new Filter { Enabled = true, Match = { Text = "keep" } });
            doc.SetFilters(filters);
            WaitFilter(doc);

            await doc.SaveRowsAsync(outPath, null, CancellationToken.None);

            string[] written = File.ReadAllLines(outPath);
            Assert.Equal(100, written.Length);
            Assert.Equal("keep 0", written[0]);
            Assert.Equal("keep 297", written[99]);
            Assert.DoesNotContain(written, w => w.StartsWith("drop", StringComparison.Ordinal));
        }
        finally { File.Delete(log); File.Delete(outPath); }
    }

    [Fact]
    public async Task Stopping_part_way_leaves_the_chosen_file_exactly_as_it_was()
    {
        // Whatever was there before is the user's, and half an export is worse than none: it looks like a
        // finished file. Cancelling has to put nothing in its place.
        string log = Log(200_000, i => $"line {i} with enough text on it to take a moment to write out");
        string outPath = Target();
        File.WriteAllText(outPath, "the file that was already there");
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(log);
            doc.WaitForIndex();

            using var cts = new CancellationTokenSource();
            var progress = new Recorder(_ => cts.Cancel());   // stop it the moment it starts
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => doc.SaveRowsAsync(outPath, progress, cts.Token));

            Assert.Equal("the file that was already there", File.ReadAllText(outPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(outPath)!,
                                            Path.GetFileName(outPath) + "*.tmp"));
        }
        finally { File.Delete(log); File.Delete(outPath); }
    }

    [Fact]
    public async Task The_writing_is_not_done_before_the_call_returns()
    {
        // The whole point. If the reading and writing happened inline, the window would stop answering for
        // as long as it took.
        string log = Log(200_000, i => $"line {i} with enough text on it that this cannot finish instantly");
        string outPath = Target();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(log);
            doc.WaitForIndex();

            var task = doc.SaveRowsAsync(outPath, null, CancellationToken.None);
            bool returnedEarly = !task.IsCompleted;
            await task;

            Assert.True(returnedEarly, "the export had already finished by the time the call returned, "
                                       + "so it ran on the thread that asked for it");
            Assert.Equal(200_000, File.ReadAllLines(outPath).Length);
        }
        finally { File.Delete(log); File.Delete(outPath); }
    }

    [Fact]
    public async Task An_export_keeps_reading_the_file_it_started_on()
    {
        // It holds the mapping open, and it must not start answering from whatever is opened next - which
        // is what reading the document's fields again part-way through would do.
        string a = Log(30_000, i => $"alpha {i}");
        string b = Log(10, i => $"bravo {i}");
        string outPath = Target();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(a);
            doc.WaitForIndex();

            var task = doc.SaveRowsAsync(outPath, null, CancellationToken.None);
            doc.Open(b);                       // swap the file out from under it
            doc.WaitForIndex();
            await task;

            string[] written = File.ReadAllLines(outPath);
            Assert.Equal(30_000, written.Length);
            Assert.Equal("alpha 0", written[0]);
            Assert.Equal("alpha 29999", written[29_999]);
        }
        finally { File.Delete(a); File.Delete(b); File.Delete(outPath); }
    }

    [Fact]
    public async Task How_far_it_has_got_is_reported_and_ends_at_the_end()
    {
        string log = Log(50_000, i => $"line {i}");
        string outPath = Target();
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(log);
            doc.WaitForIndex();

            var progress = new Recorder();
            await doc.SaveRowsAsync(outPath, progress, CancellationToken.None);

            lock (progress.Seen)
            {
                Assert.NotEmpty(progress.Seen);
                Assert.All(progress.Seen, f => Assert.InRange(f, 0, 1));
                for (int i = 1; i < progress.Seen.Count; i++)
                    Assert.True(progress.Seen[i] >= progress.Seen[i - 1],
                                $"progress went backwards: {progress.Seen[i - 1]} -> {progress.Seen[i]}");
            }
        }
        finally { File.Delete(log); File.Delete(outPath); }
    }
}
