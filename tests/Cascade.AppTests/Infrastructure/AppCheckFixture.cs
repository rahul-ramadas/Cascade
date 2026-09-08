using Xunit.Abstractions;
using Xunit.Sdk;

namespace Cascade.AppTests;

/// <summary>
/// Base class for every group of app checks.
///
/// <para>A group runs many checks and reports all of them, rather than stopping at the first: one run then
/// shows everything that is wrong, which is what makes a change to a shared piece of layout cheap to
/// diagnose. <see cref="Verify"/> is what turns that into an xUnit result - it runs the group on the STA
/// thread, prints every line it wrote, and fails with just the ones that did not pass.</para>
/// </summary>
public abstract class AppCheckFixture
{
    private readonly ITestOutputHelper _output;

    protected AppCheckFixture(ITestOutputHelper output) => _output = output;

    /// <summary>Says something about the run itself, rather than about a check.</summary>
    protected void Report(string text) => _output.WriteLine(text);

    /// <summary>Runs one group of checks on the UI thread and reports the result.</summary>
    protected void Verify(Func<bool> group)
    {
        var lines = new List<string>();
        bool ok = Sta.Run(() =>
        {
            Checks.CaptureInto(lines);
            try { return group(); }
            finally { Checks.CaptureInto(null); }
        });

        foreach (string line in lines) _output.WriteLine(line);
        if (ok) return;

        var failed = lines.Where(l => l.StartsWith(Checks.FailMarker, StringComparison.Ordinal)).ToList();
        throw new XunitException(failed.Count > 0
            ? string.Join(Environment.NewLine, failed)
            : "The group reported failure without recording a failed check.");
    }
}
