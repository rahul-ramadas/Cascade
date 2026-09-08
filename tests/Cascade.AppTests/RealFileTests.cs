using Xunit.Abstractions;

// Serial by construction: every check below marshals onto the one STA thread the app's controls live on,
// so running the classes in parallel would only queue xUnit's threads behind each other. Saying so keeps
// the order deterministic and the output readable.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cascade.AppTests;

/// <summary>
/// The engine driven over a log that exists for its own reasons, rather than a fixture written for the
/// occasion. <c>CASCADE_BIG_FILE</c> names it; <c>CASCADE_BIG_FILTERS</c> may name a .tat to import as
/// well. The nightly build finds a real log on the runner and points them here, and a machine with a
/// multi-gigabyte trace on it can be pointed at that instead.
///
/// <para>This is not the same job as <c>SoakTests</c>, the other big-file test: that one runs over a log
/// whose contents are arithmetic, so it can say exactly what every answer ought to be. This one runs over
/// data nobody chose, where the value is precisely that no expected answer exists - the decoder, the line
/// index and the row mapping are held against an independent read of the same bytes instead. Between them
/// they cover "the answers are right" and "nothing here assumed what a log looks like".</para>
///
/// <para>WITH NO LOG NAMED IT REPORTS SKIPPED, NOT PASSED. It used to return quietly, and the effect of
/// that was worse than it sounds: nothing set the variable - not this repository, not either workflow -
/// so a test that had never once run reported green on every build for as long as it had existed, in
/// 2.6 milliseconds, and would have gone on doing so if the nightly below were pointed at a path that had
/// since moved. A gap has to read as a gap.</para>
/// </summary>
public class RealFileTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    private static string? NamedLog => Environment.GetEnvironmentVariable("CASCADE_BIG_FILE");
    private static string? NamedFilters => Environment.GetEnvironmentVariable("CASCADE_BIG_FILTERS");

    [SkippableFact]
    public void A_real_log_opens_indexes_and_filters()
    {
        string? log = NamedLog;
        Skip.If(string.IsNullOrWhiteSpace(log),
                "CASCADE_BIG_FILE names no log, so there is nothing real to run against.");

        // Named but absent is a misconfiguration, not an absence, and the two must not look alike: a
        // nightly whose log has moved has to go red rather than quietly stop testing anything.
        Assert.True(File.Exists(log), $"CASCADE_BIG_FILE names {log}, which is not there.");
        string? filters = NamedFilters;
        Assert.True(string.IsNullOrWhiteSpace(filters) || File.Exists(filters),
                    $"CASCADE_BIG_FILTERS names {filters}, which is not there.");

        Report($"Running against {log} ({new FileInfo(log!).Length:N0} bytes).");

        // Eight without a filter file, nine with one. The lower figure, because naming one is optional.
        Verify(() => Checks.RunFileChecks(log!, string.IsNullOrWhiteSpace(filters) ? null : filters),
               atLeast: 8);
    }
}
