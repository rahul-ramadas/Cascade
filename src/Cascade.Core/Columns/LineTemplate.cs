using System.Text;

namespace Cascade.Core.Columns;

/// <summary>Something wrong with a template, and where in the template text it is.</summary>
public readonly record struct TemplateIssue(string Message, int Position);

/// <summary>Where one part sits in the template text, and the value it captures (-1 when it captures
/// none - a part of pure literal text, which shows no column but can still be hidden).</summary>
public readonly record struct TemplatePart(int TemplateStart, int TemplateEnd, int Value);

/// <summary>
/// The result of matching one line. Owned and reused by the caller: the paint asks for one per row per
/// frame, and a screenful of lines would otherwise leave a few hundred arrays behind every time the view
/// is redrawn.
/// </summary>
public sealed class TemplateMatch
{
    private int[] _partStart = [], _partEnd = [], _valueStart = [], _valueLength = [];

    public bool Success { get; internal set; }

    /// <summary>Where the line stopped looking like the template, and what was wanted there. Only
    /// meaningful when <see cref="Success"/> is false.</summary>
    public int FailurePosition { get; internal set; }

    public string FailureExpected { get; internal set; } = "";

    /// <summary>Where the text the template never reached begins.</summary>
    public int TailStart { get; internal set; }

    public int PartCount { get; private set; }
    public int ValueCount { get; private set; }

    internal void Reset(int parts, int values)
    {
        if (_partStart.Length < parts) { _partStart = new int[parts]; _partEnd = new int[parts]; }
        if (_valueStart.Length < values) { _valueStart = new int[values]; _valueLength = new int[values]; }
        PartCount = parts;
        ValueCount = values;
        for (int i = 0; i < parts; i++) { _partStart[i] = -1; _partEnd[i] = -1; }
        for (int i = 0; i < values; i++) { _valueStart[i] = 0; _valueLength[i] = 0; }
        Success = false;
        FailurePosition = 0;
        FailureExpected = "";
        TailStart = 0;
    }

    internal void Touch(int part, int start, int end)
    {
        if (part < 0) return;
        if (_partStart[part] < 0 || start < _partStart[part]) _partStart[part] = start;
        if (end > _partEnd[part]) _partEnd[part] = end;
    }

    internal void SetValue(int value, int start, int length)
    {
        _valueStart[value] = start;
        _valueLength[value] = length;
    }

    /// <summary>Gives a span to any part the match never touched - one whose whole content was absorbed
    /// into a neighbour's run of spaces, for instance. Walked BACKWARDS so such a part sits exactly where
    /// the part after it begins: the spans have to stay in template order, because the projection works out
    /// what goes between two parts from where the one before them ended. Settled out of order it would copy
    /// text that had already been emitted, and the line would come out longer than it went in.</summary>
    internal void SettleUntouched(int at)
    {
        int next = at;
        for (int i = PartCount - 1; i >= 0; i--)
        {
            if (_partStart[i] < 0) { _partStart[i] = next; _partEnd[i] = next; }
            else next = _partStart[i];
        }
    }

    /// <summary>The stretch of the line one part covers - its literal text and its value together, which is
    /// what makes hiding a part take its punctuation with it.</summary>
    public (int Start, int Length) Part(int index)
        => index >= 0 && index < PartCount ? (_partStart[index], _partEnd[index] - _partStart[index]) : (0, 0);

    /// <summary>The stretch of the line one captured value covers.</summary>
    public (int Start, int Length) Value(int index)
        => index >= 0 && index < ValueCount ? (_valueStart[index], _valueLength[index]) : (0, 0);
}

/// <summary>
/// How a line is split, written as a picture of the line itself.
///
/// <list type="bullet">
/// <item><c>*</c> is a value: it matches as little as it can, up to whatever comes next.</item>
/// <item><c>{ }</c> wrap a PART - the thing that is hidden or carried about, its punctuation with it.</item>
/// <item>Anything else has to be there as written, except a run of spaces, which matches any run of
/// spaces so that padded fields need not be counted out.</item>
/// <item><c>\</c> escapes <c>{ } * \</c>.</item>
/// </list>
///
/// <para>A part holds at most one value, so a part that captures IS a column. That keeps the list of
/// columns and the list of parts the same list, which is what stops names, widths and ticks drifting away
/// from the data they belong to.</para>
///
/// <para>It compiles to literal scanning rather than a regular expression: a template is only alternating
/// literal runs and gaps, so matching is IndexOf chaining. Linear by construction - there is no
/// backtracking to blow up on and so no need for a match timeout, which matters because this runs on
/// lines that may be megabytes long. It is also what lets a failure name the literal it wanted and the
/// character it wanted it at.</para>
/// </summary>
public sealed class LineTemplate
{
    private readonly struct Piece(string text, bool spaceRun, int part)
    {
        public readonly string Text = text;
        public readonly bool SpaceRun = spaceRun;
        public readonly int Part = part;
    }

    private sealed class Run
    {
        public Piece[] Pieces = [];
        public string Display = "";
        public bool IsEmpty => Pieces.Length == 0;
    }

    private readonly Run[] _runs;                    // _runs[i] precedes value i; the last one trails
    private readonly TemplatePart[] _parts;
    private readonly int[] _valuePart;               // which part each value belongs to
    private readonly TemplateIssue[] _issues;

    public string Source { get; }
    public int PartCount => _parts.Length;
    public int ValueCount => _valuePart.Length;
    public IReadOnlyList<TemplateIssue> Issues => _issues;
    public bool IsValid => _issues.Length == 0;
    public TemplatePart PartAt(int index) => _parts[index];

    /// <summary>True when the template asks for nothing at all, so there is nothing to draw.</summary>
    public bool IsEmpty => _parts.Length == 0;

    public LineTemplate(string template)
    {
        Source = template ?? "";
        var parse = new Parser(Source);
        parse.Run();
        _parts = parse.Parts.ToArray();
        _valuePart = parse.ValuePart.ToArray();
        _runs = parse.BuildRuns();
        _issues = parse.Issues.ToArray();
    }

    // ---- parsing ----

    private sealed class Parser(string template)
    {
        public readonly List<TemplateIssue> Issues = [];
        public readonly List<TemplatePart> Parts = [];
        public readonly List<int> ValuePart = [];

        private readonly List<(string Text, int Part, bool IsValue)> _units = [];

        public void Run()
        {
            var sb = new StringBuilder();
            int i = 0, part = -1, partStart = 0, partValue = -1;
            int literalPart = -1;

            void Flush()
            {
                if (sb.Length == 0) return;
                _units.Add((sb.ToString(), literalPart, false));
                sb.Clear();
            }

            void Begin(char c)
            {
                if (sb.Length == 0) literalPart = part;
                sb.Append(c);
            }

            while (i < template.Length)
            {
                char c = template[i];
                switch (c)
                {
                    case '\\':
                        if (i + 1 >= template.Length)
                        {
                            Issues.Add(new TemplateIssue("A \\ at the very end escapes nothing. Write \\\\ for a backslash.", i));
                            i++;
                        }
                        else { Begin(template[i + 1]); i += 2; }
                        break;

                    case '{':
                        if (part >= 0)
                        {
                            Issues.Add(new TemplateIssue("A part cannot hold another part. Write \\{ for a plain brace.", i));
                            Begin(c);
                            i++;
                            break;
                        }
                        Flush();
                        part = Parts.Count;
                        partStart = i;
                        partValue = -1;
                        literalPart = part;
                        Parts.Add(new TemplatePart(i, i + 1, -1));
                        i++;
                        break;

                    case '}':
                        if (part < 0)
                        {
                            Issues.Add(new TemplateIssue("This } closes a part that was never opened. Write \\} for a plain brace.", i));
                            Begin(c);
                            i++;
                            break;
                        }
                        Flush();
                        if (i == partStart + 1)
                            Issues.Add(new TemplateIssue("This part is empty, so it matches nothing.", partStart));
                        Parts[part] = new TemplatePart(partStart, i + 1, partValue);
                        part = -1;
                        literalPart = -1;
                        i++;
                        break;

                    case '*':
                        if (part < 0)
                        {
                            Issues.Add(new TemplateIssue("A * has to be inside { }, so it is clear which part it belongs to.", i));
                            Begin(c);
                            i++;
                            break;
                        }
                        if (partValue >= 0)
                        {
                            Issues.Add(new TemplateIssue("A part can hold only one *. Split it into two parts.", i));
                            i++;
                            break;
                        }
                        Flush();
                        partValue = ValuePart.Count;
                        ValuePart.Add(part);
                        _units.Add(("", part, true));
                        i++;
                        break;

                    default:
                        Begin(c);
                        i++;
                        break;
                }
            }

            Flush();
            if (part >= 0) Issues.Add(new TemplateIssue("This part is never closed - it needs a }.", partStart));
        }

        /// <summary>Turns the units into the alternating literal runs the matcher walks. A run of spaces
        /// becomes one flexible piece; everything else, tabs included, has to be there exactly.</summary>
        public Run[] BuildRuns()
        {
            var runs = new List<Run>();
            var pieces = new List<Piece>();
            var display = new StringBuilder();

            void Close()
            {
                runs.Add(new Run { Pieces = pieces.ToArray(), Display = display.ToString() });
                pieces.Clear();
                display.Clear();
            }

            foreach (var (text, unitPart, isValue) in _units)
            {
                if (isValue) { Close(); continue; }
                display.Append(text);
                int k = 0;
                while (k < text.Length)
                {
                    if (text[k] == ' ')
                    {
                        int from = k;
                        while (k < text.Length && text[k] == ' ') k++;
                        // Runs of spaces on either side of a part boundary are ONE run to the reader, and
                        // have to be one to the matcher too: a space run is taken greedily, so a second one
                        // straight after it could never be satisfied and the template would match nothing
                        // at all - while still reporting itself perfectly valid.
                        if (pieces.Count > 0 && pieces[^1].SpaceRun) continue;
                        pieces.Add(new Piece(text[from..k], true, unitPart));
                    }
                    else
                    {
                        int from = k;
                        while (k < text.Length && text[k] != ' ') k++;
                        pieces.Add(new Piece(text[from..k], false, unitPart));
                    }
                }
            }
            Close();

            // Two values with nothing between them cannot be told apart - the first has no way to know
            // where to stop. Reported against the second, which is the one to take out.
            for (int v = 0; v + 1 < ValuePart.Count; v++)
                if (runs[v + 1].IsEmpty)
                    Issues.Add(new TemplateIssue("Two * with nothing between them - there is no way to tell where the first should stop.",
                        Parts[ValuePart[v + 1]].TemplateStart));

            return runs.ToArray();
        }
    }

    // ---- matching ----

    /// <summary>Splits <paramref name="line"/> into <paramref name="into"/>, which the caller owns and
    /// reuses. Returns false when the line does not fit the template, and then the match says where it
    /// stopped fitting.</summary>
    public bool Match(string line, TemplateMatch into)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(into);
        into.Reset(_parts.Length, _valuePart.Length);

        if (!TryRun(line, 0, _runs[0], out int consumed, out int failAt, out string failWant))
        {
            into.FailurePosition = failAt;
            into.FailureExpected = failWant;
            into.TailStart = 0;
            return false;
        }
        Record(line, 0, _runs[0], into);
        int pos = consumed;

        for (int v = 0; v < _valuePart.Length; v++)
        {
            var next = _runs[v + 1];
            if (next.IsEmpty)
            {
                // Nothing left to stop at, so this value runs to the end of the line.
                into.SetValue(v, pos, line.Length - pos);
                into.Touch(_valuePart[v], pos, line.Length);
                pos = line.Length;
                continue;
            }

            int at = Find(line, pos, next, out int runLength, out failAt, out failWant);
            if (at < 0)
            {
                into.FailurePosition = failAt;
                into.FailureExpected = failWant;
                into.TailStart = pos;
                return false;
            }

            into.SetValue(v, pos, at - pos);
            into.Touch(_valuePart[v], pos, at);
            Record(line, at, next, into);
            pos = at + runLength;
        }

        into.Success = true;
        into.TailStart = pos;
        into.SettleUntouched(pos);
        return true;
    }

    /// <summary>Tries the run at one place without recording anything. On failure it says WHICH piece was
    /// missing and where it was wanted, because "expected [ at 34" is a fix and "did not match" is not.</summary>
    private static bool TryRun(string line, int pos, Run run, out int consumed, out int failAt, out string failWant)
    {
        int p = pos;
        foreach (var piece in run.Pieces)
        {
            if (piece.SpaceRun)
            {
                int n = 0;
                while (p < line.Length && line[p] == ' ') { p++; n++; }
                if (n == 0) { consumed = 0; failAt = p; failWant = " "; return false; }
            }
            else
            {
                if (p + piece.Text.Length > line.Length ||
                    !line.AsSpan(p, piece.Text.Length).SequenceEqual(piece.Text))
                {
                    consumed = 0;
                    failAt = p;
                    failWant = piece.Text;
                    return false;
                }
                p += piece.Text.Length;
            }
        }
        consumed = p - pos;
        failAt = -1;
        failWant = "";
        return true;
    }

    /// <summary>Walks a run that is known to fit, noting which part each piece belongs to.</summary>
    private static void Record(string line, int pos, Run run, TemplateMatch into)
    {
        int p = pos;
        foreach (var piece in run.Pieces)
        {
            int from = p;
            if (piece.SpaceRun) while (p < line.Length && line[p] == ' ') p++;
            else p += piece.Text.Length;
            into.Touch(piece.Part, from, p);
        }
    }

    /// <summary>Finds the run at or after <paramref name="from"/>. When it is nowhere to be found the
    /// failure reported is from the attempt that got FURTHEST, which is where the line stopped looking like
    /// the template and so the place worth pointing at.</summary>
    private static int Find(string line, int from, Run run, out int length, out int failAt, out string failWant)
    {
        var first = run.Pieces[0];
        int bestAt = -1;
        string bestWant = "";

        int i = from;
        while (i <= line.Length)
        {
            if (!first.SpaceRun)
            {
                int at = line.IndexOf(first.Text, i, StringComparison.Ordinal);
                if (at < 0) break;
                i = at;
            }
            else
            {
                while (i < line.Length && line[i] != ' ') i++;
                if (i >= line.Length) break;
            }

            if (TryRun(line, i, run, out length, out int a, out string w)) { failAt = -1; failWant = ""; return i; }
            if (a > bestAt) { bestAt = a; bestWant = w; }

            // Past the whole run of spaces, not just one of them: see the remark above.
            if (first.SpaceRun) { while (i < line.Length && line[i] == ' ') i++; }
            else i++;
        }

        length = 0;
        failAt = bestAt < 0 ? from : bestAt;
        failWant = bestAt < 0 ? run.Display : bestWant;
        return -1;
    }

    // ---- editing support ----

    /// <summary>Which part an offset in the template text falls in, or - between parts - the part that
    /// starts after it. Half-open, so a caret left where a part was just deleted reads as the part that has
    /// moved up into its place. That is what lets an edit add or take away a column WHERE THE CARET IS
    /// instead of shifting every name along by one.</summary>
    public int PartIndexAtOffset(int offset)
    {
        for (int i = 0; i < _parts.Length; i++)
            if (offset < _parts[i].TemplateEnd) return i;
        return _parts.Length;
    }

    /// <summary>Reads the <c>[...]</c> groups off the front of a line and writes a template for them,
    /// keeping whatever separates them. This is what makes turning fields on a single click for the log
    /// formats that have them.
    ///
    /// <para>It stops at the message. What tells the two apart is what lies BETWEEN two groups: a short run
    /// of punctuation or spaces still counts as a separator, but the moment a letter or a digit turns up
    /// the header is over - otherwise a message carrying a bracket of its own, which any log full of JSON
    /// does, drags half the line into the template.</para></summary>
    public static string Detect(string sample)
    {
        if (string.IsNullOrEmpty(sample)) return "";

        // Long enough for "] [" or ") - (", short enough that a sentence cannot pass for a separator.
        const int LongestSeparator = 4;

        var sb = new StringBuilder();
        int i = 0, found = 0;
        while (i < sample.Length && sample[i] == '[')
        {
            int close = sample.IndexOf(']', i + 1);
            if (close < 0) break;
            sb.Append("{[*]}");
            found++;
            i = close + 1;

            int gap = i;
            while (gap < sample.Length && gap - i < LongestSeparator &&
                   sample[gap] != '[' && !char.IsLetterOrDigit(sample[gap])) gap++;
            if (gap >= sample.Length || sample[gap] != '[') break;   // the header is over
            if (gap > i) sb.Append(Escape(sample[i..gap]));
            i = gap;
        }

        if (found == 0) return "";

        // The spaces between the last group and the message are a SEPARATOR, so they are written outside
        // the message's braces: inside them they would be part of the field and would travel with it, and
        // a message carried to the front of the row would push every line one space in.
        int from = i;
        while (i < sample.Length && sample[i] == ' ') i++;
        if (i < sample.Length || i > from) sb.Append(Escape(sample[from..i])).Append("{*}");
        return sb.ToString();
    }

    /// <summary>Makes literal text safe to drop into a template.</summary>
    public static string Escape(string text)
    {
        if (text is null) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '{' or '}' or '*' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
