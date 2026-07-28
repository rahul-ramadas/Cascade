using System.Diagnostics;

namespace Cascade.UiTests;

/// <summary>
/// The app's headless entry points: the screenshot harness, the self-test, and the command line it
/// advertises. The screenshot harness (<c>Cascade.exe --screens</c>) builds a real MainForm, so any modal
/// prompt raised while it runs blocks it forever with nobody to answer. That happened for real: it loaded the
/// developer's actual settings, auto-loaded their last filter file, dirtied it via <c>/demo</c>, and then hung
/// on "Save changes to filters?" when closing the window. This guards that it always runs to completion.
/// </summary>
public class ScreenshotHarnessTests
{
    [Fact]
    public void Screens_render_runs_to_completion_without_blocking_on_a_dialog()
    {
        string outDir = Path.Combine(Path.GetTempPath(), "cascade_screens_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir); // the harness only accepts an output path that already exists
        var psi = new ProcessStartInfo(TestData.AppExe(), $"--screens \"{outDir}\"") { UseShellExecute = false };
        using var app = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Cascade.exe.");
        try
        {
            Assert.True(app.WaitForExit(120_000),
                "--screens never finished; it is almost certainly blocked on a modal dialog.");
            Assert.Equal(0, app.ExitCode);
            Assert.True(File.Exists(Path.Combine(outDir, "main.png")), "the main-window shot was not produced");
        }
        finally
        {
            try { if (!app.HasExited) app.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// <c>Cascade.exe --selftest</c> checks the engine end to end and round-trips every persisted setting
    /// through an export and import. Running it here keeps those checks honest: they live in the app rather
    /// than in this project, which cannot reference it.
    /// </summary>
    [Fact]
    public void Self_test_passes()
    {
        var psi = new ProcessStartInfo(TestData.AppExe(), "--selftest") { UseShellExecute = false };
        using var app = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Cascade.exe.");
        try
        {
            Assert.True(app.WaitForExit(120_000), "--selftest never finished");
            string log = Path.Combine(Path.GetTempPath(), "cascade_selftest.log");
            string detail = File.Exists(log) ? "\n\n" + File.ReadAllText(log) : "";
            Assert.True(app.ExitCode == 0, $"--selftest failed (exit {app.ExitCode}){detail}");
        }
        finally { try { if (!app.HasExited) app.Kill(entireProcessTree: true); } catch { /* ignore */ } }
    }

    /// <summary>
    /// The help text is the only description of the command line a user gets, so it has to match what the
    /// parser really does. It once claimed switches that were never implemented, which is a worse failure
    /// than having no help at all - hence the negative assertions.
    /// </summary>
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Help_describes_the_real_command_line(string flag)
    {
        var (exit, output) = RunCaptured(flag);

        Assert.Equal(0, exit);
        foreach (string expected in new[]
                 {
                     "/Filters:", "/demo",
                     "--version", "--selftest", "--screens", "--cleanup",
                     "CASCADE_SETTINGS_DIR", "CASCADE_UPDATE"
                 })
            Assert.Contains(expected, output);

        // Parity arguments from the original tool that Cascade does not implement. Advertising one would
        // send a user hunting for a feature that is not there.
        foreach (string absent in new[] { "/Config:", "/Line:", "/Clipboard" })
            Assert.DoesNotContain(absent, output);
    }

    [Fact]
    public void Version_prints_a_parseable_version()
    {
        var (exit, output) = RunCaptured("--version");
        Assert.Equal(0, exit);
        Assert.True(Version.TryParse(output.Trim().Split('+')[0], out _), "not a version: " + output);
    }

    private static (int ExitCode, string Output) RunCaptured(string argument)
    {
        var psi = new ProcessStartInfo(TestData.AppExe()) { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add(argument);
        using var app = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Cascade.exe.");
        string output = app.StandardOutput.ReadToEnd();
        Assert.True(app.WaitForExit(60_000), $"'{argument}' never finished");
        return (app.ExitCode, output);
    }
}
