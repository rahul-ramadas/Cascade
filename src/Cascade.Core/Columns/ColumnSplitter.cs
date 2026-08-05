using System.Text.RegularExpressions;

namespace Cascade.Core.Columns;

/// <summary>A resolved column value: the display name and its [start,length) range within the line.</summary>
public readonly record struct ColumnValue(string Name, int Start, int Length);

/// <summary>Splits a line into column ranges per a <see cref="ColumnSpec"/>. Used only for the handful
/// of lines currently on screen, so working with strings here is cheap and keeps the code simple.
///
/// The values come out in FIELD order - the order the line splits in - which is NOT the order the columns
/// are drawn in. A column says which field it shows through <see cref="ColumnDef.Source"/>, so carrying a
/// header to another place moves its data with it instead of relabelling the fields.</summary>
public sealed class ColumnSplitter
{
    public ColumnSpec Spec { get; }
    private readonly Regex? _template;
    private readonly List<string> _fieldNames;

    public ColumnSplitter(ColumnSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Spec = spec;
        _template = spec.CompileTemplate();
        _fieldNames = spec.Mode == ColumnSplitMode.Template
            ? ColumnSpec.BuildTemplate(spec.Template).Names
            : [];
    }

    /// <summary>Splits <paramref name="line"/> into <paramref name="output"/>. Returns false if the
    /// line did not match a template (caller shows it as a single cell).</summary>
    public bool Split(string line, List<ColumnValue> output)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        return Spec.Mode == ColumnSplitMode.Template
            ? SplitTemplate(line, output)
            : SplitDelimiter(line, output);
    }

    private bool SplitTemplate(string line, List<ColumnValue> output)
    {
        if (_template is null) { output.Add(new ColumnValue("", 0, line.Length)); return false; }
        var m = _template.Match(line);
        if (!m.Success) { output.Add(new ColumnValue("", 0, line.Length)); return false; }

        for (int i = 0; i < _fieldNames.Count; i++)
        {
            var g = m.Groups[i + 1];
            output.Add(new ColumnValue(NameOfField(i, _fieldNames[i]), g.Success ? g.Index : 0, g.Success ? g.Length : 0));
        }
        return true;
    }

    private bool SplitDelimiter(string line, List<ColumnValue> output)
    {
        string delim = Spec.Delimiter;
        if (string.IsNullOrEmpty(delim)) { output.Add(new ColumnValue(NameOfField(0, "Col 1"), 0, line.Length)); return true; }

        int pos = 0, col = 0;
        while (pos <= line.Length)
        {
            if (Spec.MaxSplits > 0 && col == Spec.MaxSplits - 1)
            {
                AddField(output, col, pos, line.Length - pos);
                return true;
            }

            int next = line.IndexOf(delim, pos, StringComparison.Ordinal);
            if (next < 0)
            {
                AddField(output, col, pos, line.Length - pos);
                return true;
            }

            int len = next - pos;
            if (!(Spec.CollapseConsecutive && len == 0))
            {
                AddField(output, col, pos, len);
                col++;
            }
            pos = next + delim.Length;
        }
        return true;
    }

    private void AddField(List<ColumnValue> output, int field, int start, int len)
        => output.Add(new ColumnValue(NameOfField(field, $"Col {field + 1}"), start, Math.Max(0, len)));

    /// <summary>What to call a field: the name of whichever column shows it, wherever that column has been
    /// carried to, and a positional name when no column has claimed it.</summary>
    private string NameOfField(int field, string fallback)
    {
        foreach (var c in Spec.Columns)
            if (c.Source == field && c.Name.Length > 0) return c.Name;
        return fallback;
    }
}
