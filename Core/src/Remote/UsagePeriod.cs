namespace Markwardt.AutoRemote;

/// <summary>
/// One usage window of an AI service, such as its session or its week.
/// </summary>
public sealed record UsagePeriod
{
    /// <summary>
    /// Gets how much of the window's limit is used, from 0 to 100.
    /// </summary>
    public required double PercentUsed { get; init; }

    /// <summary>
    /// Gets the length of the window in minutes.
    /// </summary>
    public required int WindowMinutes { get; init; }

    /// <summary>
    /// Gets when the window resets, or <see langword="null" /> if the service did not say.
    /// </summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>
    /// Compares the percent used with the percent of the window that has passed: within 5 points is even, 5 to 15 points under is low and beyond that very low, 5 to 15 over is high and beyond that very high. A used up limit is always very high, and an unknown or passed reset time cannot be judged and is even.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The pace.</returns>
    public UsagePace Pace(DateTimeOffset now)
    {
        if (PercentUsed >= 100)
        {
            return UsagePace.VeryHigh;
        }

        if (ResetsAt is not { } resetsAt || resetsAt <= now || WindowMinutes <= 0)
        {
            return UsagePace.Even;
        }

        TimeSpan window = TimeSpan.FromMinutes(WindowMinutes);
        double passed = Math.Clamp((window - (resetsAt - now)) / window * 100, 0, 100);
        return Math.Round(PercentUsed - passed, 6) switch
        {
            < -15 => UsagePace.VeryLow,
            < -5 => UsagePace.Low,
            <= 5 => UsagePace.Even,
            <= 15 => UsagePace.High,
            _ => UsagePace.VeryHigh
        };
    }
}
