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
/// <item><c>.</c> is any one character, whatever it happens to be.</item>
/// <item><c>{ }</c> wrap a PART - the thing that is hidden or carried about, its punctuation with it.</item>
/// <item>Anything else has to be there as written, except a run of spaces, which matches any run of
/// spaces so that padded fields need not be counted out.</item>
/// <item><c>\</c> escapes <c>{ } * . \</c>.</item>
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
/// character it wanted it at. A <c>.</c> is a fixed WIDTH rather than a fixed character, so it costs the
/// scan nothing: a run is still found by searching for the fixed text in it and stepping back over
/// however many characters the dots before it stand for.</para>
/// </summary>
public sealed class LineTemplate
{
    /// <summary>What a piece of a run asks of the line: text that has to be there, a run of spaces of any
    /// length, or a fixed number of characters of no particular kind.</summary>
    private enum PieceKind { Literal, Spaces, Any }

    private readonly struct Piece(string text, PieceKind kind, int part)
    {
        /// <summary>The text to match; for <see cref="PieceKind.Any"/> the dots themselves, whose LENGTH is
        /// what is asked of the line and whose text is what a failure quotes back.</summary>
        public readonly string Text = text;
        public readonly PieceKind Kind = kind;
        public readonly int Part = part;
        public bool SpaceRun => Kind == PieceKind.Spaces;
    }

    private sealed class Run
    {
        public Piece[] Pieces = [];
        public string Display = "";
        public bool IsEmpty => Pieces.Length == 0;

        /// <summary>The piece this run is FOUND by - the first one that asks for anything in particular -
        /// and how many characters of the run come before it. Everything ahead of it is dots, which are of a
        /// fixed width, so finding it and stepping back lands exactly where the run must begin. -1 when the
        /// run is nothing but dots, and any position with the characters to spare will do.</summary>
        public int AnchorAt = -1;
        public int AnchorOffset;
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

    /// <summary>Which captured value belongs to a part, or -1 when that part captures nothing - a piece of
    /// fixed text is a part too, and has no text of its own to read.</summary>
    public int ValueOfPart(int part)
    {
        for (int v = 0; v < _valuePart.Length; v++) if (_valuePart[v] == part) return v;
        return -1;
    }

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

        private readonly List<(string Text, int Part, UnitKind Kind)> _units = [];

        /// <summary>What one unit of the parsed template is: text to match, a captured value, or a run of
        /// dots standing for that many characters of any kind.</summary>
        private enum UnitKind { Literal, Value, Any }

        public void Run()
        {
            var sb = new StringBuilder();
            int i = 0, part = -1, partStart = 0, partValue = -1;
            int literalPart = -1;

            void Flush()
            {
                if (sb.Length == 0) return;
                _units.Add((sb.ToString(), literalPart, UnitKind.Literal));
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
                        _units.Add(("", part, UnitKind.Value));
                        i++;
                        break;

                    // A dot stands for one character the template does not care about. Anywhere a literal
                    // may go, so it belongs to whatever part is open exactly as a literal would, and a run
                    // of them is kept together as one unit - what matters about it is only how many.
                    case '.':
                        Flush();
                        int dots = i;
                        while (dots < template.Length && template[dots] == '.') dots++;
                        _units.Add((template[i..dots], part, UnitKind.Any));
                        i = dots;
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
                var run = new Run { Pieces = pieces.ToArray(), Display = display.ToString() };
                // Where the run can be FOUND from: the first piece that asks for anything in particular,
                // with the dots ahead of it counted so the search can step back over them.
                int ahead = 0;
                for (int k = 0; k < run.Pieces.Length; k++)
                {
                    if (run.Pieces[k].Kind != PieceKind.Any) { run.AnchorAt = k; run.AnchorOffset = ahead; break; }
                    ahead += run.Pieces[k].Text.Length;
                }
                runs.Add(run);
                pieces.Clear();
                display.Clear();
            }

            foreach (var (text, unitPart, kind) in _units)
            {
                if (kind == UnitKind.Value) { Close(); continue; }
                display.Append(text);
                if (kind == UnitKind.Any) { pieces.Add(new Piece(text, PieceKind.Any, unitPart)); continue; }
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
                        pieces.Add(new Piece(text[from..k], PieceKind.Spaces, unitPart));
                    }
                    else
                    {
                        int from = k;
                        while (k < text.Length && text[k] != ' ') k++;
                        pieces.Add(new Piece(text[from..k], PieceKind.Literal, unitPart));
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
            // Literal first: it is what almost every piece of almost every template is, and this runs for
            // every row of every frame.
            if (piece.Kind == PieceKind.Literal)
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
            else if (piece.Kind == PieceKind.Spaces)
            {
                int n = 0;
                while (p < line.Length && line[p] == ' ') { p++; n++; }
                if (n == 0) { consumed = 0; failAt = p; failWant = " "; return false; }
            }
            else
            {
                // Any characters will do, so the only way this fails is the line running out under it.
                if (p + piece.Text.Length > line.Length)
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
            if (piece.Kind == PieceKind.Spaces) while (p < line.Length && line[p] == ' ') p++;
            else p += piece.Text.Length;
            into.Touch(piece.Part, from, p);
        }
    }

    /// <summary>Finds the run at or after <paramref name="from"/>. When it is nowhere to be found the
    /// failure reported is from the attempt that got FURTHEST, which is where the line stopped looking like
    /// the template and so the place worth pointing at.</summary>
    private static int Find(string line, int from, Run run, out int length, out int failAt, out string failWant)
    {
        // What is searched for, and how far into the run it sits. Dots ahead of it are a fixed width, so a
        // hit is stepped back over them to where the run itself would have to start - which is what keeps a
        // template that opens a run with dots as cheap to match as any other.
        int ahead = run.AnchorOffset;
        var anchor = run.AnchorAt >= 0 ? run.Pieces[run.AnchorAt] : default;
        bool spaces = run.AnchorAt >= 0 && anchor.SpaceRun;
        int bestAt = -1;
        string bestWant = "";

        int i = from;
        while (i <= line.Length)
        {
            if (run.AnchorAt < 0)
            {
                // Nothing but dots: any character will do, so this position is as good as the next.
            }
            else if (!spaces)
            {
                int at = line.IndexOf(anchor.Text, Math.Min(line.Length, i + ahead), StringComparison.Ordinal);
                if (at < 0) break;
                i = at - ahead;
            }
            else
            {
                int at = i + ahead;
                if (at > line.Length) break;
                while (at < line.Length && line[at] != ' ') at++;
                if (at >= line.Length) break;
                i = at - ahead;
            }

            if (TryRun(line, i, run, out length, out int a, out string w)) { failAt = -1; failWant = ""; return i; }
            if (a > bestAt) { bestAt = a; bestWant = w; }

            // Past the whole run of spaces, not just one of them: see the remark above.
            if (spaces)
            {
                int at = i + ahead;
                while (at < line.Length && line[at] == ' ') at++;
                i = at - ahead;
            }
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

    /// <summary>Reads the header off the front of a line and writes a template for it: an opening field that
    /// is not bracketed at all, then a run of bracketed groups, then the message. This is what makes turning
    /// fields on a single click for the log formats that have a header worth reading.
    ///
    /// <para>Three bracket shapes count - <c>[ ]</c>, <c>( )</c> and <c>&lt; &gt;</c> - and a line may mix
    /// them. Whatever separates two groups is kept.</para>
    ///
    /// <para>It stops at the message. What tells the two apart is what lies BETWEEN two groups: a short run
    /// of punctuation or spaces still counts as a separator, but the moment a letter or a digit turns up
    /// the header is over - otherwise a message carrying a bracket of its own, which any log full of JSON
    /// does, drags half the line into the template.</para>
    ///
    /// <para>What it will NOT read is a header held together by nothing but spaces, where where one field
    /// ends and the next begins is a guess rather than a reading. Those are written by hand.</para></summary>
    public static string Detect(string sample)
    {
        if (string.IsNullOrEmpty(sample)) return "";

        // Long enough for "] [" or ") - (", short enough that a sentence cannot pass for a separator.
        const int LongestSeparator = 4;

        var sb = new StringBuilder();
        int i = LeadInLength(sample);
        int found = 0;
        if (i > 0)
        {
            // The lead-in is a field, and the spaces after it are a separator between that field and the
            // next - written outside the braces so they stay behind if the field is carried elsewhere.
            int lead = i;
            while (lead > 0 && sample[lead - 1] == ' ') lead--;
            sb.Append("{*}").Append(Escape(sample[lead..i]));
            found++;
        }

        while (i < sample.Length && Opener(sample[i]) is { } shut)
        {
            int close = sample.IndexOf(shut, i + 1);
            if (close < 0) break;
            sb.Append('{').Append(Escape(sample[i].ToString())).Append('*').Append(Escape(shut.ToString())).Append('}');
            found++;
            i = close + 1;

            int gap = i;
            while (gap < sample.Length && gap - i < LongestSeparator &&
                   Opener(sample[gap]) is null && !char.IsLetterOrDigit(sample[gap])) gap++;
            if (gap >= sample.Length || Opener(sample[gap]) is null) break;   // the header is over
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

    /// <summary>The bracket that closes the one a header group opens with, or null for anything else.</summary>
    private static char? Opener(char c) => c switch { '[' => ']', '(' => ')', '<' => '>', _ => null };

    /// <summary>How much of a line comes before its first bracketed group and can be read as a field in its
    /// own right - the timestamp in <c>2026-08-05 12:00:00 [INFO] ...</c> and nothing more adventurous.
    ///
    /// <para>A header field is a VALUE, so it is allowed one space in it and no more: that is enough for a
    /// date beside a time, and not enough for the opening words of a sentence that happens to have a bracket
    /// somewhere further along. Zero if there is nothing to take, which is the usual answer.</para></summary>
    private static int LeadInLength(string sample)
    {
        const int Longest = 64;
        if (sample.Length == 0 || Opener(sample[0]) is not null) return 0;

        int at = -1, spaces = 0;
        for (int i = 0; i < sample.Length && i < Longest; i++)
        {
            if (Opener(sample[i]) is not null) { at = i; break; }
            if (sample[i] == ' ' && ++spaces > 2) return 0;
        }
        // A group has to follow it, the lead-in itself has to be something, and the two have to be parted by
        // a space - "foo[bar]" is one value with a bracket in it, not a field and a group.
        if (at <= 0 || sample[at - 1] != ' ') return 0;
        int text = at;
        while (text > 0 && sample[text - 1] == ' ') text--;
        return text == 0 ? 0 : at;
    }

    /// <summary>Makes literal text safe to drop into a template.</summary>
    public static string Escape(string text)
    {
        if (text is null) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '{' or '}' or '*' or '.' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
