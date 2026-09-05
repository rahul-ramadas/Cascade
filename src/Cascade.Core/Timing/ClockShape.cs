using System.Text;

namespace Cascade.Core.Timing;

/// <summary>
/// Reads the SHAPE of a timestamp written at a known place in a line, and says which
/// <see cref="ClockFormat"/> would read it.
///
/// <para>Used twice over. Detection uses the format it proposes; a clock that was detected at the start of
/// a line uses the LENGTH, because how many fractional digits a stamp carries varies from line to line and
/// the stretch to hand to the parser has to be found again each time.</para>
///
/// <para>It only ever looks where it is told. Deciding WHERE a timestamp is in an arbitrary line is the
/// ambiguous half of the problem and is not attempted here - <see cref="ClockDetector"/> tries the start of
/// the line and one character in, and anything else is named by the reader through a field template.</para>
/// </summary>
public static class ClockShape
{
    private static readonly string[] Months =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>The window an epoch count has to land in to be believed. A run of digits at the start of a
    /// line is far more often an identifier than a date, and this is what tells them apart - so it is the
    /// span a log could plausibly have been WRITTEN in rather than the whole range the number could hold.
    /// <para>It cannot separate a sequence number that happens to look like a recent moment. Nothing can;
    /// that is what naming the field yourself is for.</para></summary>
    private static readonly long EarliestEpoch = new DateTime(2005, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long LatestEpoch = DateTime.UtcNow.AddYears(5).Ticks;

    private static readonly (int Digits, string Unit, ClockFormat Format)[] Epochs =
        [(10, "s", ClockFormat.Compile("epoch:s")!), (13, "ms", ClockFormat.Compile("epoch:ms")!),
         (16, "us", ClockFormat.Compile("epoch:us")!), (19, "ns", ClockFormat.Compile("epoch:ns")!)];

    /// <summary>Scratch for the format being built. A scan runs for every row the elapsed column draws, so
    /// it may not allocate; one buffer a thread costs nothing and belongs to nobody.</summary>
    [ThreadStatic] private static StringBuilder? _scratch;

    /// <summary>Reads the stamp at <paramref name="at"/>. <paramref name="length"/> is how much of the line
    /// it covers and <paramref name="format"/> is what reads it.</summary>
    public static bool TryScan(ReadOnlySpan<char> line, int at, out int length, out string format)
    {
        if (!TryMeasure(line, at, out length)) { format = ""; return false; }
        format = _scratch!.ToString();
        return format.Length > 0;
    }

    /// <summary>How much of the line the stamp at <paramref name="at"/> covers, without saying what reads
    /// it. A clock that already knows where to look asks this once a row: how many fractional digits a
    /// stamp carries varies from line to line, so the stretch to hand the parser has to be found again each
    /// time.</summary>
    public static bool TryMeasure(ReadOnlySpan<char> line, int at, out int length)
    {
        length = 0;
        if (at < 0 || at >= line.Length) return false;

        var sb = _scratch ??= new StringBuilder(32);
        sb.Clear();
        int i = at;

        if (char.IsAsciiLetter(line[i]))
        {
            if (!TrySyslog(line, ref i, sb)) return false;
        }
        else if (!TryNumeric(line, ref i, sb)) return false;

        length = i - at;
        return length > 0 && sb.Length > 0;
    }

    /// <summary>A syslog header: a month by name, a day padded with a space, and a time. No year, so such a
    /// log can only be measured within one year - which is what it says about itself.</summary>
    private static bool TrySyslog(ReadOnlySpan<char> line, ref int i, StringBuilder sb)
    {
        if (i + 3 > line.Length) return false;
        Span<char> lower = stackalloc char[3];
        for (int k = 0; k < 3; k++) lower[k] = char.ToLowerInvariant(line[i + k]);
        bool known = false;
        foreach (string month in Months)
            if (lower.SequenceEqual(month)) { known = true; break; }
        if (!known) return false;

        int p = i + 3;
        if (!SkipSpaces(line, ref p)) return false;
        int day = Digits(line, p);
        if (day is < 1 or > 2 || Value(line, p, day) is < 1 or > 31) return false;
        p += day;
        if (!SkipSpaces(line, ref p)) return false;

        sb.Append("MMM d ");
        if (!TryTime(line, ref p, sb)) return false;
        i = p;
        return true;
    }

    private static bool TryNumeric(ReadOnlySpan<char> line, ref int i, StringBuilder sb)
    {
        int run = Digits(line, i);
        if (run == 0) return false;
        char next = i + run < line.Length ? line[i + run] : '\0';

        // A date, then whatever parts a time and a fraction it is followed by.
        if (run == 4 && next is '-' or '/')
        {
            int p = i;
            if (!TryDate(line, ref p, sb, yearFirst: true)) return false;
            TryDateTimeSeparator(line, ref p, sb);
            i = p;
            return true;
        }
        if (run == 2 && next is '-' or '/')
        {
            int p = i;
            if (!TryDate(line, ref p, sb, yearFirst: false)) return false;
            TryDateTimeSeparator(line, ref p, sb);
            i = p;
            return true;
        }
        if (run == 2 && next == ':')
        {
            int p = i;
            if (!TryTime(line, ref p, sb)) return false;
            if (TryFraction(line, ref p, sb)) TryZone(line, ref p, sb);
            i = p;
            return true;
        }

        return TryEpoch(line, ref i, run, next, sb);
    }

    /// <summary>A count of seconds - or of some smaller unit - since 1970. Believed only when the digits
    /// stop where a count would stop and the moment they name is one a log could have been written at.
    /// </summary>
    private static bool TryEpoch(ReadOnlySpan<char> line, ref int i, int run, char next, StringBuilder sb)
    {
        if (char.IsAsciiDigit(next) || next == '.') return false;
        foreach (var (digits, unit, format) in Epochs)
        {
            if (digits != run) continue;
            if (!format.TryRead(line.Slice(i, run), out long ticks)) return false;
            if (ticks < EarliestEpoch || ticks > LatestEpoch) return false;
            sb.Append("epoch:").Append(unit);
            i += run;
            return true;
        }
        return false;
    }

    /// <summary>The three numbers of a date and the mark between them. Which of the two-digit numbers is
    /// the day cannot be told from one line, so the reading is left as month-first and
    /// <see cref="ClockDetector"/> settles it across the sample - where a number over twelve gives it
    /// away.</summary>
    private static bool TryDate(ReadOnlySpan<char> line, ref int i, StringBuilder sb, bool yearFirst)
    {
        int first = yearFirst ? 4 : 2, last = yearFirst ? 2 : 4;
        int p = i;
        if (Digits(line, p) != first) return false;
        p += first;
        if (p >= line.Length) return false;
        char sep = line[p];
        if (sep is not ('-' or '/')) return false;
        p++;
        if (Digits(line, p) != 2 || Value(line, p, 2) > 31) return false;
        p += 2;
        if (p >= line.Length || line[p] != sep) return false;
        p++;
        if (Digits(line, p) != last) return false;
        int third = Value(line, p, last);
        if (last == 2 && third > 31) return false;
        p += last;

        sb.Append(yearFirst ? "yyyy" : "MM").Append(sep)
          .Append(yearFirst ? "MM" : "dd").Append(sep)
          .Append(yearFirst ? "dd" : "yyyy");
        i = p;
        return true;
    }

    /// <summary>What comes between a date and the time after it, and then the time. A date on its own is a
    /// perfectly good stamp, so none of this is required.</summary>
    private static void TryDateTimeSeparator(ReadOnlySpan<char> line, ref int i, StringBuilder sb)
    {
        int p = i;
        if (p >= line.Length) return;
        char sep = line[p];
        if (sep is not (' ' or 'T' or '_' or '-')) return;
        p++;
        if (Digits(line, p) != 2) return;

        // Written down before the time is known to be there and taken back if it is not. The alternative is
        // a second buffer, and this runs for every row the elapsed column draws.
        int mark = sb.Length;
        if (sep == 'T') sb.Append("'T'"); else sb.Append(sep);
        if (!TryTime(line, ref p, sb)) { sb.Length = mark; return; }
        if (TryFraction(line, ref p, sb)) TryZone(line, ref p, sb);
        i = p;
    }

    private static bool TryTime(ReadOnlySpan<char> line, ref int i, StringBuilder sb)
    {
        int p = i;
        if (Digits(line, p) != 2 || Value(line, p, 2) > 23) return false;
        p += 2;
        if (p >= line.Length || line[p] != ':') return false;
        p++;
        if (Digits(line, p) != 2 || Value(line, p, 2) > 59) return false;
        p += 2;
        sb.Append("HH:mm");

        if (p + 2 < line.Length && line[p] == ':' && Digits(line, p + 1) == 2 && Value(line, p + 1, 2) <= 60)
        {
            p += 3;
            sb.Append(":ss");
        }
        i = p;
        return true;
    }

    /// <summary>Whatever of a second the stamp carries, to the seven digits a tick count holds. Returns
    /// false when the stamp was written to more than that: the scan STOPS at the seventh digit, because the
    /// stretch handed to the parser has to be one the format can read - and whatever followed the digits,
    /// a zone among it, is then no longer next to the part being read.</summary>
    private static bool TryFraction(ReadOnlySpan<char> line, ref int i, StringBuilder sb)
    {
        if (i >= line.Length || (line[i] != '.' && line[i] != ',')) return true;
        int run = Digits(line, i + 1);
        if (run == 0) return true;

        int use = Math.Min(run, ClockFormat.MaxFractionDigits);
        sb.Append(line[i]).Append('f', use);
        i += 1 + use;
        return run == use;
    }

    /// <summary>A trailing <c>Z</c> or <c>+05:30</c>. <c>K</c> reads either and tolerates neither being
    /// there. The colonless form is left alone: no single specifier reads it, and a log whose offset never
    /// changes measures correctly without it.</summary>
    private static void TryZone(ReadOnlySpan<char> line, ref int i, StringBuilder sb)
    {
        if (i >= line.Length) return;
        if (line[i] is 'Z' or 'z') { sb.Append('K'); i++; return; }
        if (line[i] is not ('+' or '-')) return;
        if (Digits(line, i + 1) != 2 || i + 3 >= line.Length || line[i + 3] != ':') return;
        if (Digits(line, i + 4) != 2) return;
        sb.Append('K');
        i += 6;
    }

    private static bool SkipSpaces(ReadOnlySpan<char> line, ref int i)
    {
        int from = i;
        while (i < line.Length && line[i] == ' ') i++;
        return i > from;
    }

    /// <summary>How many digits run from <paramref name="at"/>, capped so a very long run of them cannot
    /// turn a scan of a header into a walk down a line of pure numbers.</summary>
    private static int Digits(ReadOnlySpan<char> line, int at)
    {
        const int Longest = 20;
        int n = 0;
        while (at + n < line.Length && n <= Longest && char.IsAsciiDigit(line[at + n])) n++;
        return n;
    }

    private static int Value(ReadOnlySpan<char> line, int at, int count)
    {
        int v = 0;
        for (int k = 0; k < count; k++) v = v * 10 + (line[at + k] - '0');
        return v;
    }
}
