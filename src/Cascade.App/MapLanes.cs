using System.Drawing;
using Cascade.Core.Document;
using Cascade.Core.Filtering;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// Which filters the match map gives a lane to, and what colour each one is.
///
/// Lives apart from the map because two things have to agree on it: the map, which paints the lanes, and the
/// filter list, which paints the key beside each filter. A map whose colours you cannot look up is a map of
/// nowhere, and the moment the two work it out separately they will drift.
/// </summary>
internal static class MapLanes
{
    /// <summary>Beyond this there is nothing to see, and the map falls back to a plain density bar.</summary>
    public const int Max = 24;

    internal readonly record struct Lane(Filter Filter, Color Color, FilterMatchCache.MatchSet Set);

    /// <summary>One lane per enabled include filter that no enabled include filter above it already covers.
    ///
    /// Nesting narrows, so a filter's lines are a subset of its enabled parent's - the parent's lane already
    /// accounts for every one of them, and giving the children lanes of their own would say the same thing
    /// several times over in ever-thinner columns. Turning on one filter with twenty children under it is
    /// one lane; turning on six unrelated filters is six. A filter this file has nothing for gets no lane
    /// either: it would be a blank column taking width off the ones with something to show, and a saved
    /// filter set carries filters for every log its owner reads.</summary>
    public static List<Lane> For(CascadeDocument doc, AppSettings settings)
    {
        var result = new List<Lane>();
        foreach (var f in doc.Filters.EnumerateDepthFirst())
        {
            if (!f.Enabled || f.Kind != FilterKind.Include) continue;
            if (HasEnabledIncludeAncestor(f)) continue;
            if (doc.MatchCountFor(f) == 0) continue;
            if (doc.MatchSetFor(f) is not { } set) continue;
            result.Add(new Lane(f, ColorFor(f, result.Count, settings), set));
            if (result.Count >= Max) break;
        }
        return result;
    }

    private static bool HasEnabledIncludeAncestor(Filter f)
    {
        for (var p = f.Parent; p is not null; p = p.Parent)
            if (p.Enabled && p.Kind == FilterKind.Include) return true;
        return false;
    }

    /// <summary>Colours for filters that have none of their own, spaced round the wheel so that neighbouring
    /// lanes never read as the same colour.</summary>
    private static readonly Color[] Fallback =
    {
        Color.FromArgb(0xE6, 0x39, 0x46), Color.FromArgb(0x1D, 0x7D, 0xD8), Color.FromArgb(0x2A, 0x9D, 0x54),
        Color.FromArgb(0xE8, 0x8B, 0x0A), Color.FromArgb(0x8E, 0x44, 0xCC), Color.FromArgb(0x00, 0x9A, 0xA6),
        Color.FromArgb(0xC2, 0x2E, 0x8A), Color.FromArgb(0x7A, 0x6A, 0x00),
    };

    internal static Color FallbackForTesting(int index) => Fallback[index % Fallback.Length];

    /// <summary>The colour that stands for this filter on the map.
    ///
    /// Its own, when it has one, so a lane can be matched to the rows it colours - but pushed until it
    /// actually reads a few pixels wide against the gutter. Row colours are picked to be quiet enough to put
    /// text on, which is exactly what makes them vanish here: a pale grey highlight and a pale blue one are
    /// the same pixel. A filter with no colour of its own takes one from the palette rather than the text
    /// colour, which every unstyled filter would share.</summary>
    public static Color ColorFor(Filter f, int index, AppSettings settings)
    {
        var defaults = new ResolvedStyle(
            new RgbColor(settings.Foreground.R, settings.Foreground.G, settings.Foreground.B),
            new RgbColor(settings.Background.R, settings.Background.G, settings.Background.B), false, false);
        var style = StyleResolver.Resolve(f, defaults);
        var bg = Color.FromArgb(style.Background.R, style.Background.G, style.Background.B);
        var fg = Color.FromArgb(style.Foreground.R, style.Foreground.G, style.Foreground.B);

        Color own = bg.ToArgb() != settings.Background.ToArgb() ? bg
                  : fg.ToArgb() != settings.Foreground.ToArgb() ? fg
                  : Color.Empty;
        // A grey, black or white highlight has no hue to keep, and forcing saturation onto one invents a
        // colour outright - a filter styled black on yellow came out as a red lane. Those take a palette
        // colour too: a grey lane against a grey gutter would be nothing to look at either way.
        if (own.IsEmpty || ToHsl(own).S < 0.15) return Fallback[index % Fallback.Length];
        return Vivid(own, settings.GutterBack);
    }

    /// <summary>Raises a colour's saturation and pulls its lightness away from <paramref name="against"/>,
    /// keeping its hue. Enough to tell two pastels apart in a three-pixel column.</summary>
    internal static Color Vivid(Color c, Color against)
    {
        (double h, double s, double l) = ToHsl(c);
        s = Math.Max(s, 0.65);
        double target = Luminance(against);
        // Away from the background in whichever direction there is room, so this works the same on a dark
        // theme as on a light one.
        if (Math.Abs(l - target) < 0.30) l = target > 0.5 ? target - 0.30 : target + 0.30;
        return FromHsl(h, s, Math.Clamp(l, 0.18, 0.82));
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max > min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0) { int v = (int)Math.Round(l * 255); return Color.FromArgb(v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromArgb(Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
    }

    private static int Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        double v = t < 1.0 / 6 ? p + (q - p) * 6 * t
                 : t < 1.0 / 2 ? q
                 : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
                 : p;
        return (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }
}
