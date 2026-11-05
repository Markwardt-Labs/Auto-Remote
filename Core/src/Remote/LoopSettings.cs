namespace Markwardt.AutoRemote;

/// <summary>
/// What a loop of the AI sends, how often, and when it ends.
/// </summary>
public sealed record LoopSettings
{
    /// <summary>
    /// Gets the prompt every run sends to the AI.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Gets the seconds from the start of one run to the start of the next, or zero to run again as soon as the last run is done. A run that takes longer is followed at once.
    /// </summary>
    public int IntervalSeconds { get; init; }

    /// <summary>
    /// Gets when the loop ends by itself.
    /// </summary>
    public LoopCondition Condition { get; init; } = LoopCondition.Forever;

    /// <summary>
    /// Gets the point in time the loop ends, for <see cref="LoopCondition.UntilTime" />; a run in progress then finishes and no other starts.
    /// </summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>
    /// Gets the seconds the loop may run, at most 24 days, for <see cref="LoopCondition.ForDuration" />; a run in progress when they are up finishes and no other starts.
    /// </summary>
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// Gets how many runs the loop makes, for <see cref="LoopCondition.Runs" />.
    /// </summary>
    public int? Runs { get; init; }

    /// <summary>
    /// Gets the name of the usage window to watch, as <see cref="UsagePeriod.Name" /> gives it, for <see cref="LoopCondition.UntilWindow" />.
    /// </summary>
    public string? Window { get; init; }

    /// <summary>
    /// Gets how much of the window's limit must be used, from 1 to 100, for the loop to end, for <see cref="LoopCondition.UntilWindow" />.
    /// </summary>
    public double? Percent { get; init; }
}
