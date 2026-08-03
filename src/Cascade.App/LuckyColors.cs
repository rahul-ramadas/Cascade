using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// The colours recommended for filters, and the rule for offering one no filter is already wearing.
///
/// Picking a colour by hand is the slow part of making a filter useful, and a filter with no colour is
/// invisible on the minimap. So the app carries a set worked out in advance: a sweep of 139,000 legible
/// backgrounds, packed farthest-point-first until no two left in it are close enough to be mistaken for
/// each other. Every entry clears the contrast a body of text needs, and none is nearly the page or
/// nearly the ink.
///
/// CLOSENESS IS CIEDE2000, NOT A WEIGHTED RGB DISTANCE. The cheap metrics get saturated colours badly
/// wrong: #00FF00 and #47FF33 are the same green to look at, and the redmean approximation rates them
/// 135 apart - further than it rates some pairs nobody would confuse. Packing under it produced a
/// palette with visible duplicates in it.
/// </summary>
internal static class LuckyColors
{
    internal readonly record struct Pair(RgbColor Back, RgbColor Fore);

    private static readonly RgbColor Ink = new(0x1A, 0x1A, 0x1A);
    private static readonly RgbColor Paper = new(0xFF, 0xFF, 0xFF);

    /// <summary>Below this two colours are close enough to be mistaken for each other. CIEDE2000 calls 1
    /// the least anyone can see and 10 "different at a glance"; 11 is just past that, and is what the set
    /// below is packed to. The threshold thins the set fast: 10 would give 147 colours, 12 would give 94.
    /// </summary>
    private const double TooClose = 11;

    /// <summary>Background, and whether its text is paper rather than ink. A literal because packing it
    /// takes sixteen million CIEDE2000 comparisons - worth spending once offline, not on every startup.
    /// In hue order, which is the order the palette is shown in.
    ///
    /// To regenerate: sweep 360 hues x lightness 20-90% x saturation 0-100%, keep what clears 4.5:1
    /// against whichever of ink or paper reads better on it, then farthest-point-first at CIEDE2000 11.
    /// </summary>
    private static readonly (int Back, bool OnPaper)[] Recipe =
    [
        (0x333333, true), (0xFED2CD, false), (0x47251F, true), (0x9B4331, true),
        (0x6F483E, true), (0xC19B90, false), (0xD23404, true), (0x986B5D, true),
        (0xCE7150, false), (0xFF9E7A, false), (0x989390, false), (0x7A3100, true),
        (0xD6955C, false), (0xF07305, false), (0x6E5B49, true), (0xFFC285, false),
        (0xA56112, true), (0xA78B62, false), (0x764C05, true), (0xF59B00, false),
        (0xFFEBC2, false), (0xB07E07, false), (0x4E3F18, true), (0xC3BDAC, false),
        (0xFCD75F, false), (0xBEAF74, false), (0xCCA300, false), (0x7D6908, true),
        (0x89854D, false), (0xFFFF00, false), (0xC0CC5C, false), (0x50580E, true),
        (0x74756C, true), (0x81990A, false), (0x525643, true), (0xE5EAD7, false),
        (0x90987C, false), (0xC2FF47, false), (0xEAFEC3, false), (0x617642, true),
        (0x5BDC04, false), (0x3AAD00, false), (0x1B5412, true), (0x9CFF99, false),
        (0x007A00, true), (0x90C192, false), (0x6C9D7C, false), (0x1D9A53, false),
        (0xC4FDDF, false), (0x0DD377, false), (0x5A7C6D, true), (0x234337, true),
        (0x057650, true), (0x52FFCB, false), (0xA1CEC2, false), (0x0CB68E, false),
        (0x069380, false), (0x72A19E, false), (0x1CCEC3, false), (0x8FFFFB, false),
        (0xD6F5F4, false), (0x026E6E, true), (0x11B3C5, false), (0x00DDFF, false),
        (0x258DA7, false), (0x006A85, true), (0x0A495C, true), (0x14AAEB, false),
        (0x5E7782, true), (0xA6BEC9, false), (0x095A90, true), (0x85CEFF, false),
        (0x007ACC, true), (0x1B304B, true), (0x7E90A9, false), (0x545A63, true),
        (0x5297FF, false), (0x627193, true), (0x9DB9FB, false), (0xD0DCFB, false),
        (0x0428DC, true), (0x494F83, true), (0x6565E2, true), (0x000070, true),
        (0x3E2D80, true), (0x8B7BC1, false), (0xBAA4DF, false), (0xBBB5C5, false),
        (0xB47DFC, false), (0x312343, true), (0x7749AB, true), (0x6F5E82, true),
        (0x8B7C98, false), (0xA200FF, true), (0x71038C, true), (0xE229FF, false),
        (0x643B68, true), (0xF0DAF1, false), (0xB06BB3, false), (0xFFC2FF, false),
        (0xFF85FF, false), (0xD204B6, true), (0x9D4385, true), (0xE7E4E6, false),
        (0xFF33BB, false), (0x990057, true), (0x7A7176, true), (0xCE8DB0, false),
        (0x600636, true), (0xDD0E7C, true), (0x67515B, true), (0x936277, true),
        (0xFF5C92, false), (0xCE0944, true), (0xFFADC0, false), (0x85001B, true),
        (0x8B414E, true), (0xC36574, false), (0xFF3D44, false), (0xFF8589, false),
    ];

    private static readonly Pair[] All =
        [.. Recipe.Select(r => new Pair(RgbColor.FromRgbInt(r.Back), r.OnPaper ? Paper : Ink))];

    /// <summary>Converted once: comparing happens in Lab, and converting into it is most of the cost.</summary>
    private static readonly Lab[] AllLab = [.. All.Select(p => ToLab(p.Back))];

    public static int Count => All.Length;

    /// <summary>Everything on offer, before any of it is ruled out.</summary>
    public static IReadOnlyList<Pair> Palette => All;

    /// <summary>The pair at <paramref name="index"/>, which wraps.</summary>
    public static Pair At(int index) => All[((index % Count) + Count) % Count];

    /// <summary>How far round the set each press moves. Coprime with its size, so repeated presses visit
    /// every colour before repeating any; chosen by measuring every coprime stride and taking the one whose
    /// WORST pair of consecutive offers is furthest apart - 20.6, against 13.5 for a stride picked to look
    /// good on paper. The set is in hue order, so stepping by one would offer the same colour twice running.
    /// </summary>
    private const int Stride = 49;

    /// <summary>The next pair after <paramref name="from"/> that no other filter is already wearing and
    /// that is not a near-miss for one either - two filters that look almost the same are worse than two
    /// that look identical, because you cannot tell that you are confusing them. Disabled filters count:
    /// they are still in the list, still coloured, and will be switched on again.
    ///
    /// When everything is close to something, it settles for whichever pair is furthest from anything in
    /// use rather than handing back a duplicate.</summary>
    public static int Next(int from, IEnumerable<Filter> others, Filter self)
    {
        var used = InUse(others, self);

        int best = from + Stride;
        double bestRoom = -1;
        for (int step = 1; step < Count; step++)
        {
            int at = from + step * Stride;
            double room = Room(AllLab[((at % Count) + Count) % Count], used);
            if (room > TooClose) return at;
            if (room > bestRoom) { bestRoom = room; best = at; }
        }
        return best;
    }

    /// <summary>The palette less every colour a filter already wears, near enough that the two would be
    /// mistaken for each other. The set itself is fixed, so this only ever takes entries away and a colour
    /// keeps its place however the filters change.</summary>
    public static List<Pair> Free(IEnumerable<Filter> others, Filter self)
    {
        var used = InUse(others, self);
        var free = new List<Pair>();
        for (int i = 0; i < All.Length; i++)
            if (Room(AllLab[i], used) > TooClose) free.Add(All[i]);
        return free;
    }

    private static List<Lab> InUse(IEnumerable<Filter> others, Filter self)
    {
        var used = new List<Lab>();
        foreach (var f in others)
        {
            if (ReferenceEquals(f, self)) continue;
            if (f.Style.Background is { } b) used.Add(ToLab(b));
            if (f.Style.Foreground is { } g) used.Add(ToLab(g));
        }
        return used;
    }

    /// <summary>How far the nearest colour in use is from this pair's background - the colour it would show
    /// as, on the map and down the filter list. Its text colour is always ink or paper, so it says nothing
    /// about whether two filters can be told apart.</summary>
    private static double Room(Lab candidate, List<Lab> used)
    {
        double nearest = double.MaxValue;
        foreach (var u in used) nearest = Math.Min(nearest, Difference(candidate, u));
        return nearest;
    }

    /// <summary>How different two colours look, as CIEDE2000 measures it: 1 is the least anyone can see,
    /// 10 is a glance apart, 50 is unmistakable.</summary>
    internal static double Distance(RgbColor a, RgbColor b) => Difference(ToLab(a), ToLab(b));

    internal readonly record struct Lab(double L, double A, double B);

    internal static Lab ToLab(RgbColor c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;   // relative to D65 white
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;

        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIEDE2000. Long, and every correction in it earns its place: they are what stop the measure
    /// disagreeing with the eye about saturated colours, dark colours and blues, which is exactly where a
    /// plain distance falls down.</summary>
    internal static double Difference(Lab p, Lab q)
    {
        const double Pow25To7 = 6103515625.0;

        double c1 = Math.Sqrt(p.A * p.A + p.B * p.B), c2 = Math.Sqrt(q.A * q.A + q.B * q.B);
        double cbar = (c1 + c2) / 2, cbar7 = Math.Pow(cbar, 7);
        double g = 0.5 * (1 - Math.Sqrt(cbar7 / (cbar7 + Pow25To7)));

        double ap1 = (1 + g) * p.A, ap2 = (1 + g) * q.A;
        double cp1 = Math.Sqrt(ap1 * ap1 + p.B * p.B), cp2 = Math.Sqrt(ap2 * ap2 + q.B * q.B);
        double hp1 = Hue(p.B, ap1), hp2 = Hue(q.B, ap2);

        double dl = q.L - p.L, dc = cp2 - cp1;
        double dhp = cp1 * cp2 == 0 ? 0
                   : Math.Abs(hp2 - hp1) <= 180 ? hp2 - hp1
                   : hp2 <= hp1 ? hp2 - hp1 + 360 : hp2 - hp1 - 360;
        double dh = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(Rad(dhp) / 2);

        double lbar = (p.L + q.L) / 2, cpbar = (cp1 + cp2) / 2;
        double hbar = cp1 * cp2 == 0 ? hp1 + hp2
                    : Math.Abs(hp1 - hp2) <= 180 ? (hp1 + hp2) / 2
                    : hp1 + hp2 < 360 ? (hp1 + hp2 + 360) / 2 : (hp1 + hp2 - 360) / 2;

        double t = 1 - 0.17 * Math.Cos(Rad(hbar - 30)) + 0.24 * Math.Cos(Rad(2 * hbar))
                     + 0.32 * Math.Cos(Rad(3 * hbar + 6)) - 0.20 * Math.Cos(Rad(4 * hbar - 63));
        double sl = 1 + 0.015 * (lbar - 50) * (lbar - 50) / Math.Sqrt(20 + (lbar - 50) * (lbar - 50));
        double sc = 1 + 0.045 * cpbar;
        double sh = 1 + 0.015 * cpbar * t;

        double cpbar7 = Math.Pow(cpbar, 7);
        double rt = -2 * Math.Sqrt(cpbar7 / (cpbar7 + Pow25To7))
                       * Math.Sin(Rad(60 * Math.Exp(-Math.Pow((hbar - 275) / 25, 2))));

        double kl = dl / sl, kc = dc / sc, kh = dh / sh;
        return Math.Sqrt(kl * kl + kc * kc + kh * kh + rt * kc * kh);

        static double Rad(double degrees) => degrees * Math.PI / 180;

        static double Hue(double b, double a)
        {
            if (a == 0 && b == 0) return 0;
            double d = Math.Atan2(b, a) * 180 / Math.PI;
            return d >= 0 ? d : d + 360;
        }
    }

    internal static double Luminance(RgbColor c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Contrast ratio as WCAG defines it, so the set can be held to a number rather than to an
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
