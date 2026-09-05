using Cascade.Core.Columns;

namespace Cascade.Core.Timing;

/// <summary>Where in a line the timestamp is written.</summary>
public enum ClockPlace
{
    /// <summary>A fixed number of characters in from the start of the line. What detection proposes, and
    /// what is never saved - it is worked out again each time a file is opened.</summary>
    LineStart,

    /// <summary>One field of the reader's own template. What is saved, because the reader said it.</summary>
    TemplateField
}

/// <summary>
/// A timestamp reader: where the stamp is, and what reads it.
///
/// <para>The two places share everything after they have found their stretch of the line, so the parsing,
/// the precision and the tests are the same either way. They exist as two because a fixed offset costs a
/// short character walk while a template field costs a whole template match, and the elapsed column asks
/// this of every row it draws.</para>
///
/// <para><b>Not thread-safe.</b> <see cref="TryRead(string, out long)"/> uses scratch of its own; hand in a
/// <see cref="TemplateMatch"/> per thread to read from more than one at a time.</para>
/// </summary>
public sealed class LogClock
{
    private readonly LineTemplate? _template;
    private readonly int _value;
    private readonly TemplateMatch _scratch = new();

    private LogClock(ClockPlace place, int offset, int part, LineTemplate? template, int value, ClockFormat format)
    {
        Place = place;
        Offset = offset;
        Part = part;
        _template = template;
        _value = value;
        Format = format;
    }

    public ClockPlace Place { get; }

    /// <summary>Characters in from the start of the line, for <see cref="ClockPlace.LineStart"/>.</summary>
    public int Offset { get; }

    /// <summary>Which part of the template, for <see cref="ClockPlace.TemplateField"/>.</summary>
    public int Part { get; }

    public ClockFormat Format { get; }

    /// <summary>How much of a second the stamp carries, which is what the elapsed column is drawn to.</summary>
    public int FractionDigits => Format.FractionDigits;

    public static LogClock AtLineStart(int offset, ClockFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return new LogClock(ClockPlace.LineStart, Math.Max(0, offset), -1, null, -1, format);
    }

    /// <summary>Reads the field a template captures. Null when that part captures nothing - a piece of
    /// fixed text is a part too, and it has no value to read.</summary>
    public static LogClock? AtField(LineTemplate template, int part, ClockFormat format)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(format);
        if (!template.IsValid || part < 0 || part >= template.PartCount) return null;
        int value = template.ValueOfPart(part);
        return value < 0 ? null : new LogClock(ClockPlace.TemplateField, -1, part, template, value, format);
    }

    /// <summary>Builds whatever the spec names, or null when it names nothing readable.</summary>
    public static LogClock? From(ColumnSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.TimePart < 0) return null;
        var format = ClockFormat.Compile(spec.TimeFormat);
        return format is null ? null : AtField(spec.Compiled, spec.TimePart, format);
    }

    public bool TryRead(string line, out long ticks) => TryRead(line, _scratch, out ticks);

    /// <summary>Reads the stamp out of one line. False is an ordinary answer, not a fault: a stack trace,
    /// a banner or a continuation line carries no time, and the column simply leaves that row blank.</summary>
    public bool TryRead(string line, TemplateMatch scratch, out long ticks)
    {
        ticks = 0;
        if (string.IsNullOrEmpty(line)) return false;

        if (Place == ClockPlace.LineStart)
        {
            // The length is found again rather than remembered: a log that trims a trailing zero writes
            // ".12" on one line and ".120" on the next, and the stretch to read is a different size.
            if (!ClockShape.TryMeasure(line, Offset, out int length)) return false;
            return Format.TryRead(line.AsSpan(Offset, length), out ticks);
        }

        ArgumentNullException.ThrowIfNull(scratch);
        if (_template is null || !_template.Match(line, scratch)) return false;
        var (start, len) = scratch.Value(_value);
        return len > 0 && Format.TryRead(line.AsSpan(start, len), out ticks);
    }

    /// <summary>Everything that decides what this reads, as one string - so "is it still the same clock?"
    /// is one comparison, which is what anything caching a time needs to key on.</summary>
    public string Describe()
        => $"{Place}/{Offset}/{Part}/{Format.Source}/{_template?.Source}";
}
