namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of the workspaces file: the groups and the workspaces, each in its custom order.
/// </summary>
internal sealed record StoredCatalog
{
    /// <summary>
    /// Gets the groups.
    /// </summary>
    public IReadOnlyList<WorkspaceGroup> Groups { get; init; } = [];

    /// <summary>
    /// Gets the workspaces.
    /// </summary>
    public IReadOnlyList<StoredWorkspace> Workspaces { get; init; } = [];
}
