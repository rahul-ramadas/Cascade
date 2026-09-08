using System.Diagnostics;

namespace Cascade.UiTests;

/// <summary>
/// The app's headless entry points: the screenshot harness and the command line it advertises. The
/// screenshot harness (<c>Cascade.exe --screens</c>) builds a real MainForm, so any modal prompt raised
/// while it runs blocks it forever with nobody to answer. That happened for real: it loaded the
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
        string cfg = ThrowawayConfig(psi);
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
            try { Directory.Delete(cfg, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>Points a child at a settings directory of its own. Both of these build real windows, and a
    /// window saves preferences and the recent-file lists as it goes - the app guards against this too, but
    /// a test must not depend on the thing it is testing to protect the developer's own configuration.</summary>
    private static string ThrowawayConfig(ProcessStartInfo psi)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cascade_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        psi.EnvironmentVariables["CASCADE_SETTINGS_DIR"] = dir;
        return dir;
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
                     "--version", "--screens", "--cleanup",
                     "CASCADE_SETTINGS_DIR", "CASCADE_UPDATE"
                 })
            Assert.Contains(expected, output);

        // Parity arguments from the original tool that Cascade does not implement, and one switch that was
        // withdrawn when the app stopped carrying its own test harness. Advertising any of them would send a
        // user hunting for a feature that is not there.
        foreach (string absent in new[] { "/Config:", "/Line:", "/Clipboard", "--selftest" })
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
