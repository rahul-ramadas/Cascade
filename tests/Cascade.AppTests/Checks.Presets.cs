using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using Cascade.App;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.AppTests;

/// <summary>Part of <see cref="Checks"/>: presets, where the tick says what is in effect and the selection says what a command will act on.</summary>
internal static partial class Checks
{

    /// <summary>A preset's TICK says it is in effect: it switches the filters it names on and off and leaves
    /// every other filter alone. Its SELECTION is only the user's aim, and must survive anything the model
    /// does - while the two were one thing, aiming at a preset switched its filters back on and it could
    /// never be updated to drop one.</summary>
    internal static bool RunFilterPresetChecks()
    {
        Line("-- filter presets --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_presets_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++) sb.Append($"alpha beta gamma line {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var pane = new FilterPresetsControl { Dock = DockStyle.Fill };
            host = new HiddenForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(240, 220),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(pane);
            pane.Attach(doc);
            host.Show();
            Pump();

            var collection = new FilterCollection();
            var a = new Filter { Match = new FilterMatch { Text = "alpha" } };
            var b = new Filter { Match = new FilterMatch { Text = "beta" } };
            var c = new Filter { Match = new FilterMatch { Text = "gamma" } };
            // In no preset at all, which is what says the presets keep their hands off everything else.
            var loose = new Filter { Match = new FilterMatch { Text = "line" } };
            foreach (var f in new[] { a, b, c, loose }) collection.Roots.Add(f);
            collection.Presets.Add(new FilterPreset("first", new[] { a.Id }));
            collection.Presets.Add(new FilterPreset("second", new[] { b.Id, c.Id }));
            doc.SetFilters(collection);
            pane.Attach(doc);
            Pump();

            int applied = 0;
            pane.PresetsApplied += () => applied++;

            bool ok = Check("both presets are listed", pane.LabelsForTesting.SequenceEqual(new[] { "first", "second" }),
                            string.Join(" | ", pane.LabelsForTesting));
            ok &= Check("nothing is in effect while every filter is off", pane.ActiveForTesting.Length == 0);

            // Ticking one preset puts exactly its filters on.
            pane.TickForTesting("first");
            Pump();
            ok &= Check("ticking a preset enables exactly its filters", a.Enabled && !b.Enabled && !c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            // Ticking a second means "and these too", and does not drop the first.
            pane.TickForTesting("first", "second");
            Pump();
            ok &= Check("ticking a second adds its filters to the first's", a.Enabled && b.Enabled && c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            pane.TickForTesting("second");
            Pump();
            ok &= Check("unticking one turns its filters back off", !a.Enabled && b.Enabled && c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            pane.TickForTesting();
            Pump();
            ok &= Check("unticking the last leaves every filter off", !a.Enabled && !b.Enabled && !c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            // THE REPORTED BUG. A preset says which filters belong to it and nothing about the rest, so a
            // filter switched on by hand has to survive a preset being put in and taken out of effect.
            loose.Enabled = true;
            pane.TickForTesting("first");
            Pump();
            ok &= Check("ticking a preset leaves a filter it does not name alone", a.Enabled && loose.Enabled,
                        $"a={a.Enabled} loose={loose.Enabled}");
            pane.TickForTesting();
            Pump();
            ok &= Check("and unticking it takes only its own filters away", !a.Enabled && loose.Enabled,
                        $"a={a.Enabled} loose={loose.Enabled}");
            loose.Enabled = false;
            pane.RefreshActive();

            // A burst that nets out must cost nothing at all: re-running a pass over a whole file to arrive
            // back where it started is a visible flicker of the progress bar and a lot of work for no answer.
            applied = 0;
            pane.TickForTesting("first");
            pane.TickForTesting();
            Pump();
            ok &= Check("ticking a preset and unticking it again before the pass runs re-filters nothing",
                        applied == 0 && !a.Enabled, $"applied {applied} times, a={a.Enabled}");

            // A burst of tick changes must cost one re-filter, not one each: applying is what re-runs the
            // filters over the whole file.
            applied = 0;
            pane.TickForTesting("first");
            pane.TickForTesting("second");
            pane.TickForTesting("first", "second");
            Pump();
            ok &= Check("a burst of tick changes re-filters once", applied == 1, $"applied {applied} times");

            // Landing on the same set of filters must cost nothing at all. Every click in the pane used to
            // re-run a pass over the whole file to arrive back where it started, which on a big file is a
            // visible flicker of the progress bar and a great deal of work for no answer.
            applied = 0;
            pane.TickForTesting("first", "second");
            Pump();
            pane.TickForTesting("first", "second");
            Pump();
            ok &= Check("but re-picking the same presets does not re-filter at all", applied == 0,
                        $"applied {applied} times");

            // ---- the tick and the selection are different things ----

            // THE REPORTED BUG. Aiming at a preset - by clicking its name, or by right-clicking it to reach
            // the menu - must not switch a single filter.
            pane.TickForTesting();
            Pump();
            pane.ClickForTesting("first");
            Pump();
            ok &= Check("clicking a preset's name aims at it and switches nothing on",
                        pane.SelectedForTesting == "first" && pane.ActiveForTesting.Length == 0 && !a.Enabled,
                        $"selected '{pane.SelectedForTesting}', in effect [{string.Join(",", pane.ActiveForTesting)}], a={a.Enabled}");

            pane.ClickForTesting("second", MouseButtons.Right);
            Pump();
            ok &= Check("right-clicking a preset aims the menu at it and switches nothing on",
                        pane.SelectedForTesting == "second" && pane.ActiveForTesting.Length == 0 && !b.Enabled && !c.Enabled,
                        $"selected '{pane.SelectedForTesting}', in effect [{string.Join(",", pane.ActiveForTesting)}]");

            // The tick box is the one place a press does switch filters.
            pane.ClickForTesting("first", onTick: true);
            Pump();
            ok &= Check("pressing the tick box does put the preset in effect", a.Enabled && !b.Enabled,
                        $"a={a.Enabled} b={b.Enabled}");
            pane.ClickForTesting("first", onTick: true);
            Pump();
            ok &= Check("and pressing it again takes it out", !a.Enabled, $"a={a.Enabled}");

            // Windows toggles a tick of its own accord when an already-selected row is clicked, and on the
            // second click of a double-click - which is how renaming would switch a preset on.
            pane.NativeToggleForTesting("first");
            Pump();
            ok &= Check("a tick Windows set on its own is refused",
                        pane.ActiveForTesting.Length == 0 && !a.Enabled,
                        $"in effect [{string.Join(",", pane.ActiveForTesting)}], a={a.Enabled}");

            // Nothing in the model may move the user's aim.
            pane.SelectForTesting("first");
            b.Enabled = true; c.Enabled = true;
            pane.RefreshActive();
            Pump();
            ok &= Check("the selection stays where the user put it while the filters change under it",
                        pane.SelectedForTesting == "first" && pane.ActiveForTesting.SequenceEqual(new[] { "second" }),
                        $"selected '{pane.SelectedForTesting}', in effect [{string.Join(",", pane.ActiveForTesting)}]");

            // ---- the workflow the whole change exists for ----
            pane.TickForTesting("second");
            Pump();
            c.Enabled = false;                       // drop one of its filters by hand
            pane.RefreshActive();
            ok &= Check("dropping one of a preset's filters unticks it", pane.ActiveForTesting.Length == 0,
                        string.Join(",", pane.ActiveForTesting));
            pane.ClickForTesting("second", MouseButtons.Right);
            Pump();
            ok &= Check("and the filter stays off while the menu is aimed at the preset", !c.Enabled);
            pane.UpdateSelected();
            Pump();
            ok &= Check("so the preset can be updated to drop it",
                        collection.Presets[1].FilterIds.SequenceEqual(new[] { b.Id }),
                        $"second now names {collection.Presets[1].FilterIds.Count} filters");
            ok &= Check("and what is left of it is in effect again",
                        pane.ActiveForTesting.SequenceEqual(new[] { "second" }), string.Join(",", pane.ActiveForTesting));

            // "Apply Only This Preset" is what a single click used to mean.
            pane.TickForTesting("first", "second");
            Pump();
            pane.SelectForTesting("first");
            pane.ApplyOnlySelected();
            Pump();
            ok &= Check("applying only the selected preset takes the others out",
                        pane.ActiveForTesting.SequenceEqual(new[] { "first" }) && a.Enabled && !b.Enabled,
                        $"in effect [{string.Join(",", pane.ActiveForTesting)}], a={a.Enabled} b={b.Enabled}");

            // The other direction: enabling by hand is enough to put a preset in effect.
            a.Enabled = false; b.Enabled = false; c.Enabled = false;
            pane.RefreshActive();
            ok &= Check("nothing is in effect after everything is switched off by hand", pane.ActiveForTesting.Length == 0,
                        string.Join(",", pane.ActiveForTesting));
            b.Enabled = true;
            pane.RefreshActive();
            ok &= Check("ticking every filter of a preset by hand puts it in effect",
                        pane.ActiveForTesting.SequenceEqual(new[] { "second" }), string.Join(",", pane.ActiveForTesting));

            // The tick box has to be inside the strip that reacts to a press, or the two disagree about
            // where a preset is switched on. Measured against the zone, never a raw pixel count.
            ok &= CheckTickZoneCoversTheBox(pane);

            // A deleted filter is remembered but reported.
            collection.Remove(b);
            pane.Rebuild();
            ok &= Check("a preset says how many of its filters have gone",
                        pane.LabelsForTesting[1].Contains("1 missing"), string.Join(" | ", pane.LabelsForTesting));

            // ...and one that names nothing still standing has nothing to put in effect: the tick springs
            // back rather than claiming it is on, and no pass is run for it.
            applied = 0;
            pane.ClickForTesting("second", onTick: true);
            Pump();
            ok &= Check("and ticking one whose filters have all gone comes to nothing",
                        applied == 0 && !pane.ActiveForTesting.Contains("second", StringComparer.Ordinal),
                        $"applied {applied} times, in effect [{string.Join(",", pane.ActiveForTesting)}]");

            return ok;
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Ticks the first preset and compares the two renderings: whatever changed is the box, and all
    /// of it has to fall inside the strip a press toggles from.</summary>
    private static bool CheckTickZoneCoversTheBox(FilterPresetsControl pane)
    {
        pane.TickForTesting();
        Pump();
        using var off = pane.RenderRowsForTesting();
        pane.TickForTesting("first");
        Pump();
        using var on = pane.RenderRowsForTesting();

        var row = pane.RowBoundsForTesting(0);
        int left = int.MaxValue, right = -1;
        int wide = Math.Min(off.Width, on.Width), tall = Math.Min(off.Height, on.Height);
        for (int y = Math.Max(0, row.Top); y < Math.Min(row.Bottom, tall); y++)
            for (int x = 0; x < wide; x++)
                if (off.GetPixel(x, y).ToArgb() != on.GetPixel(x, y).ToArgb())
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }

        int zone = pane.TickZoneWidthForTesting;
        return Check("the tick zone covers the box it draws", right >= 0 && right < zone,
                     $"the box spans {left}..{right}, the zone is {zone}px wide");
    }
}
