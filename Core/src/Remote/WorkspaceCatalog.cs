namespace Markwardt.AutoRemote;

/// <summary>
/// Everything the server knows about how workspaces are organized: the groups and the workspaces.
/// </summary>
public sealed record WorkspaceCatalog
{
    /// <summary>
    /// Gets the groups in their custom order, which applies among the groups sharing a parent.
    /// </summary>
    public required IReadOnlyList<WorkspaceGroup> Groups { get; init; }

    /// <summary>
    /// Gets the workspaces in their custom order, which applies among the workspaces sharing a group.
    /// </summary>
    public required IReadOnlyList<WorkspaceInfo> Workspaces { get; init; }

    /// <summary>
    /// Gets the identifiers of the workspaces a loop of the AI runs in right now; later changes arrive as <see cref="RemoteEventKind.LoopChanged" /> events.
    /// </summary>
    public IReadOnlyList<Guid> Looping { get; init; } = [];

    /// <summary>
    /// Gets the identifiers of the workspaces with work waiting for the local AI right now; later changes arrive as <see cref="RemoteEventKind.QueueChanged" /> events.
    /// </summary>
    public IReadOnlyList<Guid> Queued { get; init; } = [];

    /// <summary>
    /// Gets the workspaces that run a task another server delegated.
    /// </summary>
    public IReadOnlyList<WorkspaceDelegation> Delegations { get; init; } = [];
}
