namespace Markwardt.AutoRemote;

/// <summary>
/// A loop of the AI that is running in a workspace.
/// </summary>
public sealed record LoopInfo
{
    /// <summary>
    /// Gets what the loop was started with.
    /// </summary>
    public required LoopSettings Settings { get; init; }

    /// <summary>
    /// Gets when the loop was started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Gets how many runs the loop has started.
    /// </summary>
    public int Runs { get; init; }

    /// <summary>
    /// Gets the earliest time the next prompt is sent, which is the interval after the start of the last run, or <see langword="null" /> before the first run. The prompt waits further when the AI or the workspace is still busy then.
    /// </summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>
    /// Gets when the loop next looks at the usage limit while it waits because a usage window reached its limit and the AI cannot be prompted, or <see langword="null" /> when it does not wait. The loop stays alive meanwhile, checks every 15 minutes and goes on by itself once prompting works again.
    /// </summary>
    public DateTimeOffset? LimitCheckAt { get; init; }

    /// <summary>
    /// Gets whether a run of the loop is under way, from when its prompt is sent until its work is over. Between the runs, other work of the workspace goes on as without a loop.
    /// </summary>
    public bool IsRunning { get; init; }
}
