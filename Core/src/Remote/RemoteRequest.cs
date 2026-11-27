namespace Markwardt.AutoRemote;

/// <summary>
/// One client request, sent as a single JSON line after the handshake.
/// </summary>
public sealed record RemoteRequest
{
    /// <summary>
    /// Gets the client-chosen number echoed in the matching <see cref="RemoteResponse" />, so requests can be in flight together.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Gets the operation to perform.
    /// </summary>
    public required RemoteCommand Command { get; init; }

    /// <summary>
    /// Gets the workspace the command targets, if any.
    /// </summary>
    public Guid? WorkspaceId { get; init; }

    /// <summary>
    /// Gets the group the command targets, if any; for <see cref="RemoteCommand.CreateWorkspace" /> and <see cref="RemoteCommand.CloneWorkspace" /> the group the new workspace is filed in.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// Gets the group that contains the group the command creates or moves, or <see langword="null" /> for the top level.
    /// </summary>
    public Guid? ParentId { get; init; }

    /// <summary>
    /// Gets the saved input that <see cref="RemoteCommand.TakeSavedInput" />, <see cref="RemoteCommand.DiscardSavedInput" /> or <see cref="RemoteCommand.SetSavedInputAutoCommit" /> is about.
    /// </summary>
    public Guid? InputId { get; init; }

    /// <summary>
    /// Gets the settings of the loop for <see cref="RemoteCommand.StartLoop" />.
    /// </summary>
    public LoopSettings? Loop { get; init; }

    /// <summary>
    /// Gets the provider of the workspace for <see cref="RemoteCommand.CreateWorkspace" /> and <see cref="RemoteCommand.CloneWorkspace" />, or <see langword="null" /> for the default of the environment; and the provider whose usage <see cref="RemoteCommand.GetUsage" /> reads.
    /// </summary>
    public AiService? Service { get; init; }

    /// <summary>
    /// Gets the API key the AI session of the new workspace runs on for <see cref="RemoteCommand.CreateWorkspace" /> and <see cref="RemoteCommand.CloneWorkspace" />, or <see langword="null" /> to use the key of the environment or the subscription already signed in. Live AI stores it with the session and the server keeps no copy.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the settings of the workspace for <see cref="RemoteCommand.SetWorkspaceSettings" />.
    /// </summary>
    public WorkspaceSettings? Settings { get; init; }

    /// <summary>
    /// Gets the environment for <see cref="RemoteCommand.SetAiEnvironment" />.
    /// </summary>
    public AiEnvironment? AiEnvironment { get; init; }

    /// <summary>
    /// Gets the settings for <see cref="RemoteCommand.SetSwarm" />.
    /// </summary>
    public SwarmServer? SwarmServer { get; init; }

    /// <summary>
    /// Gets the settings of a paired server for <see cref="RemoteCommand.SetSwarmPeer" />.
    /// </summary>
    public SwarmPeer? SwarmPeer { get; init; }

    /// <summary>
    /// Gets the paired server of <see cref="RemoteCommand.RevokeSwarmPeer" /> and <see cref="RemoteCommand.ConfirmDrivePairing" />.
    /// </summary>
    public string? PeerId { get; init; }

    /// <summary>
    /// Gets the pairing code of <see cref="RemoteCommand.PairWithCode" />.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Gets the <c>host:port</c> of the server <see cref="RemoteCommand.PairWithCode" /> pairs with.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// Gets the task of <see cref="RemoteCommand.DelegateTask" />.
    /// </summary>
    public SwarmTaskRequest? Task { get; init; }

    /// <summary>
    /// Gets the delegated task the task commands name.
    /// </summary>
    public Guid? TaskId { get; init; }

    /// <summary>
    /// Gets whether <see cref="RemoteCommand.CancelQueuedWork" /> keeps the text of the removed prompt as a saved input, as a client does that did not send it.
    /// </summary>
    public bool? Keep { get; init; }

    /// <summary>
    /// Gets what the files of a new side session start from.
    /// </summary>
    public SideSessionStart? Start { get; init; }

    /// <summary>
    /// Gets whether <see cref="RemoteCommand.ConfigureOllamaService" /> turns the optimized context on (<see langword="true" />) or off, whether <see cref="RemoteCommand.SetIntegrateWhenFinished" /> turns that on or off, or whether <see cref="RemoteCommand.SetAutoCommit" /> or <see cref="RemoteCommand.SetSavedInputAutoCommit" /> turns auto commit on or off.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Gets the sudo password for <see cref="RemoteCommand.ConfigureOllamaService" />, or <see langword="null" /> when the server's user needs none. It is only ever passed to <c>sudo</c> on standard input.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets the name the command needs, if any: of a folder, group, side session, branch, tag or attached file, or the git user name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the folder or file path the command needs, if any.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the environment variables <see cref="RemoteCommand.RunScript" /> gives the script, which tell a graphical script which display of the client's machine to open on; the server uses them only for a client on its own machine.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Gets the cursor, taken from <see cref="ChatHistory.Older" />, before which <see cref="RemoteCommand.GetHistory" /> reads, or <see langword="null" /> to read the newest messages.
    /// </summary>
    public string? Before { get; init; }

    /// <summary>
    /// Gets how many messages <see cref="RemoteCommand.GetHistory" /> reads at most, or <see langword="null" /> for the server's default.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Gets how many characters of text <see cref="RemoteCommand.GetHistory" /> reads at most (the chunk always has at least one message), or <see langword="null" /> for the server's default; a chunk is bounded by its text as well as by <see cref="Limit" />, so a conversation of a few long messages and one of many short tool calls load in pieces of about the same weight.
    /// </summary>
    public int? Characters { get; init; }

    /// <summary>
    /// Gets the prompt text for <see cref="RemoteCommand.Prompt" />, the message for <see cref="RemoteCommand.GitCommit" />, the input for <see cref="RemoteCommand.SaveInput" /> or <see cref="RemoteCommand.QueueInput" />, the text to search for, the Mermaid source to draw, the input line for <see cref="RemoteCommand.SendScriptInput" /> or the first task of <see cref="RemoteCommand.CreateSideSession" />.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets the repository URL for <see cref="RemoteCommand.CloneWorkspace" /> or <see cref="RemoteCommand.GitSetRemote" />.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets the branch the command compares with, merges into or rebases onto.
    /// </summary>
    public string? Base { get; init; }

    /// <summary>
    /// Gets the email for <see cref="RemoteCommand.GitSetIdentity" />.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="RemoteCommand.GitCheckout" /> discards the changes that are not committed.
    /// </summary>
    public bool? Force { get; init; }

    /// <summary>
    /// Gets the commit hash for <see cref="RemoteCommand.GetGitCommitChanges" /> or <see cref="RemoteCommand.GetGitDiff" />.
    /// </summary>
    public string? Commit { get; init; }

    /// <summary>
    /// Gets how many of the newest commits <see cref="RemoteCommand.GetGitHistory" /> skips.
    /// </summary>
    public int? Skip { get; init; }

    /// <summary>
    /// Gets how many places <see cref="RemoteCommand.ShiftGroup" /> moves the group: negative moves it up the list, positive down.
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="RemoteCommand.DeleteWorkspace" /> also deletes the workspace's folder.
    /// </summary>
    public bool DeleteFolder { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether <see cref="RemoteCommand.ListFiles" /> also lists the entries that a <c>.filehide</c> file hides.
    /// </summary>
    public bool? ShowHidden { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="RemoteCommand.SearchFiles" /> searches the contents of the files instead of their names.
    /// </summary>
    public bool? SearchContent { get; init; }

    /// <summary>
    /// Gets whether the chat of the workspace shows the AI's work, for <see cref="RemoteCommand.SetShowWork" />.
    /// </summary>
    public bool? ShowWork { get; init; }

    /// <summary>
    /// Gets whether a <see cref="RemoteCommand.Prompt" /> goes ahead although a usage window is at or past the maximum, which the user confirmed; the workspace then ignores the maximum until the work this prompt starts is done.
    /// </summary>
    public bool? IgnoreLimit { get; init; }

    /// <summary>
    /// Gets the base64 file content for <see cref="RemoteCommand.UploadAttachment" /> and the base64 WAV audio for <see cref="RemoteCommand.TranscribeSavedInput" />.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>
    /// Gets whether the changes are committed after the work an input starts: for <see cref="RemoteCommand.SaveInput" /> and <see cref="RemoteCommand.QueueInput" /> what the input is kept with, on when omitted, and for <see cref="RemoteCommand.Prompt" />, when <see langword="true" />, that auto commit is turned on for the work the prompt starts or joins.
    /// </summary>
    public bool? AutoCommit { get; init; }
}
