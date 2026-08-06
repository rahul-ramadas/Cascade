namespace Cascade.Core.Model;

/// <summary>
/// A filter's visual style. Every attribute is optional: an unset attribute is inherited from the
/// nearest ancestor that sets it (see <see cref="StyleResolver"/>), falling back to the view default.
/// Foreground and background (and each flag) resolve independently.
/// </summary>
public sealed class FilterStyle
{
    public RgbColor? Foreground { get; set; }
    public RgbColor? Background { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }

    public bool IsEmpty => Foreground is null && Background is null && Bold is null && Italic is null
                           && Underline is null;

    public FilterStyle Clone() => new()
    {
        Foreground = Foreground,
        Background = Background,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline
    };
}

/// <summary>A fully-resolved style with no unset attributes, ready for rendering.</summary>
public readonly record struct ResolvedStyle(RgbColor Foreground, RgbColor Background, bool Bold, bool Italic,
                                            bool Underline = false);

/// <summary>Resolves a filter's effective style using per-property inheritance up the ancestor chain
/// (regardless of whether ancestors are enabled), falling back to the supplied defaults.</summary>
public static class StyleResolver
{
    public static ResolvedStyle Resolve(Filter filter, ResolvedStyle defaults)
    {
        RgbColor fg = defaults.Foreground, bg = defaults.Background;
        bool bold = defaults.Bold, italic = defaults.Italic, underline = defaults.Underline;

        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Foreground is { } c) { fg = c; break; }
        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Background is { } c) { bg = c; break; }
        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Bold is { } b) { bold = b; break; }
        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Italic is { } b) { italic = b; break; }
        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Underline is { } b) { underline = b; break; }

        return new ResolvedStyle(fg, bg, bold, italic, underline);
    }
}

/// <summary>What to do with one attribute of a style. <see cref="Leave"/> is the state that only means
/// anything when several filters are being changed together: each keeps whatever it already had.</summary>
public enum StyleEdit
{
    Leave,
    Inherit,
    Set
}

/// <summary>A change to make to the appearance of one or more filters, attribute by attribute.
///
/// Separate from <see cref="FilterStyle"/> because a style says what a filter looks like, while this says
/// what to <i>do</i> - and "leave it alone" is not a look. Reading it back off a set of filters
/// (<see cref="Describe"/>) and writing it to them (<see cref="ApplyTo"/>) are the whole of the
/// several-filters editing rule, so they live here where they can be tested without a dialog.</summary>
public readonly record struct StyleChange(
    StyleEdit Foreground, RgbColor ForegroundValue,
    StyleEdit Background, RgbColor BackgroundValue,
    StyleEdit Bold, bool BoldValue,
    StyleEdit Italic, bool ItalicValue,
    StyleEdit Underline = StyleEdit.Leave, bool UnderlineValue = false)
{
    /// <summary>Touches nothing.</summary>
    public static StyleChange Nothing =>
        new(StyleEdit.Leave, default, StyleEdit.Leave, default, StyleEdit.Leave, false, StyleEdit.Leave, false);

    /// <summary>What these filters already agree on. An attribute they do not agree on comes back as
    /// <see cref="StyleEdit.Leave"/>, which is both "they vary" and the right thing to do about it.</summary>
    public static StyleChange Describe(IEnumerable<Filter> filters)
    {
        var all = filters as IReadOnlyCollection<Filter> ?? filters.ToList();
        if (all.Count == 0) return Nothing;

        var (fore, foreValue) = Common(all.Select(f => f.Style.Foreground));
        var (back, backValue) = Common(all.Select(f => f.Style.Background));
        var (bold, boldValue) = Common(all.Select(f => f.Style.Bold));
        var (italic, italicValue) = Common(all.Select(f => f.Style.Italic));
        var (under, underValue) = Common(all.Select(f => f.Style.Underline));
        return new StyleChange(fore, foreValue ?? default, back, backValue ?? default,
                               bold, boldValue ?? false, italic, italicValue ?? false,
                               under, underValue ?? false);
    }

    private static (StyleEdit Edit, T? Value) Common<T>(IEnumerable<T?> values) where T : struct
    {
        bool first = true;
        T? agreed = null;
        foreach (var v in values)
        {
            if (first) { agreed = v; first = false; continue; }
            if (!Nullable.Equals(agreed, v)) return (StyleEdit.Leave, null);
        }
        return agreed is { } set ? (StyleEdit.Set, set) : (StyleEdit.Inherit, null);
    }

    /// <summary>Writes just the attributes this change speaks for. Returns whether anything moved, so a
    /// dialog that was opened and dismissed with OK unchanged costs no re-filtering.</summary>
    public bool ApplyTo(Filter filter)
    {
        var style = filter.Style;
        bool changed = false;
        if (Wanted(Foreground, ForegroundValue, style.Foreground) is var fg && !Nullable.Equals(style.Foreground, fg))
        { style.Foreground = fg; changed = true; }
        if (Wanted(Background, BackgroundValue, style.Background) is var bg && !Nullable.Equals(style.Background, bg))
        { style.Background = bg; changed = true; }
        if (Wanted(Bold, BoldValue, style.Bold) is var bold && !Nullable.Equals(style.Bold, bold))
        { style.Bold = bold; changed = true; }
        if (Wanted(Italic, ItalicValue, style.Italic) is var italic && !Nullable.Equals(style.Italic, italic))
        { style.Italic = italic; changed = true; }
        if (Wanted(Underline, UnderlineValue, style.Underline) is var under && !Nullable.Equals(style.Underline, under))
        { style.Underline = under; changed = true; }
        return changed;
    }

    private static T? Wanted<T>(StyleEdit edit, T value, T? current) where T : struct => edit switch
    {
        StyleEdit.Set => value,
        StyleEdit.Inherit => null,
        _ => current
    };
}
