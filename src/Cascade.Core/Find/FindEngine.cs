using System.Text.RegularExpressions;
using Cascade.Core.IO;
using Cascade.Core.Indexing;

namespace Cascade.Core.Find;

public sealed record FindQuery(string Text, bool Regex, bool CaseSensitive);

/// <summary>Sequential text search across file lines (literal or regular expression), independent of
/// the filter view. Runs on a caller-supplied thread and is cancellable.</summary>
public static class FindEngine
{
    /// <summary>Finds the next/previous line (relative to <paramref name="startLine"/>, inclusive)
    /// that matches. Returns the line number, or -1 if none before hitting an end.</summary>
    public static long Find(LineReader reader, LineIndex index, long fileLength, long lineCount,
        FindQuery query, long startLine, bool forward, CancellationToken ct, Action<double>? onProgress = null)
    {
        if (lineCount <= 0) return -1;
        if (Compile(query) is not var (rx, cmp)) return -1;

        long line = Math.Clamp(startLine, 0, lineCount - 1);
        int step = forward ? 1 : -1;
        long total = Math.Max(1, forward ? lineCount - line : line + 1);
        long scanned = 0;
        for (; line >= 0 && line < lineCount; line += step, scanned++)
        {
            if ((scanned & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if ((scanned & 0xFFFF) == 0) onProgress?.Invoke(Math.Min(1.0, (double)scanned / total));
            if (IsHit(LineSpan(reader, index, fileLength, line), query, rx, cmp)) return line;
        }
        return -1;
    }

    /// <summary>Like <see cref="Find"/> but restricted to a filtered view: it walks display <b>rows</b>
    /// (each mapped to a file line via <paramref name="rowToLine"/>) so the hit is always a visible line.
    /// Returns the matching file line, or -1.</summary>
    public static long FindInRows(LineReader reader, LineIndex index, long fileLength, long rowCount,
        Func<long, long> rowToLine, FindQuery query, long startRow, bool forward, CancellationToken ct, Action<double>? onProgress = null)
    {
        if (rowCount <= 0) return -1;
        if (Compile(query) is not var (rx, cmp)) return -1;

        long row = Math.Clamp(startRow, 0, rowCount - 1);
        int step = forward ? 1 : -1;
        long total = Math.Max(1, forward ? rowCount - row : row + 1);
        long scanned = 0;
        for (; row >= 0 && row < rowCount; row += step, scanned++)
        {
            if ((scanned & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if ((scanned & 0xFFFF) == 0) onProgress?.Invoke(Math.Min(1.0, (double)scanned / total));
            long line = rowToLine(row);
            if (IsHit(LineSpan(reader, index, fileLength, line), query, rx, cmp)) return line;
        }
        return -1;
    }

    private static ReadOnlySpan<char> LineSpan(LineReader reader, LineIndex index, long fileLength, long line)
    {
        long s = index.Get(line);
        long e = (line + 1 < index.Count) ? index.Get(line + 1) : fileLength;
        return reader.GetChars(s, e);
    }

    private static bool IsHit(ReadOnlySpan<char> span, FindQuery query, Regex? rx, StringComparison cmp)
        => rx is not null ? rx.IsMatch(span) : span.Contains(query.Text, cmp);

    /// <summary>One query, ready to test lines against. Not shared between threads: a Regex hands out a
    /// single cached runner, so sharing one across a parallel scan makes every thread but one allocate.</summary>
    public sealed class FindMatcher
    {
        private readonly Regex? _rx;
        private readonly string _text;
        private readonly StringComparison _cmp;

        internal FindMatcher(Regex? rx, string text, StringComparison cmp) { _rx = rx; _text = text; _cmp = cmp; }

        public bool Matches(ReadOnlySpan<char> line)
            => _rx is not null ? _rx.IsMatch(line) : line.Contains(_text, _cmp);

        /// <summary>The next occurrence at or after <paramref name="start"/>, for highlighting every hit on
        /// a line rather than just knowing there is one. A zero-length regex match would otherwise stand
        /// still for ever, so it reports one character and the caller moves past it.</summary>
        public bool NextMatch(ReadOnlySpan<char> line, int start, out int at, out int length)
        {
            at = -1;
            length = 0;
            if (start < 0) start = 0;
            if (start > line.Length) return false;

            if (_rx is not null)
            {
                foreach (var m in _rx.EnumerateMatches(line[start..]))
                {
                    at = start + m.Index;
                    length = Math.Max(1, m.Length);
                    return true;
                }
                return false;
            }

            if (_text.Length == 0) return false;
            int found = line[start..].IndexOf(_text, _cmp);
            if (found < 0) return false;
            at = start + found;
            length = _text.Length;
            return true;
        }

        /// <summary>How many times this matches in a line. Occurrences, not lines: a line with three hits
        /// counts three.</summary>
        public int CountIn(ReadOnlySpan<char> line)
        {
            int n = 0, from = 0;
            while (NextMatch(line, from, out int at, out int len)) { n++; from = at + Math.Max(1, len); }
            return n;
        }
    }

    /// <summary>Compiles a query, or returns null when it can never match anything: an empty term, or a
    /// regular expression that will not parse. Both mean "not found" rather than an error.</summary>
    public static FindMatcher? CompileQuery(FindQuery query)
        => Compile(query) is var (rx, cmp) ? new FindMatcher(rx, query.Text, cmp) : null;

    /// <summary>Builds the regex (if any) and comparison for a query, or returns null for an empty /
    /// invalid-regex query (which matches nothing).</summary>
    private static (Regex? Rx, StringComparison Cmp)? Compile(FindQuery query)
    {
        if (string.IsNullOrEmpty(query.Text)) return null;
        Regex? rx = null;
        if (query.Regex)
        {
            var opts = RegexOptions.CultureInvariant;
            if (!query.CaseSensitive) opts |= RegexOptions.IgnoreCase;
            try { rx = new Regex(query.Text, opts); }
            catch (ArgumentException) { return null; }
        }
        return (rx, query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
