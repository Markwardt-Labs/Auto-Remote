namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One row of the workspace drawer: either a group header or a workspace, indented by how deep in the group tree it sits.
/// </summary>
internal sealed record DrawerEntry
{
    /// <summary>
    /// Gets the group when this row is a header.
    /// </summary>
    public WorkspaceGroup? Group { get; init; }

    /// <summary>
    /// Gets the workspace when this row is a workspace.
    /// </summary>
    public WorkspaceInfo? Workspace { get; init; }

    /// <summary>
    /// Gets a value indicating whether a header's workspaces are hidden.
    /// </summary>
    public bool IsCollapsed { get; init; }

    /// <summary>
    /// Gets how many groups the row is inside.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    /// Gets a value indicating whether this row is a group header.
    /// </summary>
    public bool IsGroup => Group is not null;

    /// <summary>
    /// Gets a value indicating whether this row is a workspace.
    /// </summary>
    public bool IsWorkspace => Workspace is not null;

    /// <summary>
    /// Gets the row's title: the workspace name, or the group name with a collapse marker.
    /// </summary>
    public string Text => Group is { } group ? $"{(IsCollapsed ? "▸" : "▾")} {group.Name}" : Workspace?.Name ?? string.Empty;

    /// <summary>
    /// Gets the secondary line of a workspace row: its folder on the server.
    /// </summary>
    public string Detail => Workspace?.Folder ?? string.Empty;

    /// <summary>
    /// Gets the row's left margin.
    /// </summary>
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
}
