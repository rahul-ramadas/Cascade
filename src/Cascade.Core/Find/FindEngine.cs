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
        FindQuery query, long startLine, bool forward, CancellationToken ct)
    {
        if (lineCount <= 0 || string.IsNullOrEmpty(query.Text)) return -1;

        Regex? rx = null;
        if (query.Regex)
        {
            var opts = RegexOptions.CultureInvariant;
            if (!query.CaseSensitive) opts |= RegexOptions.IgnoreCase;
            try { rx = new Regex(query.Text, opts); }
            catch (ArgumentException) { return -1; }
        }
        var cmp = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        long line = Math.Clamp(startLine, 0, lineCount - 1);
        int step = forward ? 1 : -1;
        for (; line >= 0 && line < lineCount; line += step)
        {
            if ((line & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            long s = index.Get(line);
            long e = (line + 1 < index.Count) ? index.Get(line + 1) : fileLength;
            var span = reader.GetChars(s, e);

            bool hit = rx is not null ? rx.IsMatch(span) : span.Contains(query.Text, cmp);
            if (hit) return line;
        }
        return -1;
    }
}
