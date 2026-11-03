namespace Markwardt.AutoRemote;

/// <summary>
/// When a loop of the AI ends by itself.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoopCondition
{
    /// <summary>
    /// It does not: the loop runs until it is cancelled.
    /// </summary>
    Forever,

    /// <summary>
    /// At a point in time, <see cref="LoopSettings.Until" />.
    /// </summary>
    UntilTime,

    /// <summary>
    /// After it has run for a length of time, <see cref="LoopSettings.DurationSeconds" />.
    /// </summary>
    ForDuration,

    /// <summary>
    /// After a number of runs, <see cref="LoopSettings.Runs" />.
    /// </summary>
    Runs,

    /// <summary>
    /// When a window of the service's usage limits has used a share of its limit, <see cref="LoopSettings.Window" /> and <see cref="LoopSettings.Percent" />.
    /// </summary>
    UntilWindow
}
