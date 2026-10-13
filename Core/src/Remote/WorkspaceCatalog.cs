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
}
