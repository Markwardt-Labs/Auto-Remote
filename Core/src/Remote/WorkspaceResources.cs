namespace Markwardt.AutoRemote;

/// <summary>
/// What a workspace takes, with all of its sessions.
/// </summary>
public sealed record WorkspaceResources
{
    /// <summary>
    /// Gets the workspace, which is its main session.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the name of the workspace.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets whether any of its sessions is working or runs a script. A workspace that is not working but is listed has processes that were left running.
    /// </summary>
    public bool IsWorking { get; init; }

    /// <summary>
    /// Gets what all of its sessions take together.
    /// </summary>
    public required ResourceUsage Total { get; init; }

    /// <summary>
    /// Gets the sessions that are working or still have processes running, the main session first.
    /// </summary>
    public IReadOnlyList<SessionResources> Sessions { get; init; } = [];
}
