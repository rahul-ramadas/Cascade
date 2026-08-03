using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// A ring of about a thousand background/text pairs, and the rule for offering one no filter is already
/// wearing.
///
/// Picking a colour by hand is the slow part of making a filter useful, and a filter with no colour is
/// invisible on the minimap. The ring is generated rather than hand-picked: every hue at several
/// lightnesses and saturations, each with whichever of ink or paper reads better on it, and anything that
/// does not clear the contrast a body of text needs is thrown out. That is enough for a filter file of
/// hundreds and still leaves every offer legible.
///
/// It is walked in an order that puts consecutive offers most of the way round the wheel from each other,
/// so pressing the button twice never looks like it did nothing.
/// </summary>
internal static class LuckyColors
{
    internal readonly record struct Pair(RgbColor Back, RgbColor Fore);

    private static readonly RgbColor Ink = new(0x1A, 0x1A, 0x1A);
    private static readonly RgbColor Paper = new(0xFF, 0xFF, 0xFF);

    // A number of hues and a number of tones with no common factor, so walking one of each per step visits
    // every combination exactly once before repeating.
    private const int Hues = 143;
    // Both strides turn their own ring most of the way round per step, so one press to the next changes hue
    // and lightness together - two offers a step apart never look like the same colour twice.
    private const int HueStride = 89;
    private const int ToneStride = 3;

    // Saturation rises with distance from the middle: a pale colour at low saturation is white whatever its
    // hue and a dark one is black, so those are the shades that all look alike. It stops well short of full,
    // though - these are backgrounds behind a screenful of text, and a neon one is exhausting to read.
    // Mid lightness is left out altogether: it takes neither ink nor paper well.
    private static readonly (double Light, double Sat)[] Tones =
    {
        (0.86, 0.80), (0.79, 0.70), (0.72, 0.62), (0.64, 0.58),
        (0.45, 0.58), (0.37, 0.66), (0.29, 0.74),
    };

    /// <summary>Below this two colours are close enough to be mistaken for each other. In the weighted
    /// distance below, where a mid-grey and white are about 600 apart.</summary>
    private const double TooClose = 42;

    private const double ReadableContrast = 4.5;

    private static readonly Pair[] Ring = Build();

    public static int Count => Ring.Length;

    /// <summary>The pair at <paramref name="index"/>, which wraps.</summary>
    public static Pair At(int index) => Ring[((index % Count) + Count) % Count];

    private static Pair[] Build()
    {
        var ring = new List<Pair>(Hues * Tones.Length);
        for (int step = 0; step < Hues * Tones.Length; step++)
        {
            var (light, sat) = Tones[step * ToneStride % Tones.Length];
            var back = FromHsl(step * HueStride % Hues / (double)Hues, sat, light);
            var fore = Contrast(back, Ink) >= Contrast(back, Paper) ? Ink : Paper;
            if (Contrast(back, fore) >= ReadableContrast) ring.Add(new Pair(back, fore));
        }
        return ring.ToArray();
    }

    /// <summary>The next pair after <paramref name="from"/> that no other filter is already wearing and that
    /// is not a near-miss for one either - two filters that look almost the same are worse than two that
    /// look identical, because you cannot tell that you are confusing them. Disabled filters count: they
    /// are still in the list, still coloured, and will be switched on again.
    ///
    /// When everything is close to something, it settles for whichever pair is furthest from anything in
    /// use rather than handing back a duplicate.</summary>
    public static int Next(int from, IEnumerable<Filter> others, Filter self)
    {
        var used = InUse(others, self);

        int best = from + 1;
        double bestRoom = -1;
        // Stops one short of a full turn: a step of Count lands back on the pair just offered, and since the
        // filter being edited is not counted as using anything, that pair always looks free.
        for (int step = 1; step < Count; step++)
        {
            var candidate = At(from + step);
            double room = Room(candidate, used);
            if (room > TooClose) return from + step;
            if (room > bestRoom) { bestRoom = room; best = from + step; }
        }
        return best;
    }

    /// <summary>Every pair the button would be willing to offer - the same room-to-spare rule, applied to
    /// the whole ring at once so they can be shown together.
    ///
    /// Thinned against ITSELF as well as against what is worn. The ring is a fine sweep - 143 hues, so
    /// neighbours are under three degrees apart - which is right for a button that steps a long way each
    /// press and wrong for a grid, where a screenful of near-identical colours is no choice at all. Sorted
    /// by hue rather than left in ring order, because the ring's whole point is that consecutive entries
    /// are far apart, and that reads as noise when they are all on screen at once.</summary>
    public static List<Pair> Free(IEnumerable<Filter> others, Filter self)
    {
        var used = InUse(others, self);
        var free = new List<Pair>();
        for (int i = 0; i < Count; i++)
        {
            var candidate = At(i);
            if (Room(candidate, used) <= TooClose) continue;
            if (free.Exists(kept => Distance(kept.Back, candidate.Back) <= TooClose)) continue;
            free.Add(candidate);
        }

        free.Sort((a, b) =>
        {
            var (ha, la) = HueLight(a.Back);
            var (hb, lb) = HueLight(b.Back);
            return ha != hb ? ha.CompareTo(hb) : lb.CompareTo(la);
        });
        return free;
    }

    private static (double Hue, double Light) HueLight(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double light = (max + min) / 2, span = max - min;
        if (span < 1e-9) return (0, light);
        double hue = max == r ? (g - b) / span + (g < b ? 6 : 0)
                   : max == g ? (b - r) / span + 2
                   : (r - g) / span + 4;
        return (hue / 6, light);
    }

    private static List<RgbColor> InUse(IEnumerable<Filter> others, Filter self)
    {
        var used = new List<RgbColor>();
        foreach (var f in others)
        {
            if (ReferenceEquals(f, self)) continue;
            if (f.Style.Background is { } b) used.Add(b);
            if (f.Style.Foreground is { } g) used.Add(g);
        }
        return used;
    }

    /// <summary>How far the nearest colour in use is from this pair's background - the colour it would show
    /// as, on the map and down the filter list. Its text colour is always ink or paper, so it says nothing
    /// about whether two filters can be told apart.</summary>
    private static double Room(Pair candidate, List<RgbColor> used)
    {
        double nearest = double.MaxValue;
        foreach (var u in used) nearest = Math.Min(nearest, Distance(u, candidate.Back));
        return nearest;
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

    /// <summary>Distance weighted the way the eye weighs it - green counts most, and red and blue count for
    /// more or less depending on how red the two already are. Straight RGB distance calls two dark blues
    /// far apart and two mid greens close together, which is the wrong way round for deciding whether a
    /// colour is already taken.</summary>
    internal static double Distance(RgbColor a, RgbColor b)
    {
        double rbar = (a.R + b.R) / 2.0;
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt((2 + rbar / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rbar) / 256) * db * db);
    }

    internal static double Luminance(RgbColor c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Contrast ratio as WCAG defines it, so the ring can be held to a number rather than to an
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
