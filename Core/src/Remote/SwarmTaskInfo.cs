namespace Markwardt.AutoRemote;

/// <summary>
/// A delegated task as clients show it: one this server delegated or one it works on for another server.
/// </summary>
public sealed record SwarmTaskInfo
{
    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the identifier of the job it is part of.
    /// </summary>
    public Guid JobId { get; init; }

    /// <summary>
    /// Gets a value indicating whether another server delegated it to this one.
    /// </summary>
    public bool IsIncoming { get; init; }

    /// <summary>
    /// Gets its title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the name of the other server: the worker of an outgoing task, empty while none has it, or the sender of an incoming one.
    /// </summary>
    public string PeerName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the workspace it belongs to: the one an outgoing task was delegated from, or the workspace an incoming one runs in.
    /// </summary>
    public Guid? WorkspaceId { get; init; }

    /// <summary>
    /// Gets the name of that workspace.
    /// </summary>
    public string WorkspaceName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the capability it needs.
    /// </summary>
    public AiCapability Capability { get; init; }

    /// <summary>
    /// Gets where it stands.
    /// </summary>
    public required SwarmTaskState State { get; init; }

    /// <summary>
    /// Gets why it failed or ended, or <see langword="null" />.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the start of the worker's report once it finished.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the branch of the workspace that holds the result of an outgoing task, when it changed files.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Gets a value indicating whether the result is brought into the workspace by itself once the task is done.
    /// </summary>
    public bool BringIn { get; init; }

    /// <summary>
    /// Gets a value indicating whether bringing the result in is under way or waiting for its turn.
    /// </summary>
    public bool IsBringingIn { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user delegated it by hand, which is not reported back to the AI.
    /// </summary>
    public bool IsManual { get; init; }

    /// <summary>
    /// Gets when it was made.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets when it last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Gets a value indicating whether it has not ended.
    /// </summary>
    [JsonIgnore]
    public bool IsActive => State is SwarmTaskState.Waiting or SwarmTaskState.Pending or SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running;

    /// <summary>
    /// Gets a value indicating whether it can be cancelled.
    /// </summary>
    [JsonIgnore]
    public bool CanCancel => IsActive || State == SwarmTaskState.Lost;

    /// <summary>
    /// Gets a value indicating whether it can be delegated again.
    /// </summary>
    [JsonIgnore]
    public bool CanRetry => !IsIncoming && State is SwarmTaskState.Failed or SwarmTaskState.Cancelled or SwarmTaskState.Lost;

    /// <summary>
    /// Gets a value indicating whether the user can have its result brought into the workspace.
    /// </summary>
    [JsonIgnore]
    public bool CanBringIn => !IsIncoming && State == SwarmTaskState.Done && Branch is not null && !IsBringingIn;

    /// <summary>
    /// Gets a value indicating whether it can be removed from the list, which deletes its result branch.
    /// </summary>
    [JsonIgnore]
    public bool CanDiscard => !IsActive;
}
