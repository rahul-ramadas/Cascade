namespace Cascade.Core.Model;

/// <summary>The match configuration of a filter (its predicate). Maps 1:1 to the legacy <c>.tat</c>
/// attributes: a Text match with a <see cref="Regex"/> flag, or a Marker match.</summary>
public sealed class FilterMatch
{
    public FilterMatchType Type { get; set; } = FilterMatchType.Text;

    /// <summary>The substring or regular-expression pattern (for <see cref="MatchType.Text"/>).</summary>
    public string Text { get; set; } = "";

    public bool CaseSensitive { get; set; }

    /// <summary>When true (and Type is Text), <see cref="Text"/> is a .NET regular expression.</summary>
    public bool Regex { get; set; }

    /// <summary>Marker index 0..7 (for <see cref="MatchType.Marker"/>).</summary>
    public int MarkerIndex { get; set; } = -1;

    public string ToDisplayString() => Type == FilterMatchType.Marker
        ? $"Marked by marker {MarkerIndex + 1}"
        : Text;

    public FilterMatch Clone() => new()
    {
        Type = Type,
        Text = Text,
        CaseSensitive = CaseSensitive,
        Regex = Regex,
        MarkerIndex = MarkerIndex
    };
}
