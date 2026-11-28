namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What the server keeps about a side session with its workspace: what the clients are told, and where its files are, which is only ever removed as recorded here.
/// </summary>
internal sealed record StoredSideSession
{
    /// <summary>
    /// Gets the main workspace.
    /// </summary>
    public required Guid ParentId { get; init; }

    /// <summary>
    /// Gets the name the user gave it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the AI service of the session, which is the main workspace's.
    /// </summary>
    public required AiService Service { get; init; }

    /// <summary>
    /// Gets where it is in its life.
    /// </summary>
    public required SideSessionState State { get; init; }

    /// <summary>
    /// Gets what its files started from.
    /// </summary>
    public required SideSessionStart StartedFrom { get; init; }

    /// <summary>
    /// Gets the folder side sessions were stored in when it was made, which its folder lies in.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>
    /// Gets the folder of its files.
    /// </summary>
    public required string Folder { get; init; }

    /// <summary>
    /// Gets the commit main had checked out when it was made.
    /// </summary>
    public string StartCommit { get; init; } = string.Empty;

    /// <summary>
    /// Gets the tree of the files it started with, which what it changed is measured against.
    /// </summary>
    public string BaseTree { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether it is brought into the main workspace by itself once its work is done.
    /// </summary>
    public bool IntegrateWhenFinished { get; init; }

    /// <summary>
    /// Gets a value indicating whether its work ended well and it was not brought in yet.
    /// </summary>
    public bool MarkedComplete { get; init; }

    /// <summary>
    /// Gets a value indicating whether the changes are committed after it was brought in.
    /// </summary>
    public bool CommitBringIn { get; init; } = true;

    /// <summary>
    /// Gets what went wrong while it was set up.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the first task that was not sent because the setup failed.
    /// </summary>
    public string? UnsentTask { get; init; }

    /// <summary>
    /// Gets the integration under way, while it is integrating.
    /// </summary>
    public StoredIntegration? Integration { get; init; }
}
