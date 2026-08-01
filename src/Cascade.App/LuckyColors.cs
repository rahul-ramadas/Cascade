using System.Drawing;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// Colour pairs for a filter, and the rule for offering one that is not already in use.
///
/// Picking a colour by hand from a wheel is the slow part of making a filter useful, and a filter with no
/// colour is invisible on the minimap. These are all legible - dark text on a light ground or the reverse,
/// every pair well clear of the contrast a body of text needs - and offered in an order that keeps
/// neighbours apart on the wheel rather than walking round it.
/// </summary>
internal static class LuckyColors
{
    internal readonly record struct Pair(RgbColor Back, RgbColor Fore);

    private static readonly RgbColor Ink = new(0x1A, 0x1A, 0x1A);
    private static readonly RgbColor Paper = new(0xFF, 0xFF, 0xFF);

    // Seven hues, each offered once pale and once deep. Seven because the ring has to be an odd length for
    // every entry to land on a different hue-and-lightness pair, and because measuring says that is as many
    // as can be kept sixty units apart in RGB at a lightness that still takes readable text - crowd in more
    // and two of them start looking like each other, which is worse than having fewer.
    private const int Hues = 7;
    // Coprime with Hues so every offer lands on a different hue, and chosen so that two offers of the same
    // lightness - which is every other one - are as far apart on the ring as seven hues allow.
    private const int Step = 2;
    private const double PaleLight = 0.68, DeepLight = 0.32;
    private const double PaleSat = 0.75, DeepSat = 0.62;

    public static int Count => Hues * 2;

    /// <summary>The pair at <paramref name="index"/>, its text colour chosen for contrast against its
    /// ground.</summary>
    public static Pair At(int index)
    {
        int i = ((index % Count) + Count) % Count;
        bool pale = i % 2 == 0;
        var back = FromHsl((i * Step % Hues) / (double)Hues, pale ? PaleSat : DeepSat, pale ? PaleLight : DeepLight);
        return new Pair(back, Contrast(back, Ink) >= Contrast(back, Paper) ? Ink : Paper);
    }

    /// <summary>The next pair after <paramref name="from"/> that no other filter is already wearing and that
    /// is not a near-miss for one either - two filters that look almost the same are worse than two that
    /// look identical, because you cannot tell that you are confusing them.
    ///
    /// Falls back to simply advancing when every pair is spoken for, so the button always does something.</summary>
    public static int Next(int from, IEnumerable<Filter> others, Filter self)
    {
        var used = new List<RgbColor>();
        foreach (var f in others)
        {
            if (ReferenceEquals(f, self)) continue;
            if (f.Style.Background is { } b) used.Add(b);
            else if (f.Style.Foreground is { } g) used.Add(g);
        }

        for (int step = 1; step <= Count; step++)
        {
            var candidate = At(from + step).Back;
            if (used.All(u => Distance(u, candidate) > 90)) return from + step;
        }
        return from + 1;
    }

    private static RgbColor FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return new RgbColor(Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));

        static byte Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double v = t < 1.0 / 6 ? p + (q - p) * 6 * t
                     : t < 1.0 / 2 ? q
                     : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
                     : p;
            return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        }
    }

    /// <summary>Straight-line distance in RGB. Crude next to a perceptual space, but the palette is spread
    /// far enough apart that the only job here is to notice a near-repeat.</summary>
    internal static double Distance(RgbColor a, RgbColor b)
        => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    internal static double Luminance(RgbColor c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Contrast ratio as WCAG defines it, so the palette can be held to a number rather than to an
    /// opinion.</summary>
    internal static double Contrast(RgbColor a, RgbColor b)
    {
        double la = Relative(a), lb = Relative(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

        static double Relative(RgbColor c)
            => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}
