namespace Markwardt.AutoRemote;

/// <summary>
/// Says that a workspace runs a task another server delegated, which keeps its user from giving it other work.
/// </summary>
public sealed record WorkspaceDelegation
{
    /// <summary>
    /// Gets the workspace.
    /// </summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>
    /// Gets the name of the server that delegated the task.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// Gets the title of the task.
    /// </summary>
    public required string Title { get; init; }
}
