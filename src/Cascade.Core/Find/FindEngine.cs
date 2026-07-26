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
