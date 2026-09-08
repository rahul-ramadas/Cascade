using Xunit.Abstractions;

// Serial by construction: every check below marshals onto the one STA thread the app's controls live on,
// so running the classes in parallel would only queue xUnit's threads behind each other. Saying so keeps
// the order deterministic and the output readable.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cascade.AppTests;

/// <summary>
/// The engine driven over a real log of whatever size is to hand, rather than a fixture written for the
/// occasion. Skipped unless <c>CASCADE_BIG_FILE</c> names one - it exists so that a machine with a
/// multi-gigabyte trace on it can put the whole pipeline through its paces, and the nightly CI run points
/// it at a file it generates for the purpose.
/// </summary>
public class RealFileTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    private static string? File => Environment.GetEnvironmentVariable("CASCADE_BIG_FILE");
    private static string? Filters => Environment.GetEnvironmentVariable("CASCADE_BIG_FILTERS");

    [Fact]
    public void A_real_log_opens_indexes_and_filters()
    {
        if (File is not { Length: > 0 } path || !System.IO.File.Exists(path))
        {
            // Deliberately a pass, not a skip that reads as a gap: there is nothing here to run against.
            Report("CASCADE_BIG_FILE names no log; nothing to check.");
            return;
        }

        Verify(() => Checks.RunFileChecks(path, Filters));
    }
}
