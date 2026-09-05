using System.Globalization;
using Cascade.Core.Timing;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>
/// Whether a log's own clock can be found without being told where it is.
///
/// <para>The positives are one file per format that logs are really written in. The negatives matter more:
/// they are the lines that LOOK like timestamps - addresses, versions, identifiers, counters - and every
/// one of them has to come back with nothing, because a wrong clock is far worse than no clock.</para>
/// </summary>
public class ClockDetectorTests
{
    private static readonly DateTime Start = new(2026, 8, 5, 5, 0, 2, 123, DateTimeKind.Utc);

    /// <summary>A log of <paramref name="count"/> lines, each written by <paramref name="write"/> from a
    /// moment that steps forward by a plausible, uneven amount.</summary>
    private static List<string> Log(Func<DateTime, int, string> write, int count = 60)
    {
        var lines = new List<string>(count);
        var at = Start;
        for (int i = 0; i < count; i++)
        {
            lines.Add(write(at, i));
            at = at.AddMilliseconds(7 + i % 13 * 31);
        }
        return lines;
    }

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static TheoryData<string, Func<DateTime, int, string>, string> RealFormats() => new()
    {
        { "bracketed ISO", (t, i) => $"[{t:yyyy-MM-ddTHH:mm:ss.fff}][api-gateway][INFO] request {i}", "yyyy-MM-dd'T'HH:mm:ss.fff" },
        { "plain ISO", (t, i) => $"{Iso(t)} INFO request {i}", "yyyy-MM-dd HH:mm:ss.fff" },
        { "log4j comma", (t, i) => $"{t:yyyy-MM-dd HH:mm:ss,fff} [main] INFO Service - request {i}", "yyyy-MM-dd HH:mm:ss,fff" },
        { "time only", (t, i) => $"{t:HH:mm:ss.fff} INFO request {i}", "HH:mm:ss.fff" },
        { "time only, no fraction", (t, i) => $"{t:HH:mm:ss} INFO request {i}", "HH:mm:ss" },
        { "syslog", (t, i) => $"{t:MMM} {t.Day,2} {t:HH:mm:ss} host daemon[4711]: request {i}", "MMM d HH:mm:ss" },
        { "go", (t, i) => $"{t:yyyy/MM/dd HH:mm:ss} request {i}", "yyyy/MM/dd HH:mm:ss" },
        { "US date", (t, i) => $"{t:MM/dd/yyyy HH:mm:ss.fff} request {i}", "MM/dd/yyyy HH:mm:ss.fff" },
        { "ETW precision", (t, i) => $"[{t:yyyy-MM-ddTHH:mm:ss.fffffff}][bus][INFO] request {i}", "yyyy-MM-dd'T'HH:mm:ss.fffffff" },
        { "zoned ISO", (t, i) => $"{t:yyyy-MM-ddTHH:mm:ss.fff}Z INFO request {i}", "yyyy-MM-dd'T'HH:mm:ss.fffK" },
        { "epoch millis", (t, i) => $"{(long)(t - DateTime.UnixEpoch).TotalMilliseconds} INFO request {i}", "epoch:ms" },
        { "epoch seconds", (t, i) => $"{(long)(t - DateTime.UnixEpoch).TotalSeconds} INFO request {i}", "epoch:s" },
        { "angle brackets", (t, i) => $"<{Iso(t)}> request {i}", "yyyy-MM-dd HH:mm:ss.fff" }
    };

    [Theory]
    [MemberData(nameof(RealFormats))]
    public void Finds_the_clock_in_a_log_of_each_shape(string what, Func<DateTime, int, string> write, string format)
    {
        var lines = Log(write);
        var clock = ClockDetector.Detect(lines);
        Assert.True(clock is not null, $"{what}: nothing found in e.g. {lines[0]}");
        Assert.Equal(format, clock!.Format.Source);

        // Found is not the same as right: the times it reads have to be the times the log was written at.
        Assert.True(clock.TryRead(lines[0], out long first));
        Assert.True(clock.TryRead(lines[^1], out long last));
        Assert.True(last > first, $"{what}: the clock does not run forwards");
    }

    public static TheoryData<string, Func<int, string>> NotClocks() => new()
    {
        { "an access log", i => $"10.0.{i / 250}.{i % 250} - - [12/Oct/2026:14:02:31 +0000] \"GET /a\" 200 4523" },
        { "a build log", i => $"1.8.{400 + i} restoring Contoso.Core" },
        { "identifiers", i => $"{Guid.NewGuid()} handled request" },
        { "numbers", i => $"{i},{i * 7},{i * 13},{i * 29}" },
        { "prose", i => $"Everything is fine and nothing at all is being logged here, line {i}" },
        { "a level first", i => $"INFO request {i} handled" },
        { "sizes", i => $"{i * 1024} bytes written to disk" },
        { "an ascending counter", i => $"{100000 + i} request handled" }
    };

    [Theory]
    [MemberData(nameof(NotClocks))]
    public void Finds_nothing_where_there_is_nothing_to_find(string what, Func<int, string> write)
    {
        var lines = Enumerable.Range(0, 60).Select(write).ToList();
        var clock = ClockDetector.Detect(lines);
        Assert.True(clock is null, $"{what}: read \"{lines[0]}\" as {clock?.Format.Source}");
    }

    /// <summary>Which of the first two numbers is the day cannot be told from one line. Across the sample
    /// it can, the moment any of them passes the twelfth.</summary>
    [Fact]
    public void A_day_past_the_twelfth_says_which_way_round_the_date_is()
    {
        var lines = Log((t, i) => $"{t.AddDays(20):dd/MM/yyyy HH:mm:ss} request {i}");
        var clock = ClockDetector.Detect(lines);
        Assert.NotNull(clock);
        Assert.Equal("dd/MM/yyyy HH:mm:ss", clock!.Format.Source);
    }

    /// <summary>Stack traces, banners and continuation lines carry no time, and a log full of them is still
    /// a log with a clock in it.</summary>
    [Theory]
    [InlineData(3, true)]
    [InlineData(30, true)]
    [InlineData(70, false)]
    public void Lines_with_no_time_are_tolerated_until_there_are_too_many(int percentWithout, bool found)
    {
        var lines = new List<string>();
        var at = Start;
        for (int i = 0; i < 200; i++)
        {
            if (i % 100 < percentWithout) lines.Add($"    at Contoso.Service.Handle(request {i})");
            else { lines.Add($"{Iso(at)} INFO request {i}"); at = at.AddMilliseconds(11); }
        }
        Assert.Equal(found, ClockDetector.Detect(lines) is not null);
    }

    /// <summary>A log written by several threads at once really does arrive out of order, and the reader
    /// wants elapsed times on it more than most. So the gate has to tolerate the real rate - measured on a
    /// concurrent service log at around one line in twelve - while staying miles clear of the half a field
    /// of unrelated numbers would manage.</summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(8, true)]
    [InlineData(45, false)]
    public void Lines_out_of_order_are_tolerated_until_the_field_stops_looking_like_a_clock(int percent, bool found)
    {
        var rng = new Random(20260805);
        var lines = new List<string>();
        var at = Start;
        for (int i = 0; i < 400; i++)
        {
            at = at.AddMilliseconds(37);
            var written = rng.Next(100) < percent ? at.AddMilliseconds(-rng.Next(50, 400)) : at;
            lines.Add($"{Iso(written)} INFO request {i}");
        }
        Assert.Equal(found, ClockDetector.Detect(lines) is not null);
    }

    /// <summary>Times shuffled into no order at all are not a clock, whatever they look like line by line.
    /// This is the gate's whole job, and the case above is what decides where it sits.</summary>
    [Fact]
    public void A_field_in_no_order_at_all_is_not_a_clock()
    {
        var rng = new Random(7);
        var moments = Enumerable.Range(0, 400).Select(i => Start.AddMilliseconds(i * 37)).OrderBy(_ => rng.Next()).ToList();
        var lines = moments.Select((m, i) => $"{Iso(m)} INFO request {i}").ToList();
        Assert.Null(ClockDetector.Detect(lines));
    }

    [Fact]
    public void A_field_that_runs_backwards_is_not_a_clock()
    {
        var lines = Log((t, i) => $"{Iso(t)} INFO request {i}", 60);
        lines.Reverse();
        Assert.Null(ClockDetector.Detect(lines));
    }

    /// <summary>A count of milliseconds is only a moment when it names one a log could have been written
    /// at. Without that window any long ascending identifier would pass.</summary>
    [Fact]
    public void A_counter_that_is_not_a_plausible_moment_is_not_an_epoch()
    {
        var lines = Enumerable.Range(0, 60).Select(i => $"{1000000000000L + i * 37} handled request {i}").ToList();
        Assert.Null(ClockDetector.Detect(lines));
    }

    /// <summary>A banner at the top of a file is a few lines out of hundreds, so it cannot spoil a reading
    /// - which is what lets detection run off the head of the file instead of waiting for the whole index.
    /// </summary>
    [Fact]
    public void A_banner_at_the_top_of_the_file_does_not_spoil_the_reading()
    {
        var lines = new List<string>
        {
            "Contoso Trace Tool 4.2.1", "Copyright (c) 2026", "Machine: BUILD-07", "", "----------------"
        };
        lines.AddRange(Log((t, i) => $"{Iso(t)} INFO request {i}", 200));
        var clock = ClockDetector.Detect(lines);
        Assert.NotNull(clock);
        Assert.Equal("yyyy-MM-dd HH:mm:ss.fff", clock!.Format.Source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Too_little_of_a_log_to_judge_it_by_is_left_alone(int count)
        => Assert.Null(ClockDetector.Detect(Log((t, i) => $"{Iso(t)} INFO request {i}", count)));

    /// <summary>A log that crosses midnight and never says what day it is still runs forwards.</summary>
    [Fact]
    public void A_time_only_log_may_cross_midnight()
    {
        var lines = new List<string>();
        var at = new DateTime(2026, 8, 5, 23, 59, 40, DateTimeKind.Utc);
        for (int i = 0; i < 60; i++) { lines.Add($"{at:HH:mm:ss.fff} INFO request {i}"); at = at.AddSeconds(1); }
        var clock = ClockDetector.Detect(lines);
        Assert.NotNull(clock);
        Assert.True(clock!.WrapsAtMidnight());
    }
}

internal static class ClockAssertions
{
    public static bool WrapsAtMidnight(this LogClock clock) => clock.Format.WrapsAtMidnight;
}
