namespace Markwardt.AutoRemote;

/// <summary>
/// The server's reply to one <see cref="RemoteRequest" />, sent as a single JSON line.
/// </summary>
public sealed record RemoteResponse
{
    /// <summary>
    /// Gets the <see cref="RemoteRequest.Id" /> this answers.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Gets the failure message, or <see langword="null" /> when the request succeeded.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the groups and workspaces answering <see cref="RemoteCommand.ListWorkspaces" />.
    /// </summary>
    public WorkspaceCatalog? Catalog { get; init; }

    /// <summary>
    /// Gets the workspace answering <see cref="RemoteCommand.CreateWorkspace" />, <see cref="RemoteCommand.CloneWorkspace" />, <see cref="RemoteCommand.RenameWorkspace" />, <see cref="RemoteCommand.ConfigureWorkspace" /> or <see cref="RemoteCommand.MoveWorkspace" />.
    /// </summary>
    public WorkspaceInfo? Workspace { get; init; }

    /// <summary>
    /// Gets the group answering <see cref="RemoteCommand.CreateGroup" /> or <see cref="RemoteCommand.RenameGroup" />.
    /// </summary>
    public WorkspaceGroup? Group { get; init; }

    /// <summary>
    /// Gets the listing answering <see cref="RemoteCommand.ListFolders" /> or <see cref="RemoteCommand.CreateFolder" />.
    /// </summary>
    public FolderListing? Listing { get; init; }

    /// <summary>
    /// Gets the conversation answering <see cref="RemoteCommand.GetHistory" />.
    /// </summary>
    public ChatHistory? History { get; init; }

    /// <summary>
    /// Gets the workspace folder contents answering <see cref="RemoteCommand.ListFiles" />.
    /// </summary>
    public WorkspaceFiles? Files { get; init; }

    /// <summary>
    /// Gets the hits answering <see cref="RemoteCommand.SearchFiles" />, best match first for a name search and in file order for a content search.
    /// </summary>
    public IReadOnlyList<FileSearchResult>? SearchResults { get; init; }

    /// <summary>
    /// Gets the file content answering <see cref="RemoteCommand.ReadFile" />.
    /// </summary>
    public FileContent? Content { get; init; }

    /// <summary>
    /// Gets the usage limits answering <see cref="RemoteCommand.GetUsage" />.
    /// </summary>
    public UsageStatus? Usage { get; init; }

    /// <summary>
    /// Gets the git state answering <see cref="RemoteCommand.GetGitStatus" />.
    /// </summary>
    public GitStatus? Git { get; init; }

    /// <summary>
    /// Gets the changed paths answering <see cref="RemoteCommand.GetGitChanges" /> or <see cref="RemoteCommand.GetGitCommitChanges" />.
    /// </summary>
    public GitChanges? GitChanges { get; init; }

    /// <summary>
    /// Gets the history chunk answering <see cref="RemoteCommand.GetGitHistory" />.
    /// </summary>
    public GitHistory? GitHistory { get; init; }

    /// <summary>
    /// Gets the diff answering <see cref="RemoteCommand.GetGitDiff" />.
    /// </summary>
    public GitDiff? GitDiff { get; init; }

    /// <summary>
    /// Gets the branch comparison answering <see cref="RemoteCommand.GetGitBranchState" />.
    /// </summary>
    public GitBranchState? GitBranch { get; init; }

    /// <summary>
    /// Gets the runs answering <see cref="RemoteCommand.GetScripts" />.
    /// </summary>
    public ScriptRuns? Scripts { get; init; }

    /// <summary>
    /// Gets the event the server pushes; set only on a response with id zero, which answers no request.
    /// </summary>
    public RemoteEvent? Event { get; init; }

    /// <summary>
    /// Gets the server path of the file answering <see cref="RemoteCommand.UploadAttachment" />.
    /// </summary>
    public string? AttachmentPath { get; init; }
}
