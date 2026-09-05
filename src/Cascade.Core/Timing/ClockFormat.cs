using System.Globalization;

namespace Cascade.Core.Timing;

/// <summary>
/// How the text of a timestamp is turned into a number.
///
/// <para>Either a <b>.NET custom date and time format string</b> - the same language
/// <c>DateTime.ToString</c> takes, so there is no new syntax to learn or document - or one of the
/// <b>named forms</b> for stamps that are not calendar dates at all: seconds since the epoch, or seconds
/// since the machine started.</para>
///
/// <para>The answer is a TICK COUNT rather than a <see cref="DateTime"/>, because everything downstream
/// measures differences. A log carrying only a time of day, or only an uptime, has no calendar date to
/// give and does not need one; a scalar covers all three shapes and a calendar type covers one.</para>
///
/// <para>Parsing is always <see cref="CultureInfo.InvariantCulture"/>. In a custom format string
/// <c>:</c> means "the culture's time separator" and <c>/</c> means "the culture's date separator", so on
/// a machine whose culture separates with a dot, <c>HH:mm:ss</c> read under the current culture would fail
/// to read <c>14:02:31</c>. Invariant makes both mean themselves, which is what lets the format shown to
/// the reader stay the pretty one.</para>
/// </summary>
public sealed class ClockFormat
{
    /// <summary>The most fractional digits a tick count can carry. .NET counts in 100ns units, so a stamp
    /// written to nanoseconds is read to the first seven digits and the rest is dropped.</summary>
    public const int MaxFractionDigits = 7;

    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private readonly string[] _patterns;
    private readonly bool _epoch;
    private readonly long _mul, _div;

    private ClockFormat(string source, string[] patterns, int fractionDigits, bool wraps)
    {
        Source = source;
        _patterns = patterns;
        FractionDigits = fractionDigits;
        WrapsAtMidnight = wraps;
        _mul = _div = 1;
    }

    private ClockFormat(string source, bool epoch, long mul, long div, int fractionDigits)
    {
        Source = source;
        _patterns = [];
        _epoch = epoch;
        _mul = mul;
        _div = div;
        FractionDigits = fractionDigits;
    }

    /// <summary>Exactly what was written, so what is saved and what is shown are the same string.</summary>
    public string Source { get; }

    /// <summary>How many fractional digits of a second the stamp carries. What the elapsed column is drawn
    /// to, so it never claims precision the log does not have.</summary>
    public int FractionDigits { get; }

    /// <summary>True for a stamp that says a time of day and no date, where a difference of nearly a whole
    /// day backwards is midnight rather than a line out of order.</summary>
    public bool WrapsAtMidnight { get; }

    /// <summary>The named forms, in the order they are offered.</summary>
    public static IReadOnlyList<string> NamedForms { get; } =
        ["epoch:s", "epoch:ms", "epoch:us", "epoch:ns", "elapsed:s", "elapsed:ms", "elapsed:us", "elapsed:ns"];

    /// <summary>Reads a format, or null when it says nothing that could be parsed. A pattern is checked by
    /// USING it - a format string that reads nothing back is no use however well-formed it looks.</summary>
    public static ClockFormat? Compile(string? text)
    {
        string source = text?.Trim() ?? "";
        if (source.Length == 0) return null;

        if (TryNamed(source, out var named, out bool wasNamed)) return named;
        // "epoch:fortnights" is a named form with the unit misspelled, not a pattern. Left to fall through
        // it would be read as a custom format - 'h' is an hour and ':' a separator - and quietly accepted.
        if (wasNamed) return null;

        // A one-character custom format is read as a STANDARD format specifier, which is never what someone
        // typing "H" means. % is how the framework itself says "no, the custom one".
        string pattern = source.Length == 1 ? "%" + source : source;

        int digits = CountFraction(source);
        var patterns = digits > 0 ? new[] { pattern, WithoutFraction(pattern) } : [pattern];
        var format = new ClockFormat(source, patterns, digits, !HasDatePart(source));

        // A format that cannot read back what it writes is not a format. Rendering a known moment and
        // reading it again catches an empty pattern, a pattern of pure literals, and anything the framework
        // will not round-trip - without needing a table of what is legal.
        var probe = new DateTime(2026, 8, 5, 14, 2, 31, 884, DateTimeKind.Utc).AddTicks(1234);
        string written;
        try { written = probe.ToString(pattern, CultureInfo.InvariantCulture); }
        catch (FormatException) { return null; }
        return written.Length > 0 && format.TryRead(written, out _) ? format : null;
    }

    /// <summary>Whether a format can be read at all, without building one.</summary>
    public static bool IsValid(string? text) => Compile(text) is not null;

    private static bool TryNamed(string source, out ClockFormat? format, out bool named)
    {
        format = null;
        named = false;
        int colon = source.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return false;

        string kind = source[..colon].Trim();
        bool epoch = kind.Equals("epoch", StringComparison.OrdinalIgnoreCase);
        if (!epoch && !kind.Equals("elapsed", StringComparison.OrdinalIgnoreCase)) return false;
        named = true;

        // Ticks are 100ns, so a nanosecond stamp divides rather than multiplies.
        (long mul, long div, int digits) = source[(colon + 1)..].Trim().ToLowerInvariant() switch
        {
            "s" or "sec" or "seconds" => (TicksPerSecond, 1L, 7),
            "ms" or "milli" or "milliseconds" => (TimeSpan.TicksPerMillisecond, 1L, 3),
            "us" or "\u00b5s" or "micro" or "microseconds" => (10L, 1L, 6),
            "ns" or "nano" or "nanoseconds" => (1L, 100L, 7),
            _ => (0L, 0L, 0)
        };
        if (mul == 0) return false;

        format = new ClockFormat(source, epoch, mul, div, digits);
        return true;
    }

    /// <summary>Reads one timestamp. False when the text is not what the format describes, which is an
    /// ordinary answer: a stack trace or a continuation line simply carries no time.</summary>
    public bool TryRead(ReadOnlySpan<char> text, out long ticks)
    {
        if (_patterns.Length == 0) return TryReadNumber(text, out ticks);

        // AssumeUniversal leaves a stamp with no zone exactly as written; AdjustToUniversal brings one that
        // does carry a zone onto the same scale, so a log whose offset changes part way through still
        // measures correctly across the change.
        const DateTimeStyles Styles = DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AllowInnerWhite
                                    | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTime.TryParseExact(text, _patterns, CultureInfo.InvariantCulture, Styles, out var when))
        {
            ticks = when.Ticks;
            return true;
        }
        ticks = 0;
        return false;
    }

    /// <summary>A count rather than a date: digits, optionally with a fractional part, scaled to ticks.
    /// Kept in integer arithmetic - a double loses the last digits of a nanosecond epoch, which is exactly
    /// the resolution someone reading such a log came for.</summary>
    private bool TryReadNumber(ReadOnlySpan<char> text, out long ticks)
    {
        ticks = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        bool negative = text[0] == '-';
        if (negative || text[0] == '+') text = text[1..];

        int i = 0;
        long whole = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            if (whole > (long.MaxValue - 9) / 10) return false;
            whole = whole * 10 + (text[i] - '0');
            i++;
        }
        if (i == 0) return false;

        long fraction = 0, scale = 1;
        if (i < text.Length && (text[i] == '.' || text[i] == ','))
        {
            i++;
            int start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                if (i - start < MaxFractionDigits) { fraction = fraction * 10 + (text[i] - '0'); scale *= 10; }
                i++;
            }
            if (i == start) return false;
        }
        if (i != text.Length) return false;

        try
        {
            checked
            {
                ticks = whole * _mul / _div;
                if (fraction != 0) ticks += fraction * _mul / (_div * scale);
            }
        }
        catch (OverflowException) { return false; }

        if (negative) ticks = -ticks;
        if (_epoch) ticks += DateTime.UnixEpoch.Ticks;
        return true;
    }

    /// <summary>The longest run of fractional-second specifiers, which is how much of a second the stamp
    /// carries. <c>f</c> and <c>F</c> differ only in whether the digits may be left out.</summary>
    private static int CountFraction(string format)
    {
        int best = 0, run = 0;
        foreach (var (c, literal) in Walk(format))
        {
            if (!literal && (c == 'f' || c == 'F')) best = Math.Max(best, ++run);
            else run = 0;
        }
        return Math.Min(best, MaxFractionDigits);
    }

    /// <summary>Whether the format says anything about a DATE. A stamp that does not can only be compared
    /// within one day, so a large step backwards across it is midnight coming round.</summary>
    private static bool HasDatePart(string format)
    {
        foreach (var (c, literal) in Walk(format))
            if (!literal && c is 'y' or 'M' or 'd') return true;
        return false;
    }

    /// <summary>Drops the fractional part, so one format reads both <c>12:00:00.123</c> and
    /// <c>12:00:00</c> - logs trim a trailing zero fraction more often than not.</summary>
    private static string WithoutFraction(string format)
    {
        var sb = new System.Text.StringBuilder(format.Length);
        var chars = Walk(format).ToList();
        for (int i = 0; i < chars.Count; i++)
        {
            var (c, literal) = chars[i];
            if (literal || (c != 'f' && c != 'F')) { sb.Append(Requote(c, literal)); continue; }

            // The separator in front of the fraction goes with it: ".fff" without the digits leaves a
            // trailing dot that the stamp does not have either.
            if (sb.Length > 0 && (sb[^1] == '.' || sb[^1] == ',')) sb.Length--;
            while (i + 1 < chars.Count && !chars[i + 1].Literal &&
                   (chars[i + 1].Char == 'f' || chars[i + 1].Char == 'F')) i++;
        }
        return sb.ToString();
    }

    private static string Requote(char c, bool literal)
        => literal && char.IsAsciiLetter(c) ? "\\" + c : c.ToString();

    /// <summary>The format's characters, each said to be a specifier or a literal. Escapes and quoted runs
    /// are what tell the two apart - <c>'T'</c> and <c>\T</c> are text, a bare <c>d</c> is a day.</summary>
    private static IEnumerable<(char Char, bool Literal)> Walk(string format)
    {
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\')
            {
                if (++i < format.Length) yield return (format[i], true);
                continue;
            }
            if (c is '\'' or '"')
            {
                char quote = c;
                while (++i < format.Length && format[i] != quote) yield return (format[i], true);
                continue;
            }
            if (c == '%') continue;
            yield return (c, false);
        }
    }
}
