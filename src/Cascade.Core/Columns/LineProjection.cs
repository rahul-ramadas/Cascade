using System.Text;

namespace Cascade.Core.Columns;

/// <summary>A stretch of the projected line: text taken from the line, or - when two parts that are not
/// neighbours end up side by side - text the projection had to invent.</summary>
public readonly record struct ProjectedSpan(int Start, int Length, int LineStart, int Part, bool Invented);

/// <summary>
/// The line as the Inline layout shows it: the hidden parts left out, the rest in the order they are
/// listed. Owned and reused by the caller, because the paint builds one per row per frame.
///
/// <para>What goes BETWEEN two parts is the text that separates them in the line - taken from the line
/// itself, so padding and punctuation are whatever the log actually had. Only carrying a part BACKWARDS
/// leaves no such text, and there a single joiner goes in - but ONLY where nothing already separates the
/// two. A bracket or a quote on either side does, so <c>[a][b]</c> is left flush, exactly as a bracketed
/// line reads; a space invented there is one the reader can see is not in the file.</para>
///
/// <para>Some separator is needed at all because there are only N-1 separators for N fields: whichever
/// field came first in the line has nothing in front of it, so any order that moves it away from the front
/// leaves two fields with nothing between them, however the template was written.</para>
///
/// <para>A row never BEGINS with a blank for the same reason: a field carried to the front would otherwise
/// take the separator in front of it along, and the lines the template does not match - which are shown
/// whole - would no longer start in the same column.</para>
///
/// <para>The span map is the important part: everything downstream - the marks, the selection, the hit
/// test, the clipboard - works in DISPLAY coordinates, and <see cref="ToLine"/> and <see cref="FromLine"/>
/// are what tie those back to the raw line the filters and the search still run on.</para>
/// </summary>
public sealed class LineProjection
{
    private readonly StringBuilder _text = new();
    private readonly List<ProjectedSpan> _spans = [];
    private string _built = "";

    /// <summary>What goes between two parts that were never neighbours.</summary>
    public const string Joiner = " ";

    public string Text => _built;
    public IReadOnlyList<ProjectedSpan> Spans => _spans;

    /// <summary>True when the projection is the line unchanged, which is the common case and lets the
    /// caller skip the mapping entirely.</summary>
    public bool IsWholeLine { get; private set; }

    /// <summary>Builds the projection of <paramref name="line"/>. When the line does not fit the template
    /// it is shown whole and untouched - a template can shorten a line, never hide one.</summary>
    public void Build(string line, ColumnSpec spec, TemplateMatch match)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(match);

        _text.Clear();
        _spans.Clear();

        if (!match.Success || match.PartCount == 0)
        {
            _built = line;
            _spans.Add(new ProjectedSpan(0, line.Length, 0, -1, false));
            IsWholeLine = true;
            return;
        }

        int previous = -1;
        foreach (var column in spec.Columns)
        {
            int source = column.Source;
            if (!column.Visible || source < 0 || source >= match.PartCount) continue;

            var (start, length) = match.Part(source);
            if (previous < 0) { if (source == 0) TakeGapBefore(line, match, 0); }
            else if (source > previous) TakeGapBefore(line, match, source);
            else if (NeedsJoiner(line, start, length)) Invent(Joiner);

            Take(line, start, length, source);
            previous = source;
        }

        // Whatever the template never reached is data, not punctuation, so it is never dropped. When the
        // last part shown is also the last part there is, whatever follows it comes along in one piece -
        // that way a trailing literal in the template is not lost either.
        if (previous == match.PartCount - 1)
        {
            var (start, length) = match.Part(previous);
            Take(line, start + length, line.Length - (start + length), -1);
        }
        else Take(line, match.TailStart, line.Length - match.TailStart, -1);

        // Nothing actually left out: the answer IS the line, so hand back the line rather than building a
        // copy of it. This is the ordinary state - fields on, nothing hidden - and the paint asks for one
        // of these per row per frame, so it is the allocation worth not making.
        IsWholeLine = CoversWholeLine(line);
        if (!IsWholeLine) DropLeadingBlank();
        _built = IsWholeLine ? line : _text.ToString();
    }

    /// <summary>Whether a single space has to go in between what has been written and the field about to be.
    ///
    /// <para>The joiner exists for one reason: to stop two fields that were never neighbours running
    /// together into something that reads as one word. A bracket or a quote on EITHER side of the join is
    /// already doing that job - <c>[a][b]</c> is exactly how a bracketed line reads, and so is
    /// <c>text[a]</c> - so nothing is invented there.</para>
    ///
    /// <para>Not just any punctuation, though: <c>.</c> <c>-</c> <c>:</c> and their like JOIN rather than
    /// separate, and <c>hello</c> against <c>.5ms</c> would read as one token. Only the characters that
    /// open or close something count.</para>
    ///
    /// <para>Nor does a joiner go in where either side is already blank, which would double the
    /// separator.</para></summary>
    private bool NeedsJoiner(string line, int start, int length)
    {
        if (_text.Length == 0) return false;
        char left = _text[^1];
        if (IsBlank(left)) return false;
        if (length <= 0 || start < 0 || start >= line.Length) return false;
        char right = line[start];
        if (IsBlank(right)) return false;
        return !Closes(left) && !Opens(right);
    }

    /// <summary>Characters that end something, and so separate it from whatever follows.</summary>
    private static bool Closes(char c) => c is ')' or ']' or '}' or '>' or '"' or '\'';

    /// <summary>...and the ones that begin something.</summary>
    private static bool Opens(char c) => c is '(' or '[' or '{' or '<' or '"' or '\'';

    private static bool IsBlank(char c) => c is ' ' or '\t';

    /// <summary>Takes off the run of blanks a row would otherwise BEGIN with, and the spans with it. A
    /// field's own text can start with the space that separated it from what came before it - carry that
    /// field to the front of the row and every line would start one space in, out of line with the lines
    /// the template does not match. Only ever done to a row that already differs from the file's line.</summary>
    private void DropLeadingBlank()
    {
        int blank = 0;
        while (blank < _text.Length && IsBlank(_text[blank])) blank++;
        if (blank == 0) return;

        _text.Remove(0, blank);
        int at = 0;
        while (at < _spans.Count)
        {
            var span = _spans[at];
            if (span.Start + span.Length <= blank) { _spans.RemoveAt(at); continue; }
            int cut = Math.Max(0, blank - span.Start);
            _spans[at] = new ProjectedSpan(Math.Max(0, span.Start - blank), span.Length - cut,
                                           span.Invented ? -1 : span.LineStart + cut, span.Part, span.Invented);
            at++;
        }
    }

    /// <summary>Whether the spans, in order, are exactly the line and nothing else.</summary>
    private bool CoversWholeLine(string line)
    {
        int at = 0;
        foreach (var span in _spans)
        {
            if (span.Invented || span.LineStart != at) return false;
            at += span.Length;
        }
        return at == line.Length;
    }

    /// <summary>The text that comes before a part: from wherever the part before it ended to where it
    /// starts. Only ever the one gap, so a run of hidden parts closes up to a single separator rather than
    /// leaving one behind for each.</summary>
    private void TakeGapBefore(string line, TemplateMatch match, int part)
    {
        int from = 0;
        if (part > 0)
        {
            var (previousStart, previousLength) = match.Part(part - 1);
            from = previousStart + previousLength;
        }
        var (start, _) = match.Part(part);
        Take(line, from, start - from, -1);
    }

    private void Take(string line, int start, int length, int part)
    {
        if (length <= 0 || start < 0 || start >= line.Length) return;
        length = Math.Min(length, line.Length - start);
        _spans.Add(new ProjectedSpan(_text.Length, length, start, part, false));
        _text.Append(line, start, length);
    }

    private void Invent(string text)
    {
        if (text.Length == 0) return;
        _spans.Add(new ProjectedSpan(_text.Length, text.Length, -1, -1, true));
        _text.Append(text);
    }

    /// <summary>Where a character of the projected line came from, or -1 when it was invented and so
    /// belongs to no part of the file.</summary>
    public int ToLine(int index)
    {
        foreach (var span in _spans)
        {
            if (index < span.Start || index >= span.Start + span.Length) continue;
            return span.Invented ? -1 : span.LineStart + (index - span.Start);
        }
        // The very end of the line is a valid caret position, and belongs to the last span.
        if (index == _built.Length && _spans.Count > 0)
        {
            var last = _spans[^1];
            if (!last.Invented) return last.LineStart + last.Length;
        }
        return -1;
    }

    /// <summary>Where a character of the raw line ended up, or -1 when it is not shown.</summary>
    public int FromLine(int index)
    {
        foreach (var span in _spans)
        {
            if (span.Invented) continue;
            if (index < span.LineStart || index >= span.LineStart + span.Length) continue;
            return span.Start + (index - span.LineStart);
        }
        return -1;
    }

    /// <summary>Whether a stretch of the raw line survives into the projection in one piece. A selection
    /// that does not is one whose text appears in no line of the file, which is what a filter or a search
    /// made from it has to be warned about.</summary>
    public bool IsContiguous(int lineStart, int lineEnd)
    {
        if (lineEnd <= lineStart) return true;
        int first = FromLine(lineStart);
        if (first < 0) return false;
        for (int i = lineStart + 1; i < lineEnd; i++)
        {
            int at = FromLine(i);
            if (at != first + (i - lineStart)) return false;
        }
        return true;
    }
}
