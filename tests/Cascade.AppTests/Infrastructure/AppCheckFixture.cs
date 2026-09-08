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

    /// <summary>
    /// Runs one group of checks on the UI thread and reports the result.
    ///
    /// <para><paramref name="atLeast"/> is how many checks the group must be seen to make. Passing is not
    /// the same as having looked: a fixture that quietly stops matching, a loop over a collection that has
    /// become empty, or an early return added while chasing something else all leave a group reporting
    /// true having examined nothing, and the suite goes green over a subject nobody is testing any more.
    /// MEASURED, by returning early from one group: it passed in 13 ms where the real run takes 289, and
    /// twenty-one checks disappeared without a trace. So the count is the assertion that the checks below
    /// this one still exist.</para>
    ///
    /// <para>It is a FLOOR and not the exact number on purpose. Adding a check should cost nothing, and
    /// taking one away should have to be deliberate - the count is here to catch checks going missing, not
    /// to freeze how many there are. Every figure is what the group really runs today, so any loss at all
    /// is caught; raise it when a group grows if the new checks are worth the same protection.</para>
    /// </summary>
    protected void Verify(Func<bool> group, int atLeast)
    {
        var lines = new List<string>();
        bool ok;
        try
        {
            ok = Sta.Run(() =>
            {
                Checks.CaptureInto(lines);
                try { return group(); }
                finally { Checks.CaptureInto(null); Foreground.Release(); }
            });
        }
        finally
        {
            // Printed even when the group threw. Sta.Run marshals the exception back, and without this the
            // checks that had already run - i.e. how far it got - would go with it.
            foreach (string line in lines) _output.WriteLine(line);
        }

        if (!ok)
        {
            var failed = lines.Where(l => l.StartsWith(Checks.FailMarker, StringComparison.Ordinal)).ToList();
            throw new XunitException(failed.Count > 0
                ? string.Join(Environment.NewLine, failed)
                : "The group reported failure without recording a failed check.");
        }

        int ran = lines.Count(l => l.StartsWith(Checks.PassMarker, StringComparison.Ordinal)
                                || l.StartsWith(Checks.FailMarker, StringComparison.Ordinal));
        if (ran < atLeast)
            throw new XunitException(
                $"The group passed having made only {ran} of the {atLeast} checks it is supposed to make. " +
                "Passing without looking is not passing: something it needs is no longer there, or a path " +
                "through it now returns early. If checks were removed on purpose, lower the figure at the " +
                "call site in the same commit.");
    }
}
