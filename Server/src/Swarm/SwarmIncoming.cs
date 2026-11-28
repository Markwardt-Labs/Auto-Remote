namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A task another server delegated to this one.
/// </summary>
internal sealed class SwarmIncoming
{
    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets or sets the job.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Gets the server that delegated it.
    /// </summary>
    public required string PeerId { get; init; }

    /// <summary>
    /// Gets or sets the name of that server.
    /// </summary>
    public string PeerName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Gets or sets what to do.
    /// </summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what the result is expected to be.
    /// </summary>
    public string ExpectedResult { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the capability it needs.
    /// </summary>
    public AiCapability Capability { get; set; }

    /// <summary>
    /// Gets or sets how its code travels.
    /// </summary>
    public SwarmMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the first commit of the repository.
    /// </summary>
    public string RepoKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the commit the work starts from.
    /// </summary>
    public string BaseCommit { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote the code is fetched from in the git remote mode.
    /// </summary>
    public string? RemoteUrl { get; set; }

    /// <summary>
    /// Gets or sets the results it builds on.
    /// </summary>
    public List<SwarmDependency> Dependencies { get; set; } = [];

    /// <summary>
    /// Gets or sets why the AI said it could not finish it, or <see langword="null" />.
    /// </summary>
    public string? Unfinished { get; set; }

    /// <summary>
    /// Gets or sets where it stands.
    /// </summary>
    public SwarmTaskState State { get; set; }

    /// <summary>
    /// Gets or sets the workspace it runs in.
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>
    /// Gets or sets the name of that workspace.
    /// </summary>
    public string? WorkspaceName { get; set; }

    /// <summary>
    /// Gets or sets the folder of that workspace.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    /// Gets or sets the report of the AI.
    /// </summary>
    public string? Report { get; set; }

    /// <summary>
    /// Gets or sets why it did not succeed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets why it is being cancelled, once it is.
    /// </summary>
    public string? CancelReason { get; set; }

    /// <summary>
    /// Gets or sets the package that carries its result.
    /// </summary>
    public Guid? ResultMessageId { get; set; }

    /// <summary>
    /// Gets or sets whether the AI finished it and only the sending of its changes is left, which a restart must not undo.
    /// </summary>
    public bool IsWorkDone { get; set; }

    /// <summary>
    /// Gets or sets whether its workspace was removed.
    /// </summary>
    public bool IsCleaned { get; set; }

    /// <summary>
    /// Gets or sets when it arrived.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when it last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
