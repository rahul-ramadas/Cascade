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

/// <summary>Part of <see cref="Checks"/>: loading, saving and letting go of a filter set.</summary>
internal static partial class Checks
{

    /// <summary>Anything that throws away the filter file on screen has to ask first: closing them, closing
    /// the window, and loading another set over the top - which is the same loss on one menu click, with no
    /// undo behind it.</summary>
    internal static bool RunCloseFiltersChecks()
    {
        Line("-- letting go of the filters --");

        // The rule itself, read without a modal prompt standing in the way.
        bool ok = Check("unsaved changes to a filter file are worth asking about",
                        MainForm.ShouldOfferToSaveFilters(false, dirty: true, "x.cascade"));
        ok &= Check("nothing to ask when nothing has changed",
                    !MainForm.ShouldOfferToSaveFilters(false, dirty: false, "x.cascade"));
        ok &= Check("nor when there is no file to save to",
                    !MainForm.ShouldOfferToSaveFilters(false, dirty: true, null));
        ok &= Check("and a headless run is never asked", !MainForm.ShouldOfferToSaveFilters(true, true, "x.cascade"));

        string log = Path.Combine(Path.GetTempPath(), "cascade_st_close_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 200).Select(i => i % 5 == 0 ? $"ERROR line {i}" : $"plain line {i}"));
        string filters = Path.Combine(Path.GetTempPath(), "cascade_st_close_" + Guid.NewGuid().ToString("N") + ".cascade");
        const string OriginalFile = """
            { "filters": [ { "id": "f1", "enabled": true, "matchType": "Text", "text": "ERROR" } ] }
            """;

        MainForm? form = null;
        try
        {
            File.WriteAllText(filters, OriginalFile, new UTF8Encoding(false));
            form = new MainForm(new AppSettings(), new MachineState(), [log, "/Filters:" + filters])
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 600),
            };
            var answer = DialogResult.Cancel;
            form.AnswerSavePromptForTesting = () => answer;
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 60 && doc.CompletedLineCount < 200; i++) { Thread.Sleep(20); Pump(); }

            int Filters() => doc.Filters.EnumerateDepthFirst().Count();
            ok &= Check($"the filter file is loaded ({Filters()} filters)", Filters() == 1 && form.FilterFileForTesting == filters);

            // Nothing unsaved: closing them asks nothing and simply does it.
            ok &= Check("with nothing changed there is nothing to ask about", !form.FiltersAreDirtyForTesting);
            ok &= Check("so closing the filters just closes them",
                        form.ClickMenuForTesting("File", "Close Filters") && Filters() == 0 &&
                        form.FilterFileForTesting is null);

            // Now with something unsaved. Load it again and change it.
            form.LoadFiltersForTesting(filters);
            Pump();
            ok &= Check("the file can be loaded again", Filters() == 1 && form.FilterFileForTesting == filters,
                        $"{Filters()} filters, {form.FilterFileForTesting}");
            form.ClickMenuForTesting("Filters", "Disable All");
            Pump();
            ok &= Check("and turning a filter off is an unsaved change", form.FiltersAreDirtyForTesting);

            answer = DialogResult.Cancel;
            form.ClickMenuForTesting("File", "Close Filters");
            Pump();
            ok &= Check("answering \"cancel\" leaves the filters exactly where they were",
                        Filters() == 1 && form.FilterFileForTesting == filters && form.FiltersAreDirtyForTesting,
                        $"{Filters()} filters, {form.FilterFileForTesting}");

            answer = DialogResult.No;
            form.ClickMenuForTesting("File", "Close Filters");
            Pump();
            ok &= Check("answering \"no\" closes them", Filters() == 0 && form.FilterFileForTesting is null);
            ok &= Check("and leaves the file on disk as it was",
                        File.ReadAllText(filters).Contains("\"enabled\": true", StringComparison.Ordinal) ||
                        File.ReadAllText(filters) == OriginalFile,
                        File.ReadAllText(filters));

            // ...and "yes" writes it out before letting go of it.
            form.LoadFiltersForTesting(filters);
            Pump();
            form.ClickMenuForTesting("Filters", "Disable All");
            Pump();
            answer = DialogResult.Yes;
            form.ClickMenuForTesting("File", "Close Filters");
            Pump();
            ok &= Check("answering \"yes\" closes them too", Filters() == 0 && form.FilterFileForTesting is null);
            var saved = CascadeFile.Load(filters).Filters;
            ok &= Check("having written the change out first",
                        saved.Roots.Count == 1 && !saved.Roots[0].Enabled,
                        $"{saved.Roots.Count} filters, first enabled = {(saved.Roots.Count > 0 ? saved.Roots[0].Enabled : (bool?)null)}");

            // ---- loading another set over the top is the same loss, so it asks the same question ----

            File.WriteAllText(filters, OriginalFile, new UTF8Encoding(false));
            string other = Path.Combine(Path.GetTempPath(), "cascade_st_close_" + Guid.NewGuid().ToString("N") + ".cascade");
            File.WriteAllText(other, """
                { "filters": [ { "id": "f2", "enabled": true, "matchType": "Text", "text": "WARN" } ] }
                """, new UTF8Encoding(false));
            try
            {
                answer = DialogResult.No;
                form.LoadFiltersForTesting(filters);
                Pump();
                form.ClickMenuForTesting("Filters", "Disable All");
                Pump();
                string Pattern() => doc.Filters.Roots.Count > 0 ? doc.Filters.Roots[0].Match.Text : "(none)";
                ok &= Check($"a fresh unsaved change to start from ({Pattern()}, dirty {form.FiltersAreDirtyForTesting})",
                            form.FiltersAreDirtyForTesting && Pattern() == "ERROR");

                // The file being loaded is the one already open. Deliberately not a special case: it is
                // asked about like any other, saved if that is the answer, and then read back.
                answer = DialogResult.Cancel;
                form.LoadFiltersForTesting(filters);
                Pump();
                ok &= Check("re-opening the file already open asks, and cancelling leaves the change alone",
                            form.FiltersAreDirtyForTesting && !doc.Filters.Roots[0].Enabled,
                            $"dirty {form.FiltersAreDirtyForTesting}, enabled {doc.Filters.Roots[0].Enabled}");
                ok &= Check("and writes nothing",
                            CascadeFile.Load(filters).Filters.Roots[0].Enabled);

                answer = DialogResult.Yes;
                form.LoadFiltersForTesting(filters);
                Pump();
                ok &= Check("saying yes writes the change out and then reads the same file back",
                            !form.FiltersAreDirtyForTesting && !doc.Filters.Roots[0].Enabled
                            && !CascadeFile.Load(filters).Filters.Roots[0].Enabled,
                            $"dirty {form.FiltersAreDirtyForTesting}, on screen {doc.Filters.Roots[0].Enabled}, " +
                            $"on disk {CascadeFile.Load(filters).Filters.Roots[0].Enabled}");

                // ...and a DIFFERENT file goes down exactly the same path.
                form.ClickMenuForTesting("Filters", "Enable All");
                Pump();
                answer = DialogResult.Cancel;
                form.LoadFiltersForTesting(other);
                Pump();
                ok &= Check("cancelling stops a different file being opened too",
                            form.FilterFileForTesting == filters && Pattern() == "ERROR",
                            $"{form.FilterFileForTesting}, {Pattern()}");

                answer = DialogResult.No;
                form.LoadFiltersForTesting(other);
                Pump();
                ok &= Check($"and \"no\" opens it without saving what was on screen ({Pattern()})",
                            form.FilterFileForTesting == other && Pattern() == "WARN");
                ok &= Check("leaving the file it came from as it was on disk",
                            !CascadeFile.Load(filters).Filters.Roots[0].Enabled);

                // The report was about the Recent Filter Files menu, so drive that rather than the method
                // behind it - every way in has to reach the same guard.
                form.ClickMenuForTesting("Filters", "Enable All");
                Pump();
                answer = DialogResult.Cancel;
                ok &= Check("the recent filter files menu lists the file",
                            form.ClickMenuForTesting("File", "Recent Filter Files", filters));
                Pump();
                ok &= Check("and going back to one through it asks before throwing the change away",
                            form.FilterFileForTesting == other && form.FiltersAreDirtyForTesting,
                            $"{form.FilterFileForTesting}, dirty {form.FiltersAreDirtyForTesting}");
            }
            finally { try { File.Delete(other); } catch { /* ignore */ } }

            form.AnswerSavePromptForTesting = () => DialogResult.No;
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            try { File.Delete(log); } catch { /* ignore */ }
            try { File.Delete(filters); } catch { /* ignore */ }
        }
    }
}
