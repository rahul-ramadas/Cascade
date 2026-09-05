using Cascade.Core.Columns;

namespace Cascade.Core.Timing;

/// <summary>
/// Proposes a clock for a log nobody has described yet.
///
/// <para>It does NOT try to recognise a timestamp in a line, which is the ambiguous half of the problem -
/// in isolation an address, a version and a time are all "numbers with punctuation". It exploits the fact
/// that a log file is HOMOGENEOUS, so hundreds of samples of one shape turn a classification into a
/// consensus: a candidate has to sit at the same place in most lines, be a plausible date, and ASCEND. A
/// version number does not ascend, a sequence number is the wrong shape, and an address is neither.</para>
///
/// <para>It only looks at the start of the line, or one character in past a bracket. That restriction is
/// what makes guessing defensible rather than clever; a stamp anywhere else is named by the reader through
/// a field template, and the elapsed column says so when it has nothing to show.</para>
///
/// <para>What it produces is a PROPOSAL. It is never saved, it is worked out again on every open, and the
/// reader can see it and correct it in the field settings.</para>
/// </summary>
public static class ClockDetector
{
    /// <summary>How much of the sample has to carry the stamp. Well under all of it: stack traces, banners
    /// and continuation lines legitimately carry no time.</summary>
    private const double LeastCoverage = 0.5;

    /// <summary>How much of the sample has to ascend. This is the gate that does the real work, so where it
    /// sits matters: a field of unrelated numbers ascends about half the time, so anything well clear of a
    /// half is already a strong reading. Nine tenths keeps that margin while tolerating what a log written
    /// by several threads at once really looks like - MEASURED at 92% ascending on one where 8% of lines
    /// were queued a moment before they were written, which at 98% was refused a clock altogether.</summary>
    private const double LeastAscending = 0.90;

    private const int FewestReadable = 5;

    /// <summary>The brackets a header may open with, past which a stamp is still at "the start".</summary>
    private const string Openers = "[(<";

    public static LogClock? Detect(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return null;

        var seen = new Dictionary<(int Offset, string Format), int>();
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            foreach (int offset in Offsets(line))
                if (ClockShape.TryScan(line, offset, out _, out string format))
                {
                    seen.TryGetValue((offset, format), out int n);
                    seen[(offset, format)] = n + 1;
                    break;
                }
        }        if (seen.Count == 0) return null;

        // The most-supported shape, and then the most specific of those tied on support - a stamp read to
        // the second says more than the same stamp read to the minute.
        var best = seen.OrderByDescending(e => e.Value).ThenByDescending(e => e.Key.Format.Length).First();
        var clock = Build(best.Key.Offset, best.Key.Format);
        if (clock is null) return null;

        clock = SettleDayAndMonth(clock, lines);
        return Believable(clock, lines) ? clock : null;
    }

    private static IEnumerable<int> Offsets(string line)
    {
        yield return 0;
        if (Openers.Contains(line[0], StringComparison.Ordinal)) yield return 1;
    }

    private static LogClock? Build(int offset, string format)
    {
        var compiled = ClockFormat.Compile(format);
        return compiled is null ? null : LogClock.AtLineStart(offset, compiled);
    }

    /// <summary>Which of <c>05/08/2026</c>'s first two numbers is the day cannot be told from one line, so
    /// both readings are tried across the whole sample and whichever reads more of it wins. A log with any
    /// day past the twelfth gives itself away; one with none is measured correctly either way until it
    /// crosses a month, and month-first is kept as the reading that was proposed.</summary>
    private static LogClock SettleDayAndMonth(LogClock clock, IReadOnlyList<string> lines)
    {
        string format = clock.Format.Source;
        if (!format.StartsWith("MM", StringComparison.Ordinal) || format.Length < 10) return clock;

        string swapped = "dd" + format[2] + "MM" + format[5..];
        var other = Build(clock.Offset, swapped);
        return other is not null && Readable(other, lines) > Readable(clock, lines) ? other : clock;
    }

    private static int Readable(LogClock clock, IReadOnlyList<string> lines)
    {
        int n = 0;
        foreach (string line in lines) if (clock.TryRead(line, out _)) n++;
        return n;
    }

    /// <summary>The two gates. Enough of the sample has to carry the stamp, and the stamps have to run
    /// forwards - which is what nothing but a clock does.</summary>
    private static bool Believable(LogClock clock, IReadOnlyList<string> lines)
    {
        int read = 0, pairs = 0, ascending = 0;
        long previous = 0;
        bool have = false;

        foreach (string line in lines)
        {
            if (!clock.TryRead(line, out long ticks)) continue;
            read++;
            if (have)
            {
                pairs++;
                if (ClockMath.Elapsed(previous, ticks, clock.Format.WrapsAtMidnight) >= 0) ascending++;
            }
            previous = ticks;
            have = true;
        }

        if (read < FewestReadable || read < lines.Count * LeastCoverage) return false;
        return pairs == 0 || ascending >= pairs * LeastAscending;
    }

    // ---- proposing a format for a field the reader has pointed at ----

    /// <summary>What reads the text of one field of a template, judged across the sample.
    ///
    /// <para>Far narrower than <see cref="Detect"/>, and correspondingly safer: WHERE the stamp is has
    /// already been answered by the reader, so all that is left is what shape the text is - and whatever
    /// this proposes is shown to them beside a line of their own log before it is believed.</para></summary>
    public static string? GuessFormat(IReadOnlyList<string> lines, LineTemplate template, int part)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(template);
        if (!template.IsValid || part < 0 || part >= template.PartCount) return null;
        int value = template.ValueOfPart(part);
        if (value < 0) return null;

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var match = new Columns.TemplateMatch();
        int fields = 0;
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line) || !template.Match(line, match)) continue;
            var (start, length) = match.Value(value);
            if (length <= 0) continue;
            fields++;
            var text = line.AsSpan(start, length).Trim();
            if (text.Length == 0 || !ClockShape.TryScan(text, 0, out int read, out string format)) continue;
            if (read < text.Length) continue;      // a shape that only covers part of the field is not the field
            seen.TryGetValue(format, out int n);
            seen[format] = n + 1;
        }
        if (fields == 0 || seen.Count == 0) return null;

        var best = seen.OrderByDescending(e => e.Value).ThenByDescending(e => e.Key.Length).First();
        return best.Value >= fields * LeastCoverage ? best.Key : null;
    }

    /// <summary>Which field of a template holds the stamp, and what reads it - the leftmost that answers,
    /// since a log that carries two times puts the one it was written at first.</summary>
    public static (int Part, string Format)? GuessField(IReadOnlyList<string> lines, LineTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        for (int part = 0; part < template.PartCount; part++)
            if (GuessFormat(lines, template, part) is { } format) return (part, format);
        return null;
    }

    /// <summary>How much of a sample one clock can actually read, which is the number the field settings
    /// show back so that a proposal can be believed rather than taken on trust.</summary>
    public static (int Read, int Total) Coverage(LogClock clock, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lines);
        int read = 0, total = 0;
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            total++;
            if (clock.TryRead(line, out _)) read++;
        }
        return (read, total);
    }
}
