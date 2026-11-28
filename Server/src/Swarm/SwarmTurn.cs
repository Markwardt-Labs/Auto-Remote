namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A turn of a workspace's AI that brings results of delegated tasks in, or tells it that its delegated tasks ended, from when it is put together until it is over. It is stored, so one a restart interrupts keeps its meaning.
/// </summary>
internal sealed class SwarmTurn
{
    /// <summary>
    /// Gets the identifier, which is that of the work and of its activity entry.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the workspace.
    /// </summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>
    /// Gets what kind of work it is.
    /// </summary>
    public ActivitySource Source { get; set; }

    /// <summary>
    /// Gets or sets what the AI is asked.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the changes are committed after it.
    /// </summary>
    public bool Commit { get; set; }

    /// <summary>
    /// Gets or sets whether the AI took it.
    /// </summary>
    public bool IsStarted { get; set; }

    /// <summary>
    /// Gets or sets the tasks it asks to be brought in.
    /// </summary>
    public List<Guid> Tasks { get; set; } = [];

    /// <summary>
    /// Gets or sets the jobs it tells the AI about, each with the revision it tells.
    /// </summary>
    public Dictionary<Guid, int> Jobs { get; set; } = [];
}
