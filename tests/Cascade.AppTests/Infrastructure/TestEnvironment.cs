using System.Runtime.CompilerServices;
using System.Text;

namespace Cascade.AppTests;

/// <summary>
/// Process-wide setup, done once, before any test in this assembly runs.
///
/// <para>The most important thing here is the settings directory. These checks build real
/// <c>MainForm</c>s, and a window saves preferences and the recent-file lists as it goes - on its refresh
/// timer as well as on the way out - so a run pointed at the developer's own configuration would write
/// the empty state each test window was constructed with straight over their recent files. That has
/// happened for real. A test must not depend on the code under test to protect the machine it runs on.
/// </para>
/// </summary>
internal static class TestEnvironment
{
    private static string? _settingsDir;

    [ModuleInitializer]
    internal static void Initialise()
    {
        _settingsDir = Path.Combine(Path.GetTempPath(), "cascade_apptests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_settingsDir);
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", _settingsDir);

        // Nothing here wants a release check: it would reach the network, and on a build that reports a
        // development version it would also be entitled to replace the executable under test.
        Environment.SetEnvironmentVariable("CASCADE_UPDATE", "off");

        // Program.Main does this before anything reads a file. Without it the code-page encodings the
        // encoding menu offers - Windows-1252 among them - do not exist.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(_settingsDir, recursive: true); } catch { /* best effort */ }
        };
    }

    /// <summary>The throwaway directory this run's settings and state live in.</summary>
    public static string SettingsDir => _settingsDir
        ?? throw new InvalidOperationException("The module initialiser has not run.");
}
