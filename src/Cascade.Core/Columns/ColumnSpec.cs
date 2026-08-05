using System.Text;
using System.Text.RegularExpressions;

namespace Cascade.Core.Columns;

public enum ColumnSplitMode { Delimiter, Template }

public enum ColumnAlign { Left, Right, Center }

public sealed class ColumnDef
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public int Width { get; set; } // display width in pixels; 0 = auto

    /// <summary>Which field of the split line this column shows, counted from 0 in the order the line is
    /// split. Kept apart from the column's place in the list, because that place is only where the column
    /// is DRAWN - carrying a header about has to move the data with it, not relabel the fields.
    /// -1 means "not decided yet"; see <see cref="ColumnSpec.NormalizeSources"/>.</summary>
    public int Source { get; set; } = -1;

    /// <summary>Width in characters, which is what a width means when the log is being read in a
    /// fixed-pitch font: zooming then keeps the same fields visible instead of clipping them. Takes
    /// precedence over <see cref="Width"/> while such a font is in use; 0 leaves the pixel width to speak.</summary>
    public int WidthChars { get; set; }
    public ColumnAlign Align { get; set; } = ColumnAlign.Left;

    public ColumnDef Clone() => new() { Name = Name, Visible = Visible, Width = Width, WidthChars = WidthChars, Align = Align, Source = Source };
}

/// <summary>
/// Defines how each line is split into columns for display. Two modes: a single delimiter, or a
/// bracket <see cref="Template"/> such as <c>[time] [level] [message]</c> (each <c>[name]</c> is a
/// column; literal brackets in the data are matched with <c>[[name]]</c>). Splitting is display-only
/// and never affects filtering, which always runs on the whole raw line.
/// </summary>
public sealed class ColumnSpec
{
    public bool Enabled { get; set; }
    public ColumnSplitMode Mode { get; set; } = ColumnSplitMode.Delimiter;

    public string Delimiter { get; set; } = "\t";
    public bool CollapseConsecutive { get; set; }
    public int MaxSplits { get; set; } // 0 = unlimited

    public string Template { get; set; } = "";

    public List<ColumnDef> Columns { get; } = new();

    public ColumnSpec Clone()
    {
        var c = new ColumnSpec
        {
            Enabled = Enabled,
            Mode = Mode,
            Delimiter = Delimiter,
            CollapseConsecutive = CollapseConsecutive,
            MaxSplits = MaxSplits,
            Template = Template
        };
        foreach (var col in Columns) c.Columns.Add(col.Clone());
        return c;
    }

    /// <summary>Parses a bracket template into ordered column display-names and an anchored regex
    /// (using numbered groups). Each <c>[name]</c> becomes a non-greedy group except the last, which
    /// is greedy; anything else (including literal brackets) is a literal separator.</summary>
    public static (List<string> Names, string Pattern) BuildTemplate(string template)
    {
        var names = new List<string>();
        var tokens = new List<(bool placeholder, string text)>();

        int i = 0;
        while (i < template.Length)
        {
            char ch = template[i];
            if (ch == '[')
            {
                int j = i + 1;
                while (j < template.Length &&
                       (char.IsLetterOrDigit(template[j]) || template[j] == '_' || template[j] == ' '))
                    j++;
                if (j < template.Length && template[j] == ']' && j > i + 1)
                {
                    string name = template.Substring(i + 1, j - i - 1).Trim();
                    if (name.Length > 0)
                    {
                        tokens.Add((true, name));
                        names.Add(name);
                        i = j + 1;
                        continue;
                    }
                }
                tokens.Add((false, "["));
                i++;
            }
            else
            {
                tokens.Add((false, ch.ToString()));
                i++;
            }
        }

        int lastPlaceholder = -1;
        for (int k = 0; k < tokens.Count; k++) if (tokens[k].placeholder) lastPlaceholder = k;

        var sb = new StringBuilder("^");
        for (int k = 0; k < tokens.Count; k++)
        {
            var t = tokens[k];
            if (t.placeholder) sb.Append(k == lastPlaceholder ? "(.*)" : "(.*?)");
            else sb.Append(Regex.Escape(t.text));
        }
        return (names, sb.ToString());
    }

    public Regex? CompileTemplate()
    {
        if (Mode != ColumnSplitMode.Template || string.IsNullOrEmpty(Template)) return null;
        var (_, pattern) = BuildTemplate(Template);
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Populates <see cref="Columns"/> from the template's placeholder names.</summary>
    public void SyncColumnsFromTemplate()
    {
        var (names, _) = BuildTemplate(Template);
        // First one wins rather than throwing: nothing stops two columns being renamed the same thing.
        var existing = new Dictionary<string, ColumnDef>(StringComparer.Ordinal);
        foreach (var c in Columns) existing.TryAdd(c.Name, c);
        Columns.Clear();
        foreach (var n in names)
            Columns.Add(existing.TryGetValue(n, out var e) ? e : new ColumnDef { Name = n });
        // Reading the template again is starting over, so every column shows the field it was written for.
        for (int i = 0; i < Columns.Count; i++) Columns[i].Source = i;
    }

    /// <summary>Settles which field each column shows for any that has not been told. Columns written
    /// before the two were separate - and any file saved by such a build - show the field at their own
    /// place in the list, which is exactly what they used to do.</summary>
    public void NormalizeSources()
    {
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].Source < 0) Columns[i].Source = i;
    }
}
