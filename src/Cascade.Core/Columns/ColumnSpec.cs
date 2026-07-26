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
    public ColumnAlign Align { get; set; } = ColumnAlign.Left;

    public ColumnDef Clone() => new() { Name = Name, Visible = Visible, Width = Width, Align = Align };
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
        var existing = Columns.ToDictionary(c => c.Name, c => c);
        Columns.Clear();
        foreach (var n in names)
            Columns.Add(existing.TryGetValue(n, out var e) ? e : new ColumnDef { Name = n });
    }
}
