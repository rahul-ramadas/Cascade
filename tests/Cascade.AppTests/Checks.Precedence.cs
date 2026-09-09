using System.Drawing;
using Cascade.App;
using Cascade.Core.Model;

namespace Cascade.AppTests;

/// <summary>Part of <see cref="Checks"/>: the preference that decides which of two matching filters wins a
/// line, driven the way a reader changes it - through the window, not by poking the engine.</summary>
internal static partial class Checks
{
    /// <summary>
    /// The same filters and the same file, read under each rule. A line only both filters match is the one
    /// that tells them apart, so the fixture is built around it: with the include listed first, the classic
    /// rule takes the line away and list order leaves it where the reader put it.
    ///
    /// <para>Driven through <c>ApplySettingsEverywhere</c> - the path Preferences and a settings import both
    /// take - because a preference that the engine honours but the window never hands it is exactly the
    /// failure worth catching, and it looks like nothing at all from inside the engine.</para>
    /// </summary>
    internal static bool RunFilterPrecedenceChecks()
    {
        Line("-- which filter wins a line --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_st_prec_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, new[]
        {
            "alpha only",             // the include alone
            "beta only",              // the exclude alone
            "alpha and beta",         // both: the line the two rules disagree about
            "gamma nothing",          // neither
        });

        MainForm? form = null;
        try
        {
            var settings = new AppSettings();     // ExcludesWin, as a fresh install has it
            form = new MainForm(settings, new MachineState(), new[] { log })
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 700),
            };
            form.Show();
            Pump();

            var doc = form.DocForTesting;
            for (int i = 0; i < 60 && doc.CompletedLineCount < 4; i++) { Thread.Sleep(20); Pump(); }
            bool ok = Check("the file is open", doc.CompletedLineCount == 4, doc.CompletedLineCount.ToString());
            if (!ok) return false;

            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            filters.Add(new Filter { Enabled = true, Match = { Text = "alpha" } });
            filters.Add(new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "beta" } });
            doc.SetFilters(filters);
            WaitForFiltering(doc);

            string Shown() => string.Join(" | ", Enumerable.Range(0, (int)doc.RowCount)
                                                           .Select(r => doc.GetLineText(doc.RowToLine(r))));

            ok &= Check("the default is the rule every filter file was written against",
                        settings.FilterPrecedence == FilterPrecedence.ExcludesWin);
            ok &= Check("an exclude takes a line the include above it claimed", Shown() == "alpha only", Shown());

            long hits = doc.FilterCacheHits;
            settings.FilterPrecedence = FilterPrecedence.ListOrder;
            form.ApplySettingsForTesting();
            WaitForFiltering(doc);
            ok &= Check("list order leaves the line with the filter listed first",
                        Shown() == "alpha only | alpha and beta", Shown());
            // Deep matches do not depend on the rule, so the keys are unchanged and the answer is a recombine
            // of what is already held - not a second pass over the file.
            ok &= Check("and the change is answered from the cache, not by re-reading the file",
                        doc.FilterCacheHits > hits, $"{hits} -> {doc.FilterCacheHits}");

            // The exclude still takes anything no earlier filter claimed, so this is not "excludes stop working".
            settings.FilterPrecedence = FilterPrecedence.ExcludesWin;
            form.ApplySettingsForTesting();
            WaitForFiltering(doc);
            ok &= Check("switching back restores the classic answer", Shown() == "alpha only", Shown());

            // Move the exclude above the include and list order agrees with the classic rule again - which is
            // what makes position, rather than kind, the thing that decides.
            settings.FilterPrecedence = FilterPrecedence.ListOrder;
            form.ApplySettingsForTesting();
            WaitForFiltering(doc);
            var exclude = doc.Filters.Roots[1];
            doc.Filters.Remove(exclude);
            doc.Filters.Add(exclude, null, 0);
            doc.ApplyFilters();
            WaitForFiltering(doc);
            ok &= Check("an exclude listed above the include takes the line again",
                        Shown() == "alpha only", Shown());

            // Applying the settings again without moving the preference must not disturb the view.
            string before = Shown();
            form.ApplySettingsForTesting();
            WaitForFiltering(doc);
            ok &= Check("re-applying the same preference changes nothing", Shown() == before, Shown());
            return ok;
        }
        finally
        {
            form?.Dispose();
            try { File.Delete(log); } catch { }
        }
    }
}
