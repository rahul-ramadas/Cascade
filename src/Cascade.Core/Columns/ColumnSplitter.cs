using System.Text.RegularExpressions;

namespace Cascade.Core.Columns;

/// <summary>A resolved column value: the display name and its [start,length) range within the line.</summary>
public readonly record struct ColumnValue(string Name, int Start, int Length);

/// <summary>Splits a line into column ranges per a <see cref="ColumnSpec"/>. Used only for the handful
/// of lines currently on screen, so working with strings here is cheap and keeps the code simple.</summary>
public sealed class ColumnSplitter
{
    public ColumnSpec Spec { get; }
    private readonly Regex? _template;

    public ColumnSplitter(ColumnSpec spec)
    {
        Spec = spec;
        _template = spec.CompileTemplate();
    }

    /// <summary>Splits <paramref name="line"/> into <paramref name="output"/>. Returns false if the
    /// line did not match a template (caller shows it as a single cell).</summary>
    public bool Split(string line, List<ColumnValue> output)
    {
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

        for (int i = 0; i < Spec.Columns.Count; i++)
        {
            var g = m.Groups[i + 1];
            output.Add(new ColumnValue(Spec.Columns[i].Name, g.Success ? g.Index : 0, g.Success ? g.Length : 0));
        }
        return true;
    }

    private bool SplitDelimiter(string line, List<ColumnValue> output)
    {
        string delim = Spec.Delimiter;
        if (string.IsNullOrEmpty(delim)) { output.Add(new ColumnValue("Col 1", 0, line.Length)); return true; }

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

    private void AddField(List<ColumnValue> output, int col, int start, int len)
    {
        string name = col < Spec.Columns.Count ? Spec.Columns[col].Name : $"Col {col + 1}";
        output.Add(new ColumnValue(name, start, Math.Max(0, len)));
    }
}
