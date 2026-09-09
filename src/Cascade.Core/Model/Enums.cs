namespace Cascade.Core.Model;

/// <summary>Whether a filter selects lines to show (Include) or removes them afterward (Exclude).</summary>
public enum FilterKind
{
    Include,
    Exclude
}

/// <summary>How a line that matches both an include and an exclude is settled.</summary>
public enum FilterPrecedence
{
    /// <summary>An exclude takes the line wherever it sits in the list, unless something enabled nested under
    /// it matched too. What TextAnalysisTool.NET does, and what every filter file was written against.</summary>
    ExcludesWin,

    /// <summary>The first enabled filter in list order decides, an exclude included - so an exclude is just
    /// an include whose style is "do not draw it", and the list means one thing rather than two. Position an
    /// exclude above what it should beat and below what should beat it.</summary>
    ListOrder
}

/// <summary>How a filter tests a line. Text uses the pattern (optionally as regex); Marker matches
/// lines that carry a specific marker.</summary>
public enum FilterMatchType
{
    Text,
    Marker
}
