namespace Cascade.Core.Columns;

/// <summary>
/// Where the column edges go. Pure arithmetic - no drawing and no controls - so the rules the user feels
/// (a column edge lands on a character, the row fills the window, a dragged header settles somewhere
/// rather than flickering between two places) can be checked without a window.
/// </summary>
public static class ColumnLayout
{
    /// <summary>Rounds a width to a whole number of characters. With a fixed-pitch font that is what makes
    /// a column edge land where the text does; one character is the floor.</summary>
    public static int SnapToChars(int width, int charWidth)
    {
        if (charWidth <= 0) return Math.Max(1, width);
        int chars = (int)Math.Round(width / (double)charWidth, MidpointRounding.AwayFromZero);
        return Math.Max(1, chars) * charWidth;
    }

    /// <summary>
    /// Shares <paramref name="available"/> pixels between the visible columns, left to right.
    ///
    /// A column the user has sized keeps exactly what it was given. The rest ("auto") start from what
    /// their content asks for and then take up the slack: with room to spare the LAST auto column runs on
    /// to the right edge, and with too little the WIDEST auto columns are capped until the row fits - so a
    /// short field is never squeezed on behalf of a long one. Whenever there is an auto column and room
    /// for the minimums, the widths add up to exactly <paramref name="available"/>.
    /// </summary>
    /// <param name="wanted">What each visible column asks for: its own width if it has one, else what its
    /// content needs.</param>
    /// <param name="isAuto">Which of those are free to be resized to fit.</param>
    public static int[] Fit(IReadOnlyList<int> wanted, IReadOnlyList<bool> isAuto, int available, int minimum)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(isAuto);
        minimum = Math.Max(1, minimum);

        int n = wanted.Count;
        var result = new int[n];
        var autos = new List<int>();
        long fixedTotal = 0;
        for (int i = 0; i < n; i++)
        {
            result[i] = Math.Max(minimum, wanted[i]);
            if (i < isAuto.Count && isAuto[i]) autos.Add(i);
            else fixedTotal += result[i];
        }
        if (autos.Count == 0) return result;

        long room = available - fixedTotal;
        // Not even the minimums fit. Give each auto column its floor and let the row scroll: shrinking
        // further would leave columns nobody can read or grab the edge of.
        if (room < (long)autos.Count * minimum)
        {
            foreach (int i in autos) result[i] = minimum;
            return result;
        }

        long autoTotal = 0;
        foreach (int i in autos) autoTotal += result[i];

        if (autoTotal > room)
        {
            int cap = CapFitting(result, autos, room, minimum);
            foreach (int i in autos) result[i] = Math.Max(minimum, Math.Min(result[i], cap));
            autoTotal = 0;
            foreach (int i in autos) autoTotal += result[i];
        }

        // Whatever integer arithmetic left over goes to the last auto column, which is the one that runs
        // to the right-hand edge - so the row always ends exactly where the view does.
        result[autos[^1]] += (int)(room - autoTotal);
        return result;
    }

    /// <summary>The largest per-column ceiling under which the auto columns still fit in
    /// <paramref name="room"/>. Water-filling: everything narrower than the ceiling is untouched.</summary>
    private static int CapFitting(int[] widths, List<int> autos, long room, int minimum)
    {
        int hi = minimum;
        foreach (int i in autos) hi = Math.Max(hi, widths[i]);
        int lo = minimum;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            long sum = 0;
            foreach (int i in autos) sum += Math.Max(minimum, Math.Min(widths[i], mid));
            if (sum <= room) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Where a header being dragged should sit, given the widths as currently laid out and the pointer's
    /// x from the left edge of the first column. Returns <paramref name="from"/> to mean "leave it".
    ///
    /// The pointer has to pass the MIDDLE of the column it is over before anything moves. Without that,
    /// carrying a narrow column past a wide one swaps them, which puts the pointer back over the wide one,
    /// which swaps them again - the column would flicker between two places instead of settling.
    /// </summary>
    public static int DropTarget(IReadOnlyList<int> widths, int from, int x)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Count == 0) return from;
        if (x < 0) return 0;

        int left = 0;
        for (int i = 0; i < widths.Count; i++)
        {
            int right = left + widths[i];
            if (x < right)
            {
                if (i == from) return from;
                int middle = left + widths[i] / 2;
                if (i < from) return x < middle ? i : from;
                return x > middle ? i : from;
            }
            left = right;
        }
        return widths.Count - 1;   // dragged out past the last column
    }
}
