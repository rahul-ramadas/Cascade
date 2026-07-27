using System.Diagnostics;

namespace Cascade.UiTests;

/// <summary>
/// The headless screenshot harness (<c>Cascade.exe --screens</c>) builds a real MainForm, so any modal prompt
/// raised while it runs blocks it forever with nobody to answer. That happened for real: it loaded the
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
}
