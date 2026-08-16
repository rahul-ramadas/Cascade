using System.Text;

namespace Cascade.Core.Columns;

/// <summary>
/// Reads the column settings written by builds before the template language, so a filter file saved by one
/// of them still opens with its columns intact.
///
/// <para>Two old shapes: a bracket template, where <c>[name]</c> was a field and <c>[[name]]</c> a field
/// wrapped in real brackets; and a single delimiter with a column apiece. Both become templates.</para>
/// </summary>
public static class LegacyColumns
{
    /// <summary>Punctuation that belongs to the field BEFORE it, so that hiding that field takes its
    /// closing bracket along.</summary>
    private const string Closing = ")]}>\"'";

    /// <summary>Punctuation that belongs to the field AFTER it, for the same reason.</summary>
    private const string Opening = "([{<\"'";

    /// <summary>
    /// Turns an old bracket template into the new one. Each field becomes a part, and the punctuation
    /// around it is drawn into that part - so <c>[[time]][[level]] [msg]</c> becomes
    /// <c>{[*]}{[*]} {*}</c> and hiding the level takes its brackets with it, which is the whole point of
    /// a part. Text that belongs to neither neighbour is left between them.
    /// </summary>
    public static string FromBracketTemplate(string template)
    {
        if (string.IsNullOrEmpty(template)) return "";

        // Same reading as the old parser: [name] is a field when the name is letters, digits, _ or space;
        // anything else, a lone [ included, is literal.
        var literals = new List<string>();     // literals[i] precedes field i; the last one trails
        var current = new StringBuilder();
        int fields = 0, i = 0;

        while (i < template.Length)
        {
            if (template[i] == '[')
            {
                int j = i + 1;
                while (j < template.Length &&
                       (char.IsLetterOrDigit(template[j]) || template[j] == '_' || template[j] == ' ')) j++;
                if (j < template.Length && template[j] == ']' && j > i + 1)
                {
                    literals.Add(current.ToString());
                    current.Clear();
                    fields++;
                    i = j + 1;
                    continue;
                }
            }
            current.Append(template[i]);
            i++;
        }
        literals.Add(current.ToString());
        if (fields == 0) return "";

        // Share each run of literal text out: its leading closing-punctuation goes to the field before it,
        // its trailing opening-punctuation to the field after, and the rest stays where it is.
        var before = new string[fields];
        var after = new string[fields];
        var between = new string[fields + 1];

        for (int k = 0; k <= fields; k++)
        {
            string run = literals[k];
            int head = 0;
            if (k > 0) while (head < run.Length && Closing.Contains(run[head], StringComparison.Ordinal)) head++;
            int tail = run.Length;
            if (k < fields) while (tail > head && Opening.Contains(run[tail - 1], StringComparison.Ordinal)) tail--;

            if (k > 0) after[k - 1] = run[..head];
            if (k < fields) before[k] = run[tail..];
            between[k] = run[head..tail];
        }

        var result = new StringBuilder();
        for (int k = 0; k < fields; k++)
        {
            result.Append(LineTemplate.Escape(between[k]));
            result.Append('{').Append(LineTemplate.Escape(before[k] ?? "")).Append('*')
                  .Append(LineTemplate.Escape(after[k] ?? "")).Append('}');
        }
        result.Append(LineTemplate.Escape(between[fields]));
        return result.ToString();
    }

    /// <summary>
    /// Turns an old single-delimiter split into a template. The number of columns saved with it says how
    /// many fields there were, since the delimiter alone does not.
    ///
    /// <para>Approximate in two corners the language no longer has: "collapse consecutive" is only kept for
    /// a space delimiter, where a run of spaces matches a run anyway, and a "max splits" cap is what the
    /// trailing field does by itself.</para>
    /// </summary>
    public static string FromDelimiter(string delimiter, int columns)
    {
        if (string.IsNullOrEmpty(delimiter) || columns <= 0) return "";
        string escaped = LineTemplate.Escape(delimiter);
        var result = new StringBuilder();
        for (int i = 0; i < columns - 1; i++) result.Append('{').Append('*').Append(escaped).Append('}');
        result.Append("{*}");
        return result.ToString();
    }
}
