namespace Cascade.Core.Timing;

/// <summary>What the elapsed column measures FROM.
///
/// <para>These are not three features; they are one column with three origins, which is why they share a
/// column rather than each having one. Two numbers side by side in the same unit, with no header over
/// either, would be two things to tell apart before either could be read.</para>
/// </summary>
public enum ElapsedOrigin
{
    /// <summary>The line above this one ON SCREEN. Depends on what the filters are showing, deliberately:
    /// that is what makes it a latency profile of whatever was selected.</summary>
    PreviousShown = 0,

    /// <summary>The first line of the log that carries a time. "How far into this file am I?"</summary>
    FileStart = 1,

    /// <summary>A line the reader picked out. Measured by TIME rather than by position, so unlike the other
    /// two it does not change when the filters do.</summary>
    Reference = 2
}
