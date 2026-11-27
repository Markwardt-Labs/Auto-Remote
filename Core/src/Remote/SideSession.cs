namespace Markwardt.AutoRemote;

/// <summary>
/// What makes a workspace a side session of another one: a copy of the main workspace's folder on a branch of its own, with a chat, scripts and loops of its own, whose AI started with what main's AI knew.
/// </summary>
public sealed record SideSession
{
    /// <summary>
    /// Gets the main workspace it belongs to.
    /// </summary>
    public required Guid ParentId { get; init; }

    /// <summary>
    /// Gets the name of the main workspace.
    /// </summary>
    public string ParentName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name the user gave it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets where it is in its life.
    /// </summary>
    public SideSessionState State { get; init; } = SideSessionState.Ready;

    /// <summary>
    /// Gets what its files started from.
    /// </summary>
    public SideSessionStart StartedFrom { get; init; }

    /// <summary>
    /// Gets a value indicating whether it is integrated by itself once its AI marked its task complete and nothing of it runs any more.
    /// </summary>
    public bool IntegrateWhenFinished { get; init; }

    /// <summary>
    /// Gets what went wrong while it was set up, or <see langword="null" />.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the first task that has not been sent to the AI: it waits for the setup, or the setup or sending it failed; otherwise <see langword="null" />.
    /// </summary>
    public string? UnsentTask { get; init; }
}
