using System.Globalization;
using Cascade.Core.Columns;
using Cascade.Core.Timing;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>Reading a timestamp out of a line: the format language, the shape scanner that proposes one,
/// and the arithmetic on what they produce.</summary>
public class ClockFormatTests
{
    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff", "2026-08-05 14:02:31.884", "2026-08-05T14:02:31.8840000")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.fffffff", "2026-08-05T14:02:31.8841234", "2026-08-05T14:02:31.8841234")]
    [InlineData("yyyy/MM/dd HH:mm:ss", "2026/08/05 14:02:31", "2026-08-05T14:02:31.0000000")]
    [InlineData("HH:mm:ss.fff", "14:02:31.884", "0001-01-01T14:02:31.8840000")]
    [InlineData("HH:mm:ss", "14:02:31", "0001-01-01T14:02:31.0000000")]
    [InlineData("MM/dd/yyyy HH:mm:ss.fff", "08/05/2026 14:02:31.884", "2026-08-05T14:02:31.8840000")]
    [InlineData("dd/MM/yyyy HH:mm:ss", "05/08/2026 14:02:31", "2026-08-05T14:02:31.0000000")]
    public void Reads_the_shapes_logs_are_actually_written_in(string format, string text, string expected)
    {
        var clock = ClockFormat.Compile(format);
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead(text, out long ticks), $"{format} did not read {text}");
        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture), new DateTime(ticks));
    }

    [Fact]
    public void A_comma_is_a_decimal_point_where_a_log_writes_one()
    {
        var clock = ClockFormat.Compile("yyyy-MM-dd HH:mm:ss,fff");
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead("2026-08-05 14:02:31,884", out long ticks));
        Assert.Equal(884, new DateTime(ticks).Millisecond);
    }

    /// <summary>Logs trim a trailing zero fraction constantly, so one format has to read both. Without the
    /// second pattern the line without a fraction would carry no time at all.</summary>
    [Fact]
    public void A_stamp_that_dropped_its_fraction_is_still_read()
    {
        var clock = ClockFormat.Compile("HH:mm:ss.fff");
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead("14:02:31.884", out long with));
        Assert.True(clock.TryRead("14:02:31", out long without));
        Assert.Equal(884, new DateTime(with).Millisecond);
        Assert.Equal(0, new DateTime(without).Millisecond);
    }

    /// <summary>In a custom format string the separators and the month names both belong to a CULTURE. Read
    /// under the machine's own, <c>MMM</c> wants "août" rather than "Aug" and a syslog written in English
    /// stops being readable on a French machine - so the parse is pinned to invariant, and this says so.
    /// </summary>
    [Fact]
    public void Times_are_read_the_same_whatever_the_machine_is_set_to()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            var syslog = ClockFormat.Compile("MMM d HH:mm:ss");
            Assert.NotNull(syslog);
            Assert.True(syslog!.TryRead("Aug  5 14:02:31", out long ticks), "an English month name was refused");
            Assert.Equal(8, new DateTime(ticks).Month);

            var iso = ClockFormat.Compile("yyyy-MM-dd HH:mm:ss.fff");
            Assert.NotNull(iso);
            Assert.True(iso!.TryRead("2026-08-05 14:02:31.884", out _));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Theory]
    [InlineData("epoch:s", "", "2026-08-05T14:02:31.0000000")]
    [InlineData("epoch:ms", "", "2026-08-05T14:02:31.8840000")]
    [InlineData("epoch:us", "", "2026-08-05T14:02:31.8841230")]
    [InlineData("epoch:ns", "", "2026-08-05T14:02:31.8841234")]
    public void Counts_since_the_epoch_are_a_format_of_their_own(string format, string _, string expected)
    {
        var moment = DateTime.SpecifyKind(DateTime.Parse(expected, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        long since = moment.Ticks - DateTime.UnixEpoch.Ticks;
        // The count the log would have written, worked out from the moment rather than typed in - a hand
        // -copied epoch figure proves only that two mistakes agree with each other.
        string text = format switch
        {
            "epoch:s" => (since / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture),
            "epoch:ms" => (since / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture),
            "epoch:us" => (since / 10).ToString(CultureInfo.InvariantCulture),
            _ => (since * 100).ToString(CultureInfo.InvariantCulture)
        };

        var clock = ClockFormat.Compile(format);
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead(text, out long ticks), $"{format} did not read {text}");
        Assert.Equal(moment, new DateTime(ticks, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("elapsed:s", "12.345678", 123456780L)]
    [InlineData("elapsed:s", "12", 120000000L)]
    [InlineData("elapsed:ms", "1250", 12500000L)]
    [InlineData("elapsed:us", "1500000", 15000000L)]
    [InlineData("elapsed:ns", "1500000000", 15000000L)]    public void Uptime_stamps_are_read_as_the_durations_they_already_are(string format, string text, long ticks)
    {
        var clock = ClockFormat.Compile(format);
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead(text, out long read));
        Assert.Equal(ticks, read);
    }

    /// <summary>Integer arithmetic throughout: a double cannot hold a nanosecond epoch to the digit, and
    /// the last digits are exactly what someone reading such a log came for.</summary>
    [Fact]
    public void A_nanosecond_epoch_keeps_every_digit_a_tick_can_hold()
    {
        var clock = ClockFormat.Compile("epoch:ns");
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead("1754402551884123456", out long a));
        Assert.True(clock.TryRead("1754402551884123556", out long b));
        Assert.Equal(1, b - a);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a format at all")]
    [InlineData("epoch:")]
    [InlineData("epoch:fortnights")]
    [InlineData("elapsed:weeks")]
    public void What_cannot_be_read_is_refused_rather_than_half_accepted(string format)
        => Assert.Null(ClockFormat.Compile(format));

    /// <summary>A format string of one character is read as a STANDARD specifier by the framework, which is
    /// never what someone typing "H" meant.</summary>
    [Fact]
    public void A_single_letter_is_taken_as_the_custom_format_it_looks_like()
    {
        var clock = ClockFormat.Compile("H");
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead("14", out long ticks));
        Assert.Equal(14, new DateTime(ticks).Hour);
    }

    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff", false)]
    [InlineData("HH:mm:ss.fff", true)]
    [InlineData("HH:mm:ss", true)]
    [InlineData("MMM d HH:mm:ss", false)]
    [InlineData("epoch:ms", false)]
    public void A_stamp_with_no_date_is_the_only_one_that_can_come_round_again(string format, bool wraps)
        => Assert.Equal(wraps, ClockFormat.Compile(format)!.WrapsAtMidnight);

    /// <summary>An escaped letter is text, not a specifier - so a template whose literal happens to be 'd'
    /// must not read as a day and turn a time-only stamp into a dated one.</summary>
    [Fact]
    public void An_escaped_letter_is_literal_text()
    {
        var clock = ClockFormat.Compile(@"HH:mm:ss\d");
        Assert.NotNull(clock);
        Assert.True(clock!.WrapsAtMidnight);
        Assert.True(clock.TryRead("14:02:31d", out _));
    }

    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff", 3)]
    [InlineData("yyyy-MM-dd HH:mm:ss.fffffff", 7)]
    [InlineData("HH:mm:ss", 0)]
    [InlineData("epoch:us", 6)]
    public void The_precision_reported_is_the_log_s_own(string format, int digits)
        => Assert.Equal(digits, ClockFormat.Compile(format)!.FractionDigits);
}

public class ClockShapeTests
{
    [Theory]
    [InlineData("2026-08-05 14:02:31.884 hello", 0, "yyyy-MM-dd HH:mm:ss.fff", 23)]
    [InlineData("2026-08-05T14:02:31.8841234Z rest", 0, "yyyy-MM-dd'T'HH:mm:ss.fffffffK", 28)]
    [InlineData("2026-08-05T14:02:31+05:30 rest", 0, "yyyy-MM-dd'T'HH:mm:ssK", 25)]
    [InlineData("2026/08/05 14:02:31 x", 0, "yyyy/MM/dd HH:mm:ss", 19)]
    [InlineData("14:02:31.884 x", 0, "HH:mm:ss.fff", 12)]
    [InlineData("14:02:31 x", 0, "HH:mm:ss", 8)]
    [InlineData("08/05/2026 14:02:31 x", 0, "MM/dd/yyyy HH:mm:ss", 19)]
    [InlineData("Aug  5 14:02:31 host", 0, "MMM d HH:mm:ss", 15)]
    [InlineData("1754402551884 x", 0, "epoch:ms", 13)]
    [InlineData("[2026-08-05 14:02:31] x", 1, "yyyy-MM-dd HH:mm:ss", 19)]
    [InlineData("2026-08-05 14:02:31,884 x", 0, "yyyy-MM-dd HH:mm:ss,fff", 23)]
    public void Reads_the_shape_a_real_log_is_written_in(string line, int at, string format, int length)
    {
        Assert.True(ClockShape.TryScan(line, at, out int read, out string found), line);
        Assert.Equal(format, found);
        Assert.Equal(length, read);
        Assert.True(ClockFormat.Compile(found)!.TryRead(line.AsSpan(at, read), out _),
                    $"{found} could not read back {line[at..(at + read)]}");
    }

    /// <summary>A stamp written to nanoseconds is read to the seven digits a tick count holds, and stops
    /// there rather than guessing at what follows.</summary>
    [Fact]
    public void More_precision_than_a_tick_holds_is_read_as_far_as_it_goes()
    {
        Assert.True(ClockShape.TryScan("2026-08-05T14:02:31.123456789Z x", 0, out int length, out string format));
        Assert.Equal("yyyy-MM-dd'T'HH:mm:ss.fffffff", format);
        Assert.Equal(27, length);
        Assert.True(ClockFormat.Compile(format)!.TryRead("2026-08-05T14:02:31.123456789".AsSpan(0, length), out long ticks));
        Assert.Equal(1234567, ticks % 10_000_000);
    }

    [Theory]
    [InlineData("10.0.0.1 - - [12/Oct/2026:14:02:31 +0000] \"GET /\"")]
    [InlineData("1.8.402 build started")]
    [InlineData("99:99:99 nonsense")]
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData("2026-13-45 99:99:99")]
    [InlineData("123 short number")]
    [InlineData("Xyz  5 14:02:31 not a month")]
    public void What_is_not_a_timestamp_is_not_read_as_one(string line)
        => Assert.False(ClockShape.TryScan(line, 0, out _, out _), line);

    /// <summary>A ten-digit epoch has to name a moment a log could have been written at. Without that, any
    /// long identifier at the start of a line would pass for a date.</summary>
    [Theory]
    [InlineData("1754402551 ok", true)]
    [InlineData("9999999999 far future", false)]
    [InlineData("0000000001 far past", false)]
    public void An_epoch_count_has_to_name_a_plausible_moment(string line, bool read)
        => Assert.Equal(read, ClockShape.TryScan(line, 0, out _, out _));
}

public class ClockMathTests
{
    private static long At(int h, int m, int s) => new TimeSpan(h, m, s).Ticks;

    [Fact]
    public void Midnight_is_a_few_seconds_forward_not_a_day_back()
    {
        long elapsed = ClockMath.Elapsed(At(23, 59, 58), At(0, 0, 1), wrapsAtMidnight: true);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, elapsed);
    }

    /// <summary>A line genuinely out of order is a real answer and stays negative - concurrent writers do
    /// emit them, and hiding it would hide something true about the log.</summary>
    [Fact]
    public void A_small_step_backwards_stays_a_step_backwards()
    {
        long elapsed = ClockMath.Elapsed(At(14, 2, 31), At(14, 2, 29), wrapsAtMidnight: true);
        Assert.Equal(-TimeSpan.FromSeconds(2).Ticks, elapsed);
    }

    /// <summary>With a date in the stamp there is no ambiguity to resolve, so nothing is adjusted.</summary>
    [Fact]
    public void A_dated_stamp_never_comes_round()
    {
        long elapsed = ClockMath.Elapsed(At(23, 59, 58), At(0, 0, 1), wrapsAtMidnight: false);
        Assert.True(elapsed < 0);
    }
}

public class ElapsedTextTests
{
    [Theory]
    [InlineData(0L, 3, "0.000")]
    [InlineData(14_820_000L, 3, "1.482")]
    [InlineData(-310_000L, 3, "-0.031")]
    [InlineData(14_820_000L, 0, "1")]
    [InlineData(14_820_000L, 6, "1.482000")]
    [InlineData(1L, 6, "0.000000")]
    [InlineData(1L, 7, "0.0000001")]
    public void The_column_says_seconds_to_the_log_s_own_precision(long ticks, int digits, string text)
        => Assert.Equal(text, ElapsedText.Gutter(ticks, digits));

    /// <summary>Past the width the column was sized for it says so, rather than growing and sliding the
    /// whole log sideways.</summary>
    [Theory]
    [InlineData(100_000_000_000L, ">9999")]
    [InlineData(-100_000_000_000L, "<-9999")]
    public void A_gap_too_long_to_draw_is_said_to_be_too_long(long ticks, string text)
        => Assert.Equal(text, ElapsedText.Gutter(ticks, 3));

    [Fact]
    public void Nothing_it_can_draw_is_wider_than_what_it_was_sized_for()
    {
        for (int digits = 0; digits <= 7; digits++)
        {
            int room = ElapsedText.WidestGutter(digits).Length;
            foreach (long ticks in new[] { 0L, 1L, -1L, 9_999_999_999L, -9_999_999_999L, 99_990_000_000L, long.MaxValue / 2 })
                Assert.True(ElapsedText.Gutter(ticks, digits).Length <= room,
                            $"{ElapsedText.Gutter(ticks, digits)} does not fit {room} at {digits} digits");
        }
    }

    [Theory]
    [InlineData(8_400L, "840 \u00b5s")]
    [InlineData(124_570L, "12.457 ms")]
    [InlineData(14_820_000L, "1.482 s")]
    [InlineData(1_240_000_000L, "2 m 04 s")]
    [InlineData(43_200_000_000L, "1 h 12 m")]
    [InlineData(-14_820_000L, "-1.482 s")]
    public void The_status_bar_takes_the_unit_that_suits_the_value(long ticks, string text)
        => Assert.Equal(text, ElapsedText.Status(ticks));
}

public class ClockSpecTests
{
    /// <summary>The time field is a PART index, so adding a part before it has to carry it along - exactly
    /// as the columns' own sources are carried.</summary>
    [Fact]
    public void The_time_field_follows_its_part_when_the_template_grows()
    {
        var spec = new ColumnSpec { Template = "{[*]}{[*]}{*}" };
        spec.Reset();
        spec.TimePart = 1;
        spec.TimeFormat = "HH:mm:ss";

        spec.Template = "{[*]}{(*)}{[*]}{*}";
        spec.Sync(spec.Compiled.PartIndexAtOffset(5));

        Assert.Equal(2, spec.TimePart);
        Assert.Equal("HH:mm:ss", spec.TimeFormat);
    }

    [Fact]
    public void Taking_the_time_field_away_leaves_no_clock_behind()
    {
        var spec = new ColumnSpec { Template = "{[*]}{[*]}{*}" };
        spec.Reset();
        spec.TimePart = 1;
        spec.TimeFormat = "HH:mm:ss";

        spec.Template = "{[*]}{*}";
        spec.Sync(spec.Compiled.PartIndexAtOffset(5));
        spec.NormalizeSources();

        Assert.Equal(-1, spec.TimePart);
        Assert.False(spec.HasTime);
    }

    /// <summary>A part of pure fixed text has no text that changes from line to line, so it cannot be the
    /// one carrying a stamp - and a hand-edited file could say it was.</summary>
    [Fact]
    public void A_field_that_captures_nothing_cannot_be_the_time()
    {
        var spec = new ColumnSpec { Template = "{[}{*}" };
        spec.Reset();
        spec.TimePart = 0;
        spec.TimeFormat = "HH:mm:ss";
        spec.NormalizeSources();
        Assert.Equal(-1, spec.TimePart);
    }

    [Fact]
    public void What_the_reader_set_travels_with_the_spec()
    {
        var spec = new ColumnSpec { Template = "{[*]}{*}", TimePart = 0, TimeFormat = "HH:mm:ss.fff" };
        spec.Reset();
        spec.TimePart = 0;
        spec.TimeFormat = "HH:mm:ss.fff";

        var copy = spec.Clone();
        Assert.Equal(0, copy.TimePart);
        Assert.Equal("HH:mm:ss.fff", copy.TimeFormat);

        var onto = new ColumnSpec();
        onto.CopyFrom(spec);
        Assert.Equal(0, onto.TimePart);
        Assert.Equal("HH:mm:ss.fff", onto.TimeFormat);

        // Anything that decides what is read has to be in the summary, or a change to it would not count
        // as a change and the filter set would never be offered for saving.
        var other = spec.Clone();
        other.TimeFormat = "HH:mm:ss";
        Assert.NotEqual(spec.Describe(), other.Describe());
        other.TimeFormat = "HH:mm:ss.fff";
        other.TimePart = -1;
        Assert.NotEqual(spec.Describe(), other.Describe());
    }

    /// <summary>The field is read out of a template whether or not the fields are being DRAWN. Someone who
    /// only wants elapsed times should never have to turn column mode on.</summary>
    [Fact]
    public void A_time_field_is_read_with_the_columns_switched_off()
    {
        var spec = new ColumnSpec { Enabled = false, Template = "{[*]}{[*]}{*}" };
        spec.Reset();
        spec.TimePart = 0;
        spec.TimeFormat = "HH:mm:ss.fff";

        var clock = LogClock.From(spec);
        Assert.NotNull(clock);
        Assert.True(clock!.TryRead("[14:02:31.884][api] hello", out long ticks));
        Assert.Equal(new TimeSpan(0, 14, 2, 31, 884).Ticks, ticks);
    }
}
