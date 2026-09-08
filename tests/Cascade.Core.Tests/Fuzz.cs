namespace Cascade.Core.Tests;

/// <summary>
/// How hard the random tests try.
///
/// <para>A fuzz test with a fixed seed and a few hundred cases is a regression test wearing a fuzz test's
/// clothes: it explores exactly the same ground on every run for ever. Left at scale 1 that is what these
/// are, and deliberately so - a push has to be answered in seconds. The nightly run raises the scale
/// instead, so the same tests spend real time looking somewhere new.</para>
///
/// <para>The seed moves with the scale as well as the count. Multiplying only the iterations makes a longer
/// walk down the same road; changing the seed as well is what lets a nightly run find something a hundred
/// pushes did not, and the failure still names a seed that reproduces it exactly.</para>
/// </summary>
internal static class Fuzz
{
    /// <summary>Multiplier for iteration counts and seeds. 1 unless CASCADE_FUZZ_SCALE says otherwise.</summary>
    public static int Scale { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("CASCADE_FUZZ_SCALE"), out int n) && n > 0
            ? Math.Min(n, 1000)
            : 1;

    /// <summary>Whether this is a scaled-up run, for a test that wants to say so in its output.</summary>
    public static bool IsSoak => Scale > 1;

    /// <summary>How many cases to try: <paramref name="baseline"/> on a push, more at night.</summary>
    public static int Cases(int baseline) => baseline * Scale;

    /// <summary>
    /// A seed that is <paramref name="baseline"/> on a push and something else on a scaled run, so a
    /// nightly explores ground the pushes never reach. Print it in any failure message: with it, the case
    /// reproduces exactly by setting CASCADE_FUZZ_SCALE to the same value.
    /// </summary>
    public static int Seed(int baseline) => Scale == 1 ? baseline : baseline + (Scale * 7919);
}
