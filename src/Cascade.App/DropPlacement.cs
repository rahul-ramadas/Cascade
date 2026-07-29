namespace Cascade.App;

/// <summary>One row of the filter list as the drag code sees it: how deeply it is nested and where it sits.</summary>
internal readonly record struct DropRow(int Level, int Top, int Height);

/// <summary>Where a dragged filter would land: before the row at <see cref="Slot"/> in the list the drag
/// code was given (equal to its length means "at the end"), nested <see cref="Level"/> deep.</summary>
internal readonly record struct DropSpot(int Slot, int Level);

/// <summary>
/// Works out where a dragged filter should go from the pointer alone, the way outliners do it: the vertical
/// position picks the gap between two rows, and the horizontal position picks how deep to nest in that gap.
///
/// Kept free of WinForms so the rules can be tested directly - they are fiddly, and every one of them is a
/// judgement about how the list should feel rather than something the compiler can check.
/// </summary>
internal static class DropPlacement
{
    /// <param name="rows">Visible rows in display order, <b>excluding</b> the subtree being dragged.</param>
    /// <param name="pointerY">Pointer position in the same coordinates as the rows.</param>
    /// <param name="indentFromRoot">How far right of a top-level row the pointer is, in pixels.</param>
    /// <param name="indentWidth">Pixels per level of nesting.</param>
    public static DropSpot For(IReadOnlyList<DropRow> rows, int pointerY, int indentFromRoot, int indentWidth)
    {
        // The gap the pointer is nearest: past the middle of a row means below it.
        int slot = rows.Count;
        for (int i = 0; i < rows.Count; i++)
        {
            if (pointerY < rows[i].Top + rows[i].Height / 2) { slot = i; break; }
        }

        // A row can sit one level deeper than the row above it, and no shallower than the row below - any
        // less and the row below would be adopted by something that is no longer its parent.
        int deepest = slot > 0 ? rows[slot - 1].Level + 1 : 0;
        int shallowest = slot < rows.Count ? rows[slot].Level : 0;
        if (shallowest > deepest) shallowest = deepest;

        int wanted = indentWidth <= 0 ? shallowest : (int)Math.Round((double)indentFromRoot / indentWidth);
        return new DropSpot(slot, Math.Clamp(wanted, shallowest, deepest));
    }
}
