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
    /// Gets the folder or group name the command needs, if any.
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
    /// Gets the prompt text for <see cref="RemoteCommand.Prompt" />, the message for <see cref="RemoteCommand.GitCommit" /> or the input line for <see cref="RemoteCommand.SendScriptInput" />.
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
    /// Gets the base64 file content for <see cref="RemoteCommand.UploadAttachment" /> and the base64 WAV audio for <see cref="RemoteCommand.Transcribe" />.
    /// </summary>
    public string? Data { get; init; }
}
