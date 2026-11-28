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
    /// Gets the delegated task when this row is one, shown under the workspace that delegated it while it has not ended.
    /// </summary>
    public SwarmTaskInfo? DelegatedTask { get; init; }

    /// <summary>
    /// Gets who delegated the task the workspace runs, when it runs one.
    /// </summary>
    public string? DelegatedBy { get; init; }

    /// <summary>
    /// Gets a value indicating whether this row is a delegated task.
    /// </summary>
    public bool IsTask => DelegatedTask is not null;

    /// <summary>
    /// Gets a value indicating whether a header's workspaces are hidden.
    /// </summary>
    public bool IsCollapsed { get; init; }

    /// <summary>
    /// Gets how many groups the row is inside.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    /// Gets a value indicating whether the row is highlighted as working: a workspace in any state but <see cref="WorkspaceState.Ready" />, or a group holding one, however deep.
    /// </summary>
    public bool IsWorking { get; init; }

    /// <summary>
    /// Gets a value indicating whether a loop of the AI runs in the workspace, which shows a loop icon next to its name.
    /// </summary>
    public bool IsLooping { get; init; }

    /// <summary>
    /// Gets a value indicating whether work of the workspace waits for the local AI.
    /// </summary>
    public bool IsQueued { get; init; }

    /// <summary>
    /// Gets the line that says what a working workspace takes, the same one for as long as the list shows the workspace.
    /// </summary>
    public ResourceBadge? Resources { get; init; }

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
    public string Text => Group is { } group ? $"{(IsCollapsed ? "▸" : "▾")} {group.Name}" : DelegatedTask is { } task ? $"\u21B3 {task.Title}" : Workspace?.Name ?? string.Empty;

    /// <summary>
    /// Gets the secondary line of a row: for a workspace its folder on the server, after notes that its work waits for the local AI or that another server delegated it, and for a delegated task where it stands.
    /// </summary>
    public string Detail => DelegatedTask is { } task ? new SwarmTaskViewModel(task).Detail : Workspace is null ? string.Empty : string.Join(" - ", new[] { IsQueued ? "Waiting for Local AI" : null, DelegatedBy is { } from ? $"Delegated by {from}" : null, Workspace.Folder }.Where(part => part is not null));

    /// <summary>
    /// Gets the row's left margin.
    /// </summary>
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
}
