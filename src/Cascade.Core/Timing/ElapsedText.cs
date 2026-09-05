using System.Globalization;

namespace Cascade.Core.Timing;

/// <summary>Arithmetic on the times a clock reads.</summary>
public static class ClockMath
{
    /// <summary>More than this far backwards, in a log that says a time of day and no date, is midnight
    /// coming round rather than a line out of order. Half a day is the point at which the one reading
    /// becomes likelier than the other; a genuine step back of thirteen hours is read as eleven forwards,
    /// which is the price of a log that never says what day it is.</summary>
    private const long Rollover = TimeSpan.TicksPerDay / 2;

    /// <summary>How much time passed between two stamps. Backwards is a real answer and is reported as
    /// one - lines written by concurrent threads do arrive out of order, and hiding that would be hiding
    /// something true about the log.</summary>
    public static long Elapsed(long from, long to, bool wrapsAtMidnight)
    {
        long delta = to - from;
        if (wrapsAtMidnight && delta < -Rollover) delta += TimeSpan.TicksPerDay;
        return delta;
    }
}

/// <summary>
/// How a length of time is written.
///
/// <para>The two places it is written want different things, and they are deliberately different rather
/// than accidentally inconsistent. The gutter is SCANNED, so it is aligned: seconds, right-aligned, fixed
/// width, no unit - a long gap is longer on the page and its decimal point marches right, which is what
/// makes an outlier findable without reading any of them. The status bar is READ, one value at a time with
/// nothing to line up against, so it takes whatever unit suits the value and says which.</para>
/// </summary>
public static class ElapsedText
{
    /// <summary>How many seconds the gutter has room for when nobody says otherwise - enough for any gap
    /// between two adjacent lines that is worth reading as a number. A caller measuring from a fixed origin
    /// passes the log's own span instead, since every value it can produce is bounded by that.</summary>
    public const long DefaultWidestSeconds = 9999;

    private const string Nothing = "";

    /// <summary>One value for the elapsed column: seconds, to whatever of a second the log itself carries.
    /// </summary>
    /// <param name="widestSeconds">The largest figure the column has been sized for. Past it the column
    /// says so rather than growing, because a column that changed width as it scrolled would slide the
    /// whole log sideways.</param>
    public static string Gutter(long ticks, int fractionDigits, long widestSeconds = DefaultWidestSeconds)
    {
        int digits = Math.Clamp(fractionDigits, 0, ClockFormat.MaxFractionDigits);
        long widest = Math.Max(1, widestSeconds);
        bool negative = ticks < 0;
        long abs = Math.Abs(ticks);

        long scale = Pow10(ClockFormat.MaxFractionDigits - digits);
        long units = scale > 1 ? (abs + scale / 2) / scale : abs;   // half-up, so 0.4995 does not read as 0.499
        long divisor = Pow10(digits);
        long whole = units / divisor, fraction = units % divisor;

        if (whole > widest)
            return (negative ? "<-" : ">") + widest.ToString(CultureInfo.InvariantCulture);

        string sign = negative ? "-" : "";
        return digits == 0
            ? sign + whole.ToString(CultureInfo.InvariantCulture)
            : sign + whole.ToString(CultureInfo.InvariantCulture) + "."
                   + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>The longest the column can ever be asked to draw, which is what it is sized from - never
    /// the values on screen, or scrolling would resize it.
    ///
    /// <para>Including the words it says instead of a value it cannot fit. On a log carrying whole seconds
    /// and no fraction, "&lt;-9999" is a character LONGER than the widest figure - so a column sized for
    /// the figures alone clipped the one thing it was drawing because it had run out of room.</para>
    /// </summary>
    public static string WidestGutter(int fractionDigits, long widestSeconds = DefaultWidestSeconds)
    {
        int digits = Math.Clamp(fractionDigits, 0, ClockFormat.MaxFractionDigits);
        string seconds = Math.Max(1, widestSeconds).ToString(CultureInfo.InvariantCulture);
        string value = digits == 0 ? "-" + seconds : "-" + seconds + "." + new string('9', digits);
        string overflow = "<-" + seconds;
        return overflow.Length > value.Length ? overflow : value;
    }

    /// <summary>One value for the status bar: the unit that suits it, and said out loud.
    ///
    /// <para>Nothing at all is just "0". Every unit measures the same amount of it, so naming one only
    /// invites the question of why that one - and sitting on the reference line, where the reading is
    /// zero by definition, is the first thing anybody does with it.</para>
    /// </summary>
    public static string Status(long ticks)
    {
        if (ticks == 0) return "0";

        string sign = ticks < 0 ? "-" : "";
        long abs = Math.Abs(ticks);

        if (abs < TimeSpan.TicksPerMillisecond)
            return sign + Round(abs / 10.0) + " \u00b5s";
        if (abs < TimeSpan.TicksPerSecond)
            return sign + Round(abs / (double)TimeSpan.TicksPerMillisecond) + " ms";
        if (abs < TimeSpan.TicksPerMinute)
            return sign + Round(abs / (double)TimeSpan.TicksPerSecond) + " s";

        var span = TimeSpan.FromTicks(abs);
        if (abs < TimeSpan.TicksPerHour)
            return sign + span.Minutes.ToString(CultureInfo.CurrentCulture) + " m "
                 + span.Seconds.ToString("00", CultureInfo.CurrentCulture) + " s";

        long hours = abs / TimeSpan.TicksPerHour;
        return sign + hours.ToString("N0", CultureInfo.CurrentCulture) + " h "
             + span.Minutes.ToString("00", CultureInfo.CurrentCulture) + " m";
    }

    private static string Round(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>Values wide enough to size the status bar's box from, so a figure appearing there never
    /// shifts what is beside it.</summary>
    public static IReadOnlyList<string> WidestStatus { get; } =
        ["-999.999 \u00b5s", "-999.999 ms", "-59.999 s", "-59 m 59 s", "-999 h 59 m"];

    /// <summary>What the status bar says when there is nothing to measure - a dash rather than an empty
    /// box, so the reader can see the feature is there and waiting for a selection.</summary>
    public const string None = "\u2014";

    private static long Pow10(int n)
    {
        long v = 1;
        for (int i = 0; i < n; i++) v *= 10;
        return v;
    }
}
