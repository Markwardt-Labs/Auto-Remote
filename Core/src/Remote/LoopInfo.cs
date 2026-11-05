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
}
