using System.Globalization;
using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Model;
using Cascade.Core.Timing;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>Times as the window asks about them: read off a real file, through the filters, and with the
/// reader's own template overriding what could be guessed.</summary>
public class DocumentTimeTests
{
    private static readonly DateTime Start = new(2026, 8, 5, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A log where line <c>i</c> is written at <c>i</c> seconds, so every expected answer is
    /// arithmetic on the line number rather than a figure copied out of the implementation.</summary>
    private static string WriteLog(int count, string service = "api", Func<int, string>? line = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            var at = Start.AddSeconds(i);
            sb.Append(line?.Invoke(i)
                      ?? $"[{at.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}][{service}] request {i}")
              .Append('\n');
        }
        return Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Wait(CascadeDocument doc)
    {
        doc.WaitForIndex();
        for (int i = 0; i < 2000 && !doc.IsFilterIdle; i++) Thread.Sleep(2);
    }

    [Fact]
    public void A_log_with_a_stamp_at_the_front_is_read_without_being_told_anything()
    {
        string path = WriteLog(600);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);

            Assert.NotNull(doc.Clock);
            Assert.False(doc.TimeFieldIsSet, "nobody named the field - this one was found");
            Assert.Equal(Start.Ticks, doc.TimeOf(0));
            Assert.Equal(Start.AddSeconds(41).Ticks, doc.TimeOf(41));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_log_with_no_times_in_it_simply_has_no_clock()
    {
        string path = WriteLog(600, line: i => $"request {i} handled by worker {i % 8}");
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);
            Assert.Null(doc.Clock);
            Assert.Null(doc.TimeOf(3));
            Assert.False(doc.TryElapsedBefore(3, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Elapsed_is_measured_from_the_line_above_when_every_line_is_showing()
    {
        string path = WriteLog(600);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);

            Assert.True(doc.TryElapsedBefore(9, out long elapsed));
            Assert.Equal(TimeSpan.FromSeconds(1).Ticks, elapsed);
            Assert.False(doc.TryElapsedBefore(0, out _), "the first line has nothing before it");
        }
        finally { File.Delete(path); }
    }

    /// <summary>The whole point of the column. With the noise filtered away it measures between one
    /// interesting line and the next, which is a latency profile of whatever the filters select - and is
    /// not obtainable any other way.</summary>
    [Fact]
    public void Elapsed_is_measured_from_the_previous_SHOWN_line_once_filters_are_hiding_the_rest()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 600; i++)
        {
            var at = Start.AddSeconds(i);
            string service = i % 10 == 0 ? "payment-svc" : "api-gateway";
            sb.Append($"[{at.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}][{service}] request {i}\n");
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);

            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            filters.Add(new Filter { Match = new FilterMatch { Text = "payment-svc" }, Enabled = true });
            doc.SetFilters(filters);
            Wait(doc);

            // Every tenth line shows, and the log ticks a second a line, so consecutive shown lines are ten
            // seconds apart however many lines were dropped between them.
            Assert.True(doc.TryElapsedBefore(20, out long elapsed));
            Assert.Equal(TimeSpan.FromSeconds(10).Ticks, elapsed);

            // The same two lines, unfiltered, are one second apart - the number differs by an order of
            // magnitude between the two modes, which is exactly why it says which it is measuring.
            filters.ShowOnlyFilteredLines = false;
            doc.ApplyFilters();
            Wait(doc);
            Assert.True(doc.TryElapsedBefore(20, out long unfiltered));
            Assert.Equal(TimeSpan.FromSeconds(1).Ticks, unfiltered);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A stack trace carries no time. The line after it is measured from the last line that did,
    /// so a wrapped exception does not leave a hole in the column.</summary>
    [Fact]
    public void Lines_carrying_no_time_are_stepped_over()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 600; i++)
        {
            var at = Start.AddSeconds(i);
            sb.Append($"[{at.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}][api] request {i}\n");
            if (i % 10 == 0) sb.Append("    at Contoso.Service.Handle()\n    at Contoso.Host.Run()\n");
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);

            long line = 3;                       // "request 1", two lines past the first trace
            Assert.Null(doc.TimeOf(1));
            Assert.True(doc.TryElapsedBefore(line, out long elapsed));
            Assert.Equal(TimeSpan.FromSeconds(1).Ticks, elapsed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_span_covers_the_stretch_between_two_lines()
    {
        string path = WriteLog(600);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);

            Assert.True(doc.TrySpan(10, 90, out long span));
            Assert.Equal(TimeSpan.FromSeconds(80).Ticks, span);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A drag that ends on a stack trace is the ordinary case, so an end carrying no time is walked
    /// INWARD to the nearest line that does. The stretch measured is then a little shorter than the stretch
    /// selected, which is the honest answer and the only one available.</summary>
    [Fact]
    public void A_span_whose_ends_carry_no_time_is_measured_from_the_nearest_lines_that_do()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 600; i++)
        {
            // Every third line is a continuation with no stamp, so both ends of any range can miss.
            sb.Append(i % 3 == 0
                ? "    at Contoso.Service.Handle()"
                : $"[{Start.AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}][api] request {i}")
              .Append('\n');
        }
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);

            Assert.Null(doc.TimeOf(9));
            Assert.Null(doc.TimeOf(90));
            Assert.True(doc.TrySpan(9, 90, out long span));
            // Walked in to lines 10 and 89, which the log wrote 79 seconds apart.
            Assert.Equal(TimeSpan.FromSeconds(79).Ticks, span);
        }
        finally { File.Delete(path); }
    }

    /// <summary>What the reader named beats what could be guessed, and it is read whether or not the fields
    /// are being DRAWN - nobody should have to turn column mode on to measure a gap.</summary>
    [Fact]
    public void A_named_field_wins_over_the_guess_and_needs_no_columns()
    {
        // Two stamps a line: the guess takes the one at the front, and the reader wants the other.
        string path = WriteLog(600, line: i =>
            $"[{Start.AddSeconds(i).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}]"
            + $"[{Start.AddSeconds(i * 60).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] request {i}");
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            Wait(doc);

            Assert.Equal(Start.AddSeconds(5).TimeOfDay.Ticks, doc.TimeOf(5));

            doc.Columns.Enabled = false;
            doc.Columns.Template = "{[*]}{[*]}{*}";
            doc.Columns.Reset();
            doc.Columns.TimePart = 1;
            doc.Columns.TimeFormat = "HH:mm:ss.fff";

            Assert.True(doc.TimeFieldIsSet);
            Assert.Equal(Start.AddSeconds(5 * 60).TimeOfDay.Ticks, doc.TimeOf(5));
            Assert.True(doc.TryElapsedBefore(5, out long elapsed));
            Assert.Equal(TimeSpan.FromMinutes(1).Ticks, elapsed);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A clock belongs to the file it was read from. Opening another must start over, or the
    /// second log is measured with the first one's reader.</summary>
    [Fact]
    public void Opening_another_file_forgets_the_clock_that_was_found_for_the_last_one()
    {
        string timed = WriteLog(600);
        string untimed = WriteLog(600, line: i => $"request {i} handled");
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(timed);
            Wait(doc);
            Assert.NotNull(doc.Clock);

            doc.Open(untimed);
            Wait(doc);
            Assert.Null(doc.Clock);
        }
        finally { File.Delete(timed); File.Delete(untimed); }
    }

    /// <summary>Detection reads the head of the file so that it can answer within a moment of opening,
    /// rather than waiting on the index of a log that takes seconds to walk - and it must not keep re-reading
    /// it while the rest of the index arrives, because the elapsed column asks once a row per frame.</summary>
    [Fact]
    public void The_clock_is_found_before_the_whole_file_has_been_indexed()
    {
        string path = WriteLog(200_000);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            for (int i = 0; i < 2000 && doc.CompletedLineCount < 600; i++) Thread.Sleep(1);
            Assert.NotNull(doc.Clock);

            // Once it has read as much of the head as it ever will, asking again costs nothing at all.
            // Measured on THIS thread: the document's own background threads are still alive and their
            // allocations would drown a per-call figure taken across the process.
            doc.WaitForIndex();
            _ = doc.Clock;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) _ = doc.Clock;
            long each = (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
            Assert.True(each == 0, $"asking for the clock allocated {each} bytes a time");
        }
        finally { File.Delete(path); }
    }
}
