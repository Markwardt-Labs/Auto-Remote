namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A row of the session list of a workspace.
/// </summary>
internal sealed record SessionEntry
{
    /// <summary>
    /// Gets the identifier of the session, which for the main session is the workspace's.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the name shown for the session.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets what the session is doing, in a word or two.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets what makes it a side session, or <see langword="null" /> for the main session.
    /// </summary>
    public SideSession? Side { get; init; }

    /// <summary>
    /// Gets a value indicating whether it is the session on screen.
    /// </summary>
    public bool IsShown { get; init; }

    /// <summary>
    /// Gets a value indicating whether it has work under way.
    /// </summary>
    public bool IsWorking { get; init; }

    /// <summary>
    /// Gets a value indicating whether the actions of the row are shown.
    /// </summary>
    public bool IsExpanded { get; init; }

    /// <summary>
    /// Gets a value indicating whether it is the main session.
    /// </summary>
    public bool IsMain => Side is null;

    /// <summary>
    /// Gets a value indicating whether it is a side session, which has actions of its own.
    /// </summary>
    public bool IsSide => Side is not null;

    /// <summary>
    /// Gets a value indicating whether it can be switched to.
    /// </summary>
    public bool CanOpen => Side is null || Side.State == SideSessionState.Ready;

    /// <summary>
    /// Gets a value indicating whether it is being integrated into the main session.
    /// </summary>
    public bool IsIntegrating => Side?.State == SideSessionState.Integrating;

    /// <summary>
    /// Gets a value indicating whether it is ready, so that it can be renamed, integrated and compared with its start.
    /// </summary>
    public bool CanManage => Side?.State == SideSessionState.Ready;

    /// <summary>
    /// Gets a value indicating whether it can be deleted.
    /// </summary>
    public bool CanDelete => Side?.State is SideSessionState.Ready or SideSessionState.Creating;

    /// <summary>
    /// Gets a value indicating whether the row has actions to show.
    /// </summary>
    public bool HasActions => IsSide && (CanManage || CanDelete);

    /// <summary>
    /// Gets a value indicating whether the actions of a ready session are shown.
    /// </summary>
    public bool ShowManage => IsExpanded && CanManage;

    /// <summary>
    /// Gets a value indicating whether the delete action is shown.
    /// </summary>
    public bool ShowDelete => IsExpanded && CanDelete;

    /// <summary>
    /// Gets the label of the switch that integrates the session once it is finished, with a check mark when it is on.
    /// </summary>
    public string WhenFinishedText => Side?.IntegrateWhenFinished == true ? "✓ Integrate When Finished" : "Integrate When Finished";

    /// <summary>
    /// Gets the line under the name.
    /// </summary>
    public string Detail => Side is { IntegrateWhenFinished: true, State: SideSessionState.Ready } ? $"{Status} - Integrates when finished" : Status;
}
