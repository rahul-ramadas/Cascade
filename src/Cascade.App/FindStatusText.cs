using System.Text;
using Cascade.Core.Find;

namespace Cascade.App;

/// <summary>
/// How a search's tally is put into words for the find bar.
///
/// Kept apart from the form so the wording can be checked directly. It has to stay short while still
/// answering everything a search raises: which match am I on, how many are there, and how much is being kept
/// from me - in lines and in hits, so neither has to be worked out by subtraction.
///
/// <para>Two words and one mark, each meaning one thing. A <b>line</b> is a line the term matched; a
/// <b>hit</b> is a single occurrence, so one line can carry several. <b>Hidden</b> is the filters keeping
/// matching lines back, and nothing else - a crop never reaches this far, because everywhere else in the app
/// a crop simply is the file. A <b>+</b> marks a count the sweep has not finished growing.</para>
/// </summary>
internal static class FindStatusText
{
    /// <summary>The short form. Clauses only appear when they say something: hit counts only when a line
    /// matched more than once, the hidden half only when the filters are keeping matches back.</summary>
    public static string Short(FindTally t)
    {
        if (t.VisibleLines == 0 && t.HiddenLines == 0)
            return t.Complete ? "No matches" : "Searching\u2026";

        string more = t.Complete ? "" : "+";
        // Hits earn their room only when they say something the line count does not, and only when the
        // record they are worked out from survived intact.
        bool countHits = t.Occurrences > t.VisibleLines + t.HiddenLines && t.Hits != HitCount.TotalUnknown;
        bool perSide = countHits && t.Hits == HitCount.Exact;

        // Off a match there is no "Match 12 of" to give the number its meaning, so it has to name itself -
        // otherwise the bar reads as a bare "348".
        string text = t.VisibleLines == 0
            ? "No matches shown"
            : t.Position > 0
                ? $"Match {t.Position:N0} of {Lines(t.VisibleLines)}{Hits(t.VisibleOccurrences)}"
                : $"{Lines(t.VisibleLines)}{Hits(t.VisibleOccurrences)}";

        // One label for the whole of what is being kept back, so the two halves never have to be told apart
        // by their nouns alone: a comma binds a tally together, a middle dot starts a different one.
        if (t.HiddenLines > 0)
            text += $" \u00b7 hidden: {Lines(t.HiddenLines)}{Hits(t.HiddenOccurrences)}";

        // The total survives a capped record even when the split does not, and saying it plainly beats
        // implying a precision the record cannot back.
        if (countHits && !perSide) text += $" \u00b7 {t.Occurrences:N0}{more} hits in all";
        return text;

        string Lines(long n) => $"{n:N0}{more} lines";
        string Hits(long n) => perSide ? $", {n:N0}{more} hits" : "";
    }

    /// <summary>The long form, for the tooltip: the same numbers with nothing left implied, plus the totals
    /// the bar leaves out and the reason for anything it could not count.</summary>
    public static string Long(FindTally t, string term)
    {
        if (t.VisibleLines == 0 && t.HiddenLines == 0)
            return t.Complete ? $"No matches for \u201c{term}\u201d" : $"Still searching for \u201c{term}\u201d\u2026";

        long lines = t.VisibleLines + t.HiddenLines;
        bool manyHits = t.Occurrences > lines;
        var s = new StringBuilder($"\u201c{term}\u201d matches {lines:N0} lines");
        if (manyHits && t.Hits != HitCount.TotalUnknown) s.Append($" with {t.Occurrences:N0} hits");
        s.Append('.');

        if (t.HiddenLines > 0)
            s.Append(manyHits && t.Hits == HitCount.Exact
                ? $" {t.VisibleLines:N0} lines and {t.VisibleOccurrences:N0} hits are shown;"
                  + $" {t.HiddenLines:N0} lines and {t.HiddenOccurrences:N0} hits are hidden by the filters."
                : $" {t.VisibleLines:N0} are shown and {t.HiddenLines:N0} are hidden by the filters.");

        if (t.Hits == HitCount.SplitUnknown)
            s.Append(" Too many lines matched more than once to say how many of those hits are shown.");
        else if (t.Hits == HitCount.TotalUnknown)
            s.Append(" Too many lines matched more than once for the hits to be counted.");

        if (t.Position > 0) s.Append($" You are on match {t.Position:N0}.");
        if (!t.Complete) s.Append(" Still searching, so the counts marked \u201c+\u201d can still grow.");
        return s.ToString();
    }
}
