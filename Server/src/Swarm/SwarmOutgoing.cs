namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A task this server delegates.
/// </summary>
internal sealed class SwarmOutgoing
{
    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the job.
    /// </summary>
    public required Guid JobId { get; init; }

    /// <summary>
    /// Gets the workspace it was delegated from.
    /// </summary>
    public required Guid WorkspaceId { get; init; }

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
    /// Gets or sets the server it is meant for, or <see langword="null" /> for any.
    /// </summary>
    public string? RequestedPeerId { get; set; }

    /// <summary>
    /// Gets or sets the server it was given to.
    /// </summary>
    public string? PeerId { get; set; }

    /// <summary>
    /// Gets or sets the name of that server.
    /// </summary>
    public string PeerName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tasks it builds on.
    /// </summary>
    public List<Guid> DependsOn { get; set; } = [];

    /// <summary>
    /// Gets or sets where it stands.
    /// </summary>
    public SwarmTaskState State { get; set; }

    /// <summary>
    /// Gets or sets how its code travelled to the worker it was given to.
    /// </summary>
    public SwarmMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the first commit of the repository.
    /// </summary>
    public string RepoKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the files of the workspace when it was delegated, as a commit.
    /// </summary>
    public string SnapshotCommit { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the commit the work starts from, which its changes are measured against.
    /// </summary>
    public string BaseCommit { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the package that carries it.
    /// </summary>
    public Guid? MessageId { get; set; }

    /// <summary>
    /// Gets or sets the commit holding its changes.
    /// </summary>
    public string? ResultCommit { get; set; }

    /// <summary>
    /// Gets or sets the branch of the workspace that holds its changes.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Gets or sets the remote its branches were pushed to in the git remote mode.
    /// </summary>
    public string? RemoteUrl { get; set; }

    /// <summary>
    /// Gets or sets the worker's report.
    /// </summary>
    public string? Report { get; set; }

    /// <summary>
    /// Gets or sets why it did not succeed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the results of the tasks it builds on, as taken when they were all done.
    /// </summary>
    public List<SwarmDependency> Dependencies { get; set; } = [];

    /// <summary>
    /// Gets or sets whether its result is brought into the workspace once it is done. A task stored without it waits until the user asks.
    /// </summary>
    public bool BringIn { get; set; }

    /// <summary>
    /// Gets or sets whether the changes are committed after its result was brought in.
    /// </summary>
    public bool CommitBringIn { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the user asked for its result now, without waiting for the rest of its job.
    /// </summary>
    public bool IsAsked { get; set; }

    /// <summary>
    /// Gets or sets the identifier the AI confirms bringing it in with, while that is under way or still to be tried again.
    /// </summary>
    public string? BringInId { get; set; }

    /// <summary>
    /// Gets or sets the turn that is to bring it in now.
    /// </summary>
    public Guid? TurnId { get; set; }

    /// <summary>
    /// Gets or sets how many times the AI was asked to bring it in.
    /// </summary>
    public int Deliveries { get; set; }

    /// <summary>
    /// Gets or sets whether the user delegated it.
    /// </summary>
    public bool IsManual { get; set; }

    /// <summary>
    /// Gets or sets whether it was sent again with the whole history.
    /// </summary>
    public bool IsResent { get; set; }

    /// <summary>
    /// Gets or sets when it was made.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when it last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
