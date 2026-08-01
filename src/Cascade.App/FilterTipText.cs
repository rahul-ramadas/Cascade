using System.Text;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// Wording for the hover tip that explains a line: which filters matched it, and what each of them
/// actually says.
///
/// The filter list shows a description when there is one, which is friendly right up to the moment you
/// are staring at a line wondering why it is that colour - so here the pattern is always spelled out in
/// full, description or not. Excludes are the ones that take a line away, so they are marked; and a
/// switched-off filter that matched is worth saying, because switching it on is the obvious next move.
/// </summary>
internal static class FilterTipText
{
    /// <summary>Most filters listed before the tip gives up and says how many more there were. A tip taller
    /// than the screen helps nobody.</summary>
    internal const int MaxListed = 12;

    /// <summary>Builds the tip for a line, or an empty string when nothing matched (no tip is worth showing
    /// then - the absence of filters is what the plain row already says).</summary>
    internal static string Build(IReadOnlyList<Filter> matches)
    {
        if (matches.Count == 0) return "";

        // Switched-on filters first: they are the ones that explain what is on the screen right now.
        var ordered = new List<Filter>(matches);
        var order = new Dictionary<Filter, int>();
        for (int i = 0; i < matches.Count; i++) order[matches[i]] = i;
        ordered.Sort((a, b) => a.Enabled != b.Enabled ? (a.Enabled ? -1 : 1) : order[a].CompareTo(order[b]));

        var sb = new StringBuilder();
        int listed = Math.Min(MaxListed, ordered.Count);
        for (int i = 0; i < listed; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Line(ordered[i]));
        }
        if (ordered.Count > listed)
            sb.Append('\n').Append("…and ").Append(ordered.Count - listed).Append(" more");
        return sb.ToString();
    }

    private static string Line(Filter f)
    {
        var sb = new StringBuilder();
        if (f.Kind == FilterKind.Exclude) sb.Append("\u2260 ");   // ≠ : this one takes lines away

        string pattern = f.Match.ToDisplayString();
        if (f.Match.Type == FilterMatchType.Text)
        {
            if (f.Match.Regex) pattern = "/" + pattern + "/";
            if (f.Match.CaseSensitive) pattern += " (case)";
        }

        if (!string.IsNullOrEmpty(f.Description)) sb.Append(f.Description).Append(" — ");
        sb.Append(pattern);
        if (!f.Enabled) sb.Append(" (off)");
        return sb.ToString();
    }
}
