namespace Markwardt.AutoRemote;

/// <summary>
/// Everything the server knows about how workspaces are organized: the groups and the workspaces.
/// </summary>
public sealed record WorkspaceCatalog
{
    /// <summary>
    /// Gets the groups, ordered by name.
    /// </summary>
    public required IReadOnlyList<WorkspaceGroup> Groups { get; init; }

    /// <summary>
    /// Gets the workspaces, ordered by name.
    /// </summary>
    public required IReadOnlyList<WorkspaceInfo> Workspaces { get; init; }
}
