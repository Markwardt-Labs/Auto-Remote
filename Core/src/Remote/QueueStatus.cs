namespace Markwardt.AutoRemote;

/// <summary>
/// Work of a workspace that waits for the local AI to be free.
/// </summary>
public sealed record QueueStatus
{
    /// <summary>
    /// Gets the place of the work in the queue, 1 being the next to start, in the order work starts.
    /// </summary>
    public required int Position { get; init; }

    /// <summary>
    /// Gets the prompt that waits, with the lines of its attachments at the end; empty when what waits is not a prompt, such as writing a commit message.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets the names of the workspaces whose local AI work runs now, which the waiting work waits for.
    /// </summary>
    public IReadOnlyList<string> Running { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether what waits is a task another server delegated.
    /// </summary>
    public bool Delegated { get; init; }
}
