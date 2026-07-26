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

    public bool IsEmpty => Foreground is null && Background is null && Bold is null && Italic is null;

    public FilterStyle Clone() => new()
    {
        Foreground = Foreground,
        Background = Background,
        Bold = Bold,
        Italic = Italic
    };
}

/// <summary>A fully-resolved style with no unset attributes, ready for rendering.</summary>
public readonly record struct ResolvedStyle(RgbColor Foreground, RgbColor Background, bool Bold, bool Italic);

/// <summary>Resolves a filter's effective style using per-property inheritance up the ancestor chain
/// (regardless of whether ancestors are enabled), falling back to the supplied defaults.</summary>
public static class StyleResolver
{
    public static ResolvedStyle Resolve(Filter filter, ResolvedStyle defaults)
    {
        RgbColor fg = defaults.Foreground, bg = defaults.Background;
        bool bold = defaults.Bold, italic = defaults.Italic;

        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Foreground is { } c) { fg = c; break; }
        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Background is { } c) { bg = c; break; }
        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Bold is { } b) { bold = b; break; }
        for (Filter? n = filter; n is not null; n = n.Parent)
            if (n.Style.Italic is { } b) { italic = b; break; }

        return new ResolvedStyle(fg, bg, bold, italic);
    }
}
