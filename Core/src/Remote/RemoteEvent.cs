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
    /// Gets the workspace the event is about, or <see cref="Guid.Empty" /> for <see cref="RemoteEventKind.WorkspacesChanged" />, which is about all of them, and for <see cref="RemoteEventKind.SwarmChanged" /> and <see cref="RemoteEventKind.SwarmTaskChanged" />.
    /// </summary>
    public required Guid WorkspaceId { get; init; }

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
    /// Gets the folder whose entries changed, relative to the workspace folder, for <see cref="RemoteEventKind.FilesChanged" />, or the path of the run for <see cref="RemoteEventKind.ScriptOutput" /> and <see cref="RemoteEventKind.ScriptRemoved" />.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the run that started or changed state, without its output, for <see cref="RemoteEventKind.ScriptChanged" />.
    /// </summary>
    public ScriptRun? Script { get; init; }

    /// <summary>
    /// Gets the loop that runs in the workspace, for <see cref="RemoteEventKind.LoopChanged" />, or <see langword="null" /> when the loop ended.
    /// </summary>
    public LoopInfo? Loop { get; init; }

    /// <summary>
    /// Gets the saved inputs of the workspace, the last saved first, for <see cref="RemoteEventKind.SavedInputsChanged" />.
    /// </summary>
    public IReadOnlyList<SavedInput>? SavedInputs { get; init; }

    /// <summary>
    /// Gets the errors of the workspace, the newest first, for <see cref="RemoteEventKind.ErrorsChanged" />.
    /// </summary>
    public IReadOnlyList<ErrorEntry>? Errors { get; init; }

    /// <summary>
    /// Gets whether the workspace now shows the AI's work, for <see cref="RemoteEventKind.ShowWorkChanged" />.
    /// </summary>
    public bool? ShowWork { get; init; }

    /// <summary>
    /// Gets the automatic pause the workspace is in, for <see cref="RemoteEventKind.LimitPauseChanged" />; <see langword="null" /> when the pause ended.
    /// </summary>
    public LimitPause? LimitPause { get; init; }

    /// <summary>
    /// Gets the work of the workspace that waits for the local AI, for <see cref="RemoteEventKind.QueueChanged" />; <see langword="null" /> when nothing waits any more.
    /// </summary>
    public QueueStatus? Queue { get; init; }

    /// <summary>
    /// Gets the delegated task <see cref="RemoteEventKind.SwarmTaskChanged" /> is about.
    /// </summary>
    public Guid? TaskId { get; init; }

    /// <summary>
    /// Gets a value indicating whether the piece of text of a <see cref="RemoteEventKind.MessageDelta" /> is the reasoning of the AI and not its answer; omitted for the answer.
    /// </summary>
    public bool? Thought { get; init; }

    /// <summary>
    /// Gets the text a run wrote since the last event, for <see cref="RemoteEventKind.ScriptOutput" />; <see cref="Path" /> says which run. For <see cref="RemoteEventKind.LoopChanged" /> it says why a loop ended by itself. For <see cref="RemoteEventKind.FilesChanged" /> it is <c>all</c> when too much changed to name the folders, so every folder may have changed.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets the failure message, for <see cref="RemoteEventKind.Failed" />.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets when the event happened, for <see cref="RemoteEventKind.Failed" />, so that every client shows the same time for the failure.
    /// </summary>
    public DateTimeOffset? Time { get; init; }
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
    /// The contents of a folder of a workspace that is open (see <see cref="RemoteCommand.WatchWorkspace" />) changed, whoever changed them.
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
    WorkspacesChanged,

    /// <summary>
    /// A loop of the AI started, made a run, or ended; <see cref="RemoteEvent.Loop" /> is the loop, or empty once it ended, and then <see cref="RemoteEvent.Text" /> says why when it ended by itself.
    /// </summary>
    LoopChanged,

    /// <summary>
    /// A chat input was saved in the workspace or taken out of it by any client; <see cref="RemoteEvent.SavedInputs" /> is the list now.
    /// </summary>
    SavedInputsChanged,

    /// <summary>
    /// The AI of the workspace ended with an error, which the server recorded; <see cref="RemoteEvent.Errors" /> is the history now.
    /// </summary>
    ErrorsChanged,

    /// <summary>
    /// The workspace was set to show or hide the AI's work in its chat; the clients read the history again, which now includes or leaves out the tool calls and results.
    /// </summary>
    ShowWorkChanged,

    /// <summary>
    /// The workspace was paused automatically because a usage window of its provider reached the maximum of the global settings, or the pause changed (it was read again) or ended; <see cref="RemoteEvent.LimitPause" /> is the pause now, or <see langword="null" /> once the AI is no longer held.
    /// </summary>
    LimitPauseChanged,

    /// <summary>
    /// The AI wrote a piece of a message it has not finished, which <see cref="RemoteEvent.Text" /> holds; only sent for a workspace that shows the AI's work. Pieces follow each other in order, and the finished message arrives as a <see cref="MessageAdded" /> event that replaces them. Pieces of a message that is never finished, because the AI stopped or failed, are dropped.
    /// </summary>
    MessageDelta,

    /// <summary>
    /// The work of the workspace that waits for the local AI changed: it started waiting, moved up, changed or no longer waits.
    /// </summary>
    QueueChanged,

    /// <summary>
    /// The swarm changed: its settings, paired servers or tasks; clients that show them read it again. The workspace identifier is empty.
    /// </summary>
    SwarmChanged,

    /// <summary>
    /// The conversation or the state of the delegated task <see cref="RemoteEvent.TaskId" /> changed. The workspace identifier is empty.
    /// </summary>
    SwarmTaskChanged
}
