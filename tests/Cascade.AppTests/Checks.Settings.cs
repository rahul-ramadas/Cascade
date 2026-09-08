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

/// <summary>Part of <see cref="Checks"/>: preferences, per-machine state, and the credential the updater is allowed to use.</summary>
internal static partial class Checks
{

    /// <summary>The credential the updater borrows is the user's own git token, carrying the scopes of their
    /// git rather than of this app. CASCADE_UPDATE_API decides where the updater talks, so it decides where
    /// that token would be sent - which makes this rule the whole of its protection.</summary>
    internal static bool RunUpdateCredentialChecks()
    {
        Line("-- the git credential only leaves for github, over https --");

        bool ok = Check("the address the app ships with may have it",
                        AppUpdater.MayUseGitCredential(AppUpdater.DefaultApi), AppUpdater.DefaultApi);
        ok &= Check("and that address is https", AppUpdater.DefaultApi.StartsWith("https://", StringComparison.Ordinal),
                    AppUpdater.DefaultApi);

        // The right host over plain http would put the token on the wire in clear.
        ok &= Check("the same host over http may not", !AppUpdater.MayUseGitCredential("http://api.github.com"));

        // Each of these reads as api.github.com at a glance and none of them is.
        ok &= Check("nor a host that merely starts with it",
                    !AppUpdater.MayUseGitCredential("https://api.github.com.example.org"));
        ok &= Check("nor github worn as a user name",
                    !AppUpdater.MayUseGitCredential("https://api.github.com@example.org"));
        ok &= Check("nor anywhere else at all", !AppUpdater.MayUseGitCredential("https://example.org"));

        // The end-to-end tests point the updater at a loopback stub, which is exactly why the credential
        // cannot simply always be offered: that stub is somebody else's process.
        ok &= Check("nor the loopback stub the tests use",
                    !AppUpdater.MayUseGitCredential("http://127.0.0.1:52000"));

        ok &= Check("case is not a way round it", AppUpdater.MayUseGitCredential("HTTPS://API.GITHUB.COM"));
        ok &= Check("and something that is not an address is refused",
                    !AppUpdater.MayUseGitCredential("api.github.com"));
        return ok;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG message, IntPtr window, uint first, uint last, uint remove);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Posted, not sent: a sent message goes straight to WndProc and skips the key pre-processing
    /// where a control is asked whether it wants the key and the dialog gets it if not.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Exported settings must come back exactly, or carrying them to another machine silently
    /// loses whichever preference was forgotten. Every persisted property is compared, so a newly added one
    /// is covered automatically. The export must also stay free of anything machine-specific, or importing
    /// it elsewhere plants paths that do not exist there.</summary>
    internal static bool RunSettingsChecks()
    {
        Line("-- settings export/import --");
        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var original = new AppSettings
            {
                FontFamily = "Cascadia Mono",
                FontSize = 13.5f,
                ZoomPercent = 130,
                TabSize = 7,
                ShowLineNumbers = false,
                MarkerVisibility = MarkerVisibilityMode.Never,
                FilterListDock = FilterDock.Right,
                ShowFilterList = false,
                FilterListHeightFraction = 0.42,
                FilterListWidthFraction = 0.18,
                ForegroundArgb = Color.Teal.ToArgb(),
                BackgroundArgb = Color.Ivory.ToArgb(),
                AutoLoadLastFilterFile = false
            };

            string file = Path.Combine(dir, "exported.json");
            original.ExportTo(file);

            var restored = new AppSettings();          // starts at defaults
            restored.ImportFrom(file);

            bool ok = true;
            foreach (var p in AppSettings.Persisted)
            {
                object? a = p.GetValue(original), b = p.GetValue(restored);
                bool same = a is IEnumerable<string> left && b is IEnumerable<string> right
                    ? left.SequenceEqual(right)
                    : Equals(a, b);
                ok &= Check($"round-trips {p.Name}", same);
            }

            // Nothing in the export may name this machine. Checked against the state class by reflection so
            // that a property moved into it later cannot quietly start leaking.
            string exported = File.ReadAllText(file);
            foreach (var p in typeof(MachineState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                  .Where(p => p.CanWrite))
                ok &= Check($"export leaves out {p.Name}",
                            !exported.Contains($"\"{p.Name}\"", StringComparison.Ordinal));

            // Something that is not a settings file must be refused, not allowed to wipe the preferences.
            string junk = Path.Combine(dir, "junk.json");
            File.WriteAllText(junk, "definitely not json");
            bool refused = false;
            try { new AppSettings().ImportFrom(junk); }
            catch (InvalidDataException) { refused = true; }
            ok &= Check("refuses a file that is not settings", refused);
            return ok;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Per-machine state lives in its own file and survives a save/load round trip. Importing
    /// someone else's preferences must leave it untouched.</summary>
    internal static bool RunMachineStateChecks()
    {
        Line("-- machine state --");
        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_state_" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("CASCADE_SETTINGS_DIR");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", dir);
        try
        {
            var state = new MachineState { LastFilterFile = @"C:\somewhere\filters.cascade" };
            state.AddRecentFile(@"C:\logs\a.log");
            state.AddRecentFilterFile(@"C:\logs\f.cascade");
            state.Save();

            bool ok = Check("state is a separate file", File.Exists(MachineState.FilePath)
                                                        && MachineState.FilePath != AppSettings.FilePath);

            var reloaded = MachineState.Load();
            ok &= Check("round-trips RecentFiles", reloaded.RecentFiles.SequenceEqual(state.RecentFiles));
            ok &= Check("round-trips RecentFilterFiles", reloaded.RecentFilterFiles.SequenceEqual(state.RecentFilterFiles));
            ok &= Check("round-trips LastFilterFile", reloaded.LastFilterFile == state.LastFilterFile);

            // Importing preferences from another machine must not disturb any of it.
            string exported = Path.Combine(dir, "exported.json");
            new AppSettings { FontFamily = "Cascadia Mono" }.ExportTo(exported);
            new AppSettings().ImportFrom(exported);
            var afterImport = MachineState.Load();
            ok &= Check("import leaves recent files alone", afterImport.RecentFiles.SequenceEqual(state.RecentFiles));
            ok &= Check("import leaves the last filter file alone", afterImport.LastFilterFile == state.LastFilterFile);

            // Upgrading from the single-file layout must carry the old lists across rather than drop them.
            File.Delete(MachineState.FilePath);
            File.WriteAllText(AppSettings.FilePath,
                """{"FontFamily":"Consolas","RecentFiles":["C:\\logs\\old.log"],"LastFilterFile":"C:\\old.cascade"}""");
            var migrated = MachineState.Load();
            ok &= Check("reads state left in an old settings file",
                        migrated.RecentFiles.SequenceEqual([@"C:\logs\old.log"])
                        && migrated.LastFilterFile == @"C:\old.cascade");

            // A file that will not parse is kept, not quietly replaced by defaults on the next save.
            File.WriteAllText(AppSettings.FilePath, """{"FontFamily":"Cascadia Mono","Fon""");
            _ = AppSettings.Load();
            ok &= Check("keeps an unparseable settings file aside",
                        File.Exists(AppSettings.FilePath + ".bad"));
            return ok;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", previous);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
