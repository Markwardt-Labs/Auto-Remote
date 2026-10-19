namespace Markwardt.AutoRemote;

/// <summary>
/// Something the server pushes to every connected client without being asked, sent in <see cref="RemoteResponse.Event" /> of a response with id zero.
/// </summary>
public sealed record RemoteEvent
{
    /// <summary>
    /// Gets what happened.
    /// </summary>
    public required RemoteEventKind Kind { get; init; }

    /// <summary>
    /// Gets the workspace the event is about, or <see cref="Guid.Empty" /> for <see cref="RemoteEventKind.WorkspacesChanged" />, which is about all of them.
    /// </summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>
    /// Gets the position of <see cref="Message" /> in the workspace's conversation, for <see cref="RemoteEventKind.MessageAdded" />.
    /// </summary>
    public int? Index { get; init; }

    /// <summary>
    /// Gets the added message, for <see cref="RemoteEventKind.MessageAdded" />.
    /// </summary>
    public ChatMessage? Message { get; init; }

    /// <summary>
    /// Gets the state the workspace entered, for <see cref="RemoteEventKind.StateChanged" />.
    /// </summary>
    public WorkspaceState? State { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI is working, for <see cref="RemoteEventKind.WorkingChanged" />.
    /// </summary>
    public bool? Working { get; init; }

    /// <summary>
    /// Gets the folder that changed relative to the workspace folder for <see cref="RemoteEventKind.FilesChanged" />, or the path of the run for <see cref="RemoteEventKind.ScriptOutput" /> and <see cref="RemoteEventKind.ScriptRemoved" />.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the run that started or changed state, without its output, for <see cref="RemoteEventKind.ScriptChanged" />.
    /// </summary>
    public ScriptRun? Script { get; init; }

    /// <summary>
    /// Gets the text a run wrote since the last event, for <see cref="RemoteEventKind.ScriptOutput" />; <see cref="Path" /> says which run.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets the failure message, for <see cref="RemoteEventKind.Failed" />.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// The kinds of <see cref="RemoteEvent" />.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteEventKind
{
    /// <summary>
    /// A message was added to a workspace's conversation.
    /// </summary>
    MessageAdded,

    /// <summary>
    /// The AI started or stopped working in a workspace.
    /// </summary>
    WorkingChanged,

    /// <summary>
    /// The contents of a folder watched with <see cref="RemoteCommand.WatchFiles" /> changed.
    /// </summary>
    FilesChanged,

    /// <summary>
    /// The AI could not do the work it was given.
    /// </summary>
    Failed,

    /// <summary>
    /// A git action started or finished in a workspace, so its git state may have changed.
    /// </summary>
    GitChanged,

    /// <summary>
    /// A script or task run started or changed state.
    /// </summary>
    ScriptChanged,

    /// <summary>
    /// A running script wrote output.
    /// </summary>
    ScriptOutput,

    /// <summary>
    /// A run was forgotten.
    /// </summary>
    ScriptRemoved,

    /// <summary>
    /// A workspace entered a different <see cref="WorkspaceState" />.
    /// </summary>
    StateChanged,

    /// <summary>
    /// The workspaces or the groups changed on the server: one was created, deleted, renamed, moved, reordered or configured. The event carries no details, so a client lists the workspaces again.
    /// </summary>
    WorkspacesChanged
}
