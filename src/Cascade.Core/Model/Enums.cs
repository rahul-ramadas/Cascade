namespace Cascade.Core.Model;

/// <summary>Whether a filter selects lines to show (Include) or removes them afterward (Exclude).</summary>
public enum FilterKind
{
    Include,
    Exclude
}

/// <summary>How a filter tests a line. Text uses the pattern (optionally as regex); Marker matches
/// lines that carry a specific marker.</summary>
public enum FilterMatchType
{
    Text,
    Marker
}
