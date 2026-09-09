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

/// <summary>Part of <see cref="Checks"/>: the engine as the window uses it, and what the app does to the machine it runs on.</summary>
internal static partial class Checks
{
    // The watchdog samples every 250ms, so worst-case detection is the limit plus one of those. A stall has
    // to outlast both with room to spare, and the group sits out four of them.
    private const int StallLimitMs = 400;
    private const int StallMs = 900;

    /// <summary>The hang watchdog: it must stay out of the way until asked for, notice a UI thread that has
    /// stopped answering, leave one dump and one report per episode rather than a pile of them, and tell the
    /// window so when it comes back.
    ///
    /// It is driven through a real <see cref="MainForm"/> rather than the class alone, because the half most
    /// likely to be broken is the wiring: a heartbeat that is never sent looks exactly like a hang, and would
    /// dump the process every few seconds of ordinary use.</summary>
    internal static bool RunHangWatchdogChecks()
    {
        Line("-- hang watchdog --");
        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_hang_" + Guid.NewGuid().ToString("N"));
        string? oldOn = Environment.GetEnvironmentVariable("CASCADE_HANG_WATCHDOG");
        string? oldSeconds = Environment.GetEnvironmentVariable("CASCADE_HANG_SECONDS");
        string? oldDir = Environment.GetEnvironmentVariable("CASCADE_HANG_DIR");
        string? oldDump = Environment.GetEnvironmentVariable("CASCADE_HANG_DUMP");
        Form? probe = null;
        MainForm? form = null;
        try
        {
            // Every one of these is read back below, so the group has to start from nothing: a run launched
            // with the watchdog already switched on would otherwise be checking the environment it inherited
            // rather than what the app defaults to.
            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", null);
            Environment.SetEnvironmentVariable("CASCADE_HANG_SECONDS", null);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DIR", null);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DUMP", null);
            var off = new AppSettings();
            bool ok = Check("nobody is watched unless they ask", !HangWatchdog.IsWanted(off));
            ok &= Check("and the limit is shorter than a freeze anyone would sit through",
                        HangWatchdog.SecondsToWait(off) == 2, $"{HangWatchdog.SecondsToWait(off)}s");
            ok &= Check("a dump leaves the mapped log out unless told otherwise",
                        HangWatchdog.WantedDetail() == DumpDetail.Heap);

            probe = new HiddenForm { Opacity = 0, FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(80, 60) };
            ok &= Check("switched off, nothing is started", HangWatchdog.Start(probe, off) is null);

            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", "1");
            ok &= Check("the environment can turn it on without touching the preferences", HangWatchdog.IsWanted(off));
            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", null);

            // From here the PREFERENCE turns it on, which is how a user would. The limit is set in
            // milliseconds, which only a test can do: the preference's own floor is a whole second, and
            // this group has to sit out four separate stalls at it.
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DIR", dir);
            HangWatchdog.ThresholdMsForTesting = StallLimitMs;
            var settings = new AppSettings { HangWatchdog = true };   // and the shipping dump kind, not a cheap one

            form = new MainForm(settings, new MachineState(), Array.Empty<string>())
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 600),
            };
            form.NoSavePrompt = true;
            form.Show();

            // Answering. Longer than the limit and a sampling interval together, so a heartbeat that never
            // arrived would already have been called a hang - which is what makes this the check on the
            // wiring.
            for (int i = 0; i < 8; i++) { Pump(); Thread.Sleep(100); }
            // "Nothing was recorded" only means something once it is clear something was watching.
            ok &= Check("the preference really did start one", form.WatchingForHangsForTesting);
            ok &= Check("and a window that keeps answering is left alone", Directory.GetFiles(dir).Length == 0,
                        string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

            // Not answering: no pumping at all, on the thread that owns the window. The real thing.
            Thread.Sleep(StallMs);

            // A dump of a process holding a large file takes a second or so to write, so the artefacts do
            // not all appear at once - the report says so until the dump is finished with.
            for (int i = 0; i < 400 && Directory.GetFiles(dir, "cascade_hang_*.txt")
                                                .All(f => File.ReadAllText(f).Contains("still being taken", StringComparison.Ordinal));
                 i++)
                Thread.Sleep(25);

            string[] dumps = Directory.GetFiles(dir, "*.dmp");
            string[] reports = Directory.GetFiles(dir, "cascade_hang_*.txt");
            ok &= Check($"a stalled window is caught ({dumps.Length} dump(s), {reports.Length} report(s))",
                        dumps.Length == 1 && reports.Length == 1);

            if (reports.Length >= 1)
            {
                string text = File.ReadAllText(reports[0]);
                foreach (string line in text.Split('\n'))
                    if (line.Length > 0) Line("   | " + line.TrimEnd());
                ok &= Check("the report says how long it was stuck",
                            text.Contains("stopped answering for", StringComparison.Ordinal), text.Split('\n')[0]);
                ok &= Check("and what the collector was doing, which is what the stacks cannot say",
                            text.Contains("gc pause", StringComparison.Ordinal) &&
                            text.Contains("VERDICT", StringComparison.Ordinal));
                ok &= Check("and how hard the process was having to page",
                            text.Contains("page faults", StringComparison.Ordinal));
                // Whether anything is reading the window through UI Automation decides whether closing a
                // window blocks at all, so a report that does not say is missing the first thing to check.
                ok &= Check("and whether anything is reading the window through automation",
                            text.Contains("automation", StringComparison.Ordinal),
                            text.Split('\n').FirstOrDefault(l => l.Contains("automation", StringComparison.Ordinal))?.Trim()
                            ?? "(not mentioned)");
            }
            if (dumps.Length >= 1)
            {
                byte[] head = new byte[4];
                using (var f = File.OpenRead(dumps[0])) f.ReadExactly(head);
                ok &= Check($"the dump is a real one ({new FileInfo(dumps[0]).Length / 1024:N0} KB)",
                            Encoding.ASCII.GetString(head) == "MDMP", Encoding.ASCII.GetString(head));
            }

            // Coming back: the window is told what was written, so the evidence does not have to be found.
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }
            ok &= Check("and the window says so when it comes back",
                        form.StatusForTesting.Contains("Hang recorded", StringComparison.Ordinal),
                        form.StatusForTesting);

            // The dump a machine will not allow is the whole reason this has a fallback: thread stacks are a
            // fraction of the size and still name the thread that stopped.
            HangWatchdog.RefuseDumpForTesting = flags => flags == HangWatchdog.FlagsForTesting(DumpDetail.Heap);
            int dumpsBefore = Directory.GetFiles(dir, "*.dmp").Length;
            Thread.Sleep(StallMs);
            for (int i = 0; i < 200 && Directory.GetFiles(dir, "*.dmp").Length == dumpsBefore; i++)
            { Pump(); Thread.Sleep(25); }
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }

            string[] afterFallback = Directory.GetFiles(dir, "cascade_hang_*.txt").OrderBy(f => f).ToArray();
            ok &= Check($"a refused heap dump falls back to thread stacks ({Directory.GetFiles(dir, "*.dmp").Length} dumps)",
                        Directory.GetFiles(dir, "*.dmp").Length == dumpsBefore + 1 && afterFallback.Length == 2);
            if (afterFallback.Length == 2)
            {
                string fell = File.ReadAllText(afterFallback[1]);
                Line("   | " + (fell.Split('\n').FirstOrDefault(l => l.Contains("dump ", StringComparison.Ordinal)) ?? "").Trim());
                ok &= Check("and says so rather than pretending it got what it asked for",
                            fell.Contains("refused", StringComparison.Ordinal) &&
                            fell.Contains("thread stacks only", StringComparison.Ordinal));
            }

            // A dump that cannot be taken at all - which is what security software does to this on some
            // machines - must leave a report saying WHY, or the reader has a hang and no evidence at all.
            HangWatchdog.RefuseDumpForTesting = _ => true;
            int before = Directory.GetFiles(dir, "*.dmp").Length;
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }
            Thread.Sleep(StallMs);
            for (int i = 0; i < 120 && Directory.GetFiles(dir, "cascade_hang_*.txt").Length < 3; i++)
            { Pump(); Thread.Sleep(25); }
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }

            string[] afterRefusal = Directory.GetFiles(dir, "cascade_hang_*.txt").OrderBy(f => f).ToArray();
            ok &= Check($"a third stall is recorded even when no dump can be taken ({afterRefusal.Length} reports)",
                        afterRefusal.Length == 3 && Directory.GetFiles(dir, "*.dmp").Length == before);
            if (afterRefusal.Length == 3)
            {
                string refused = File.ReadAllText(afterRefusal[2]);
                Line("   | " + (refused.Split('\n').FirstOrDefault(l => l.Contains("dump ", StringComparison.Ordinal)) ?? "").Trim());
                ok &= Check("and the report says no dump was written",
                            refused.Contains("NO DUMP WRITTEN", StringComparison.Ordinal));
                ok &= Check("and carries what Windows gave as the reason, in words and in numbers",
                            System.Text.RegularExpressions.Regex.IsMatch(refused, @"NO DUMP WRITTEN: \S.*\(\d+\)"),
                            refused.Split('\n').FirstOrDefault(l => l.Contains("NO DUMP", StringComparison.Ordinal))?.Trim()
                            ?? "(no such line)");
            }
            ok &= Check("the reason is explained rather than left as a number",
                        HangWatchdog.Explain(112).Contains("(112)", StringComparison.Ordinal) &&
                        HangWatchdog.Explain(112).Contains("no room", StringComparison.Ordinal),
                        HangWatchdog.Explain(112));
            ok &= Check("and the one security software causes is named for what it is",
                        HangWatchdog.Explain(299).Contains("security software", StringComparison.Ordinal),
                        HangWatchdog.Explain(299));
            // dbghelp reports its failures as HRESULTs, so the one error that really turns up here reads as
            // a large negative number unless it is unwrapped.
            ok &= Check("and an HRESULT from dbghelp reads as the error it wraps",
                        HangWatchdog.Explain(unchecked((int)0x8007012B)).Contains("(299)", StringComparison.Ordinal),
                        HangWatchdog.Explain(unchecked((int)0x8007012B)));
            return ok;
        }
        finally
        {
            HangWatchdog.RefuseDumpForTesting = null;
            HangWatchdog.ThresholdMsForTesting = null;
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            try { probe?.Dispose(); } catch { /* ignore */ }
            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", oldOn);
            Environment.SetEnvironmentVariable("CASCADE_HANG_SECONDS", oldSeconds);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DIR", oldDir);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DUMP", oldDump);
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>UI Automation is answered only when it is asked for. Checked by sending the very message a
    /// client sends, to the real windows the app builds: the mechanism is a window message, so nothing above
    /// it could tell the difference, and a window that still answers would still arm the teardown that
    /// freezes the app for seconds on a machine that inspects thread creation.</summary>
    internal static bool RunAutomationChecks()
    {
        Line("-- automation --");
        string? old = Environment.GetEnvironmentVariable(Automation.Variable);
        try
        {
            // Read back below, so the group has to start from nothing rather than from what it inherited.
            Environment.SetEnvironmentVariable(Automation.Variable, null);
            bool ok = Check("a fresh installation does not answer automation", !new AppSettings().Automation);

            Automation.Configure(false);
            ok &= Check("nor does the app, left alone", !Automation.Wanted);
            Automation.Configure(true);
            ok &= Check("the preference turns it on", Automation.Wanted);

            // Nothing calls Configure on the headless paths, so the answer they get is the bare default.
            using (Automation.ForTesting(null))
                ok &= Check("and a run that never settles the question is silent too", !Automation.Wanted);

            Environment.SetEnvironmentVariable(Automation.Variable, "0");
            Automation.Configure(true);
            ok &= Check("the environment overrules the preference", !Automation.Wanted);
            Environment.SetEnvironmentVariable(Automation.Variable, "1");
            Automation.Configure(false);
            ok &= Check("and does so in both directions", Automation.Wanted);
            Environment.SetEnvironmentVariable(Automation.Variable, null);

            var silent = AskDialogForProviders(automationWanted: false);
            Line($"   | silent: window {silent.Window}, nested {silent.Nested}, older interface {silent.Msaa}");
            ok &= Check("switched off, a window offers nothing", silent.Window == 0);
            // The nested one is the check on the wiring: a dialog's controls are all added after the window
            // has been hooked, so a hook that did not follow them would leave every one of them answering.
            ok &= Check("nor does anything nested inside it", silent.Nested == 0);
            ok &= Check("and the older interface is refused too, or WinForms builds the object anyway",
                        silent.Msaa == 0);

            var answering = AskDialogForProviders(automationWanted: true);
            Line($"   | answering: window {answering.Window}, nested {answering.Nested}, older interface {answering.Msaa}");
            ok &= Check("switched on, a window hands out a provider", answering.Window != 0);
            ok &= Check("and so does a control nested inside it", answering.Nested != 0);
            ok &= Check("and the older interface it can also be reached through", answering.Msaa != 0);

            // The whole point of the warm-up is WHERE the cost lands. Held long enough that a call which
            // waited for it could not possibly come back in time.
            Automation.BeforeWarmUpForTesting = () => Thread.Sleep(600);
            var clock = Stopwatch.StartNew();
            var warmUp = Automation.PayTheStartupCost();
            long handedBack = clock.ElapsedMilliseconds;
            warmUp.Join(10_000);
            ok &= Check($"the startup warm-up does not hold up the thread that starts it ({handedBack} ms)",
                        handedBack < 300);
            return ok;
        }
        finally
        {
            Automation.BeforeWarmUpForTesting = null;
            Environment.SetEnvironmentVariable(Automation.Variable, old);
            Automation.Configure(false);
        }
    }

    /// <summary>Drawing handles have to be given back at a moment we choose, not whenever a collection
    /// happens to run. A font handed to a control is not disposed with it, and a finalizer will get there
    /// eventually - so counting handles proves nothing, and these ask the objects themselves instead.</summary>
    internal static bool RunResourceChecks()
    {
        Line("-- drawing handles are given back --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_gdi_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\n")),
                          new UTF8Encoding(false));

        static bool LetGo(Font font)
        {
            try { _ = font.Height; return false; }
            catch (ArgumentException) { return true; }   // what a disposed Font answers
        }

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new HiddenForm { ClientSize = new Size(600, 400), Opacity = 0, FormBorderStyle = FormBorderStyle.None };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            // Zooming rebuilds every font. There are four of them and a family behind them, and a user
            // holding Ctrl and turning the wheel does this dozens of times a minute.
            var wasRegular = grid.FontForTesting;
            settings.ZoomPercent = 150;
            grid.RebuildFonts();
            // Only the faces are asked about. Disposing the family they were cut from is worth doing, but it
            // cannot be checked this way: GDI+ keeps it alive behind any font still holding it, so it
            // answers happily either way.
            bool ok = Check("rebuilding the fonts lets go of the ones it replaces", LetGo(wasRegular));
            ok &= Check("and makes a working one to draw with", grid.FontForTesting.Height > 0,
                        grid.FontForTesting.Height.ToString());

            // A log view is built per window, and closing one has to give its fonts back at that moment.
            var spare = new LineGridControl();
            spare.Attach(doc, settings);
            var spareFont = spare.FontForTesting;
            spare.Dispose();
            ok &= Check("and closing a log view lets go of its fonts", LetGo(spareFont));

            // The text is drawn straight onto the device context now, through handles of its own: a face
            // per style and a brush per colour. Those are GDI handles, not objects - nothing collects them,
            // and a process that loses ten thousand of them is killed. Zooming rebuilds every face and every
            // paint asks for brushes, so this does both, many times, and counts what the process holds.
            //
            // Every step a different size, not five sizes over and over: the faces are filed under the font
            // they were cut from, and a font that comes round again finds the one it had. Repeating five
            // sizes therefore leaks five handles however long the loop runs - the shape of a real leak but
            // not the size of one. Dragging the zoom the way a reader drags it is what tells them apart.
            int handlesBefore = GdiHandles();
            for (int i = 0; i < 40; i++)
            {
                settings.ZoomPercent = 60 + i * 5;
                grid.RebuildFonts();
                grid.Invalidate();
                grid.Update();
            }
            settings.ZoomPercent = 100;
            grid.RebuildFonts();
            Pump();
            int handlesAfter = GdiHandles();
            ok &= Check($"and drawing through GDI's own handles hands every one of them back " +
                        $"({handlesBefore} held before 40 rebuilds and repaints, {handlesAfter} after)",
                        handlesBefore == 0 || handlesAfter <= handlesBefore + 12);

            // The find bar used to cut a font of its own for the term box, at a different size from the rest
            // of the row. It draws everything in the ambient font now, so there is nothing for it to give
            // back - and nothing on the row may quietly go back to one of its own.
            var find = new FindBar((_, _) => { });
            var privately = AllControls(find).Where(c => !ReferenceEquals(c.Font, find.Font)).ToList();
            ok &= Check("the find bar draws its whole row in one font, which it does not own",
                        privately.Count == 0,
                        string.Join(", ", privately.Select(c => $"{c.GetType().Name} {c.Font.Name} {c.Font.SizeInPoints}pt")));
            find.Dispose();

            using var one = new FilterEditDialog(new Filter { Match = { Text = "x" } }, isNew: true, Array.Empty<Filter>());
            using var two = new FilterEditDialog(new Filter { Match = { Text = "y" } }, isNew: true, Array.Empty<Filter>());
            ok &= Check("and the filter dialog shares one font rather than making another each time",
                        ReferenceEquals(one.FontForTesting, two.FontForTesting));
            return ok;
        }
        finally
        {
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    internal static bool RunEngineChecks()
    {
        Line("-- engine checks (temp file) --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
            sb.Append(i % 4 == 0 ? "ERROR disk " : i % 4 == 1 ? "ERROR net " : "info ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            bool ok = Check("indexed 5000 lines", doc.CompletedLineCount == 5000);

            var error = new Filter { Enabled = false, Match = { Text = "ERROR" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };
            doc.Filters.Add(error);
            doc.Filters.Add(disk, error);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            // disabled parent still constrains: only "ERROR disk" (every 4th) match
            ok &= Check("hierarchical match = 1250", doc.MatchedLineCount == 1250);
            ok &= Check("row0 maps to line 0", doc.RowToLine(0) == 0);
            return ok;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>
    /// The whole document pipeline over a log that exists for its own reasons - a real trace, or whatever
    /// the nightly found on the runner - rather than a fixture written to be tested.
    ///
    /// <para>Nothing here knows what the file says, so there is no expected answer to compare against and
    /// asking the engine what it found would be circular. Every check therefore holds the engine against an
    /// INDEPENDENT READ of the same bytes: <see cref="ReferenceLines"/> decodes the head of the file and
    /// splits it the way the format says lines are split, and the filter is a term taken out of the data
    /// itself so that it is certain to match something. The head is enough to bite - a decoder, an index or
    /// a row mapping that is wrong is wrong from the first page - and it keeps this affordable on a file of
    /// any size.</para>
    ///
    /// <para>The timings and the encoding are reported rather than asserted on: they are what a developer
    /// pointing this at a fifteen-gigabyte trace actually wants to see, and there is no threshold that
    /// would mean the same thing on their machine and on a hosted runner.</para>
    /// </summary>
    internal static bool RunFileChecks(string file, string? tat)
    {
        Line($"-- file checks: {file} --");
        long bytes = new FileInfo(file).Length;
        var total = Stopwatch.StartNew();
        using var doc = new CascadeDocument();
        doc.Open(file);

        var first = Stopwatch.StartNew();
        while (doc.CompletedLineCount == 0 && first.ElapsedMilliseconds < 10000) Thread.Sleep(1);
        Line($"first lines available: {first.ElapsedMilliseconds} ms");

        doc.WaitForIndex();
        Line($"indexed {doc.CompletedLineCount:N0} lines of {bytes:N0} bytes in {total.ElapsedMilliseconds} ms");
        Line($"encoding: {doc.Encoding.WebName}");

        // Everything below reads a line, so an empty document leaves them all vacuous. Fail here rather
        // than report a row of passes that never looked at anything.
        if (!Check($"the log has lines in it ({doc.CompletedLineCount:N0})", doc.CompletedLineCount > 0))
            return false;

        Line("first line: " + Truncate(doc.GetLineText(0), 100));

        var reference = ReferenceLines(file, doc.Encoding, SampleLines);
        bool ok = Check($"the head of the file decodes to lines at all ({reference.Count:N0} read)",
                        reference.Count > 0);
        if (!ok) return false;

        int wrong = 0;
        string firstWrong = "";
        for (int i = 0; i < reference.Count && i < doc.CompletedLineCount; i++)
        {
            string got = doc.GetLineText(i);
            if (string.Equals(got, reference[i], StringComparison.Ordinal)) continue;
            if (wrong++ == 0)
                firstWrong = $"line {i + 1}: file has [{Truncate(reference[i], 60)}], engine says [{Truncate(got, 60)}]";
        }
        ok &= Check($"every one of the first {Math.Min(reference.Count, doc.CompletedLineCount):N0} lines " +
                    "reads back exactly as the file has it",
                    wrong == 0, $"{wrong:N0} differ - {firstWrong}");

        // Splitting on a newline that is then handed back with the line would corrupt every filter and
        // every search against it, and is invisible in a screenshot.
        ok &= Check("no line carries a line ending of its own",
                    !reference.Any(l => l.Contains('\n') || l.Contains('\r')));

        if (tat is not null && File.Exists(tat))
        {
            // Reported, not asserted: a filter file names patterns this log may know nothing about, so the
            // only honest claim is that importing and running it does not fall over.
            var imported = TatImporter.Import(tat);
            doc.SetFilters(imported);
            foreach (var f in imported.Roots.Take(5)) f.Enabled = true;
            int enabled = imported.EnumerateDepthFirst().Count(f => f.Enabled);
            imported.ShowOnlyFilteredLines = true;
            var tsw = Stopwatch.StartNew();
            doc.ApplyFilters();
            WaitFilter(doc, 180000);
            Line($"imported {imported.EnumerateDepthFirst().Count()} filters from {tat}; " +
                 $"{enabled} enabled gave {doc.MatchedLineCount:N0} matches in {tsw.ElapsedMilliseconds} ms");
            ok &= Check("a filter set from a file cannot show more lines than the log has",
                        doc.MatchedLineCount <= doc.CompletedLineCount,
                        $"{doc.MatchedLineCount:N0} of {doc.CompletedLineCount:N0}");
        }

        // A term lifted out of the log itself, so there is certainly something to find whatever the file
        // turns out to be. A term written down here would match nothing on most logs, and a filter that
        // matches nothing makes every check below it pass without looking.
        string term = TermFrom(reference);
        ok &= Check($"the head of the log offers a word to filter on [{term}]", term.Length > 0);
        if (!ok) return false;

        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        filters.Add(new Filter { Enabled = true, Match = { Text = term, CaseSensitive = true } });
        doc.SetFilters(filters);
        var fsw = Stopwatch.StartNew();
        doc.ApplyFilters();
        WaitFilter(doc, 180000);
        Line($"filtered on [{term}]: {doc.MatchedLineCount:N0} matches in {fsw.ElapsedMilliseconds} ms");

        // How many of the lines read independently carry the term is knowable without the engine, and
        // RowAtOrAfterLine says how many rows the view puts before that same line. They are the same
        // question asked of two different things.
        long readThrough = Math.Min(reference.Count, doc.CompletedLineCount);
        long expected = 0;
        for (int i = 0; i < readThrough; i++)
            if (reference[i].Contains(term, StringComparison.Ordinal)) expected++;
        ok &= Check($"the view shows exactly the {expected:N0} lines of the first {readThrough:N0} that carry it",
                    doc.RowAtOrAfterLine(readThrough) == expected,
                    $"the view puts {doc.RowAtOrAfterLine(readThrough):N0} rows before line {readThrough:N0}");

        // Sampled across the whole view, not just the head: this is the mapping the window reads every row
        // it paints, and it has to hold at the far end of a file as well as the near one.
        long rows = doc.RowCount, step = Math.Max(1, rows / SampleRows), last = -1;
        int walked = 0, outOfOrder = 0, unmatched = 0;
        for (long row = 0; row < rows; row += step)
        {
            long line = doc.RowToLine(row);
            if (line <= last) outOfOrder++;
            if (!doc.GetLineText(line).Contains(term, StringComparison.Ordinal)) unmatched++;
            last = line;
            walked++;
        }
        ok &= Check($"row to line climbs over all {rows:N0} rows ({walked:N0} sampled)", outOfOrder == 0,
                    $"{outOfOrder:N0} went backwards");
        ok &= Check($"every one of the {walked:N0} rows sampled is a line that really carries the term",
                    unmatched == 0, $"{unmatched:N0} do not");
        return ok;
    }

    /// <summary>How much of an unknown log to read independently and compare against.</summary>
    private const int SampleLines = 20_000;

    /// <summary>How many rows of the filtered view to walk, spread across all of it.</summary>
    private const int SampleRows = 2_000;

    /// <summary>
    /// The first lines of a file, decoded and split HERE rather than by anything the engine shares.
    ///
    /// <para>Deliberately not <c>StreamReader.ReadLine</c>: that ends a line at a lone carriage return as
    /// well, and Cascade splits on the newline alone (a carriage return before it belongs to the ending,
    /// anything else belongs to the text). A log with a bare CR in it - a progress bar, an embedded
    /// payload - would make the two disagree for a reason that is nothing to do with the engine. This says
    /// what the format says, so a disagreement is a real one.</para>
    /// </summary>
    private static List<string> ReferenceLines(string file, Encoding encoding, int most)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        using var reader = new StreamReader(file, encoding, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[1 << 16];
        int read;
        while (lines.Count < most && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read && lines.Count < most; i++)
            {
                if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                lines.Add(line.ToString());
                line.Clear();
            }

        // A trailing stretch with no newline after it is a line too, but only once the file has ended -
        // otherwise it is however much of the next line the read happened to stop in the middle of.
        if (lines.Count < most && line.Length > 0 && reader.EndOfStream) lines.Add(line.ToString());
        return lines;
    }

    /// <summary>
    /// A word out of the log to filter on. Taken from the line that offers the longest one, so that a file
    /// beginning with a banner, a blank line or a row of dashes still yields something; case-sensitive
    /// letters only, so the term cannot collide with the regex or wildcard meaning of any punctuation.
    /// </summary>
    private static string TermFrom(List<string> lines)
    {
        string best = "";
        foreach (string line in lines.Take(200))
            foreach (string word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (word.Length > best.Length && word.Length <= 24 && word.All(char.IsLetter)) best = word;
        return best;
    }
}
