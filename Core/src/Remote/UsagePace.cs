namespace Markwardt.AutoRemote;

/// <summary>
/// How fast a usage limit is being consumed relative to how much of its window has passed.
/// </summary>
public enum UsagePace
{
    /// <summary>
    /// Well under an even pace.
    /// </summary>
    VeryLow,

    /// <summary>
    /// Somewhat under an even pace.
    /// </summary>
    Low,

    /// <summary>
    /// Roughly matching the time passed; also the answer when the reset time is unknown.
    /// </summary>
    Even,

    /// <summary>
    /// Somewhat over an even pace.
    /// </summary>
    High,

    /// <summary>
    /// Well over an even pace, or the limit is used up.
    /// </summary>
    VeryHigh
}
