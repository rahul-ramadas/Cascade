using Cascade.Core.Find;

namespace Cascade.App;

/// <summary>
/// How a search's tally is put into words for the status bar.
///
/// Kept apart from the form so the wording can be checked directly. It has to stay short - the status bar
/// is a fixed row and this sits alongside the file paths - while still answering the three questions a
/// search raises: which match am I on, how many are there, and is anything being kept from me.
/// </summary>
internal static class FindStatusText
{
    /// <summary>The short form. Clauses only appear when they say something: occurrences only when a line
    /// matched more than once, hidden only when the filters are keeping matches back, and a trailing "+"
    /// on anything the sweep has not finished counting.</summary>
    public static string Short(FindTally t)
    {
        if (t.VisibleLines == 0 && t.HiddenLines == 0)
            return t.Complete ? "No matches" : "Searching\u2026";

        string more = t.Complete ? "" : "+";
        bool detailed = t.HiddenLines > 0 || t.Occurrences > t.VisibleLines + t.HiddenLines;

        // Off a match there is no "Match 12 of" to give the number its meaning, so it has to name itself -
        // otherwise the status bar reads as a bare "348".
        string text = t.Position > 0
            ? $"Match {t.Position:N0} of {t.VisibleLines:N0}{more}{(detailed ? " lines" : "")}"
            : $"{t.VisibleLines:N0}{more} {(detailed ? "lines" : "matches")}";

        if (t.HiddenLines > 0) text += $" \u00b7 {t.HiddenLines:N0}{more} hidden";
        if (t.Occurrences > t.VisibleLines + t.HiddenLines)
        {
            string floor = t.Approximate ? "\u2265" : "";
            text += t.HiddenLines > 0
                ? $" \u00b7 {t.VisibleOccurrences:N0}{more} of {floor}{t.Occurrences:N0}{more} hits"
                : $" \u00b7 {floor}{t.Occurrences:N0}{more} hits";
        }
        return text;
    }

    /// <summary>The long form, for the tooltip: the same numbers with nothing left implied.</summary>
    public static string Long(FindTally t, string term)
    {
        if (t.VisibleLines == 0 && t.HiddenLines == 0)
            return t.Complete ? $"No matches for \u201c{term}\u201d" : $"Still searching for \u201c{term}\u201d\u2026";

        string state = t.Complete ? "" : " (still searching)";
        string hidden = t.HiddenLines > 0
            ? $", {t.HiddenLines:N0} more on lines the filters are hiding"
            : "";
        string occurrences = t.Occurrences > t.VisibleLines + t.HiddenLines
            ? $"; {t.VisibleOccurrences:N0} occurrences shown of {(t.Approximate ? "at least " : "")}{t.Occurrences:N0} in the file"
            : "";
        string position = t.Position > 0 ? $"On match {t.Position:N0} of them. " : "";
        return $"{position}\u201c{term}\u201d matches {t.VisibleLines:N0} shown lines{hidden}{occurrences}{state}";
    }
}
