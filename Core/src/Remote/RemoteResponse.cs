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
    /// Gets the workspace answering <see cref="RemoteCommand.CreateWorkspace" />, <see cref="RemoteCommand.CloneWorkspace" />, <see cref="RemoteCommand.RenameWorkspace" />, <see cref="RemoteCommand.MoveWorkspace" />, <see cref="RemoteCommand.MoveWorkspaceToFolder" />, <see cref="RemoteCommand.SetShowWork" />, <see cref="RemoteCommand.CreateSideSession" /> or <see cref="RemoteCommand.RenameSideSession" />.
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
    /// Gets the state answering <see cref="RemoteCommand.GetWorkspaceState" />.
    /// </summary>
    public WorkspaceState? State { get; init; }

    /// <summary>
    /// Gets the usage limits answering <see cref="RemoteCommand.GetUsage" />.
    /// </summary>
    public UsageStatus? Usage { get; init; }

    /// <summary>
    /// Gets the loop answering <see cref="RemoteCommand.GetLoop" />, or <see langword="null" /> when none runs.
    /// </summary>
    public LoopInfo? Loop { get; init; }

    /// <summary>
    /// Gets the settings answering <see cref="RemoteCommand.GetWorkspaceSettings" /> or <see cref="RemoteCommand.SetWorkspaceSettings" />.
    /// </summary>
    public WorkspaceSettings? Settings { get; init; }

    /// <summary>
    /// Gets the environment answering <see cref="RemoteCommand.GetAiEnvironment" /> or <see cref="RemoteCommand.SetAiEnvironment" />, without any API key.
    /// </summary>
    public AiEnvironment? AiEnvironment { get; init; }

    /// <summary>
    /// Gets the names answering <see cref="RemoteCommand.ListLocalModels" /> or <see cref="RemoteCommand.ListImageModels" />.
    /// </summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>
    /// Gets the git state answering <see cref="RemoteCommand.GetGitStatus" />.
    /// </summary>
    public GitStatus? Git { get; init; }

    /// <summary>
    /// Gets the changed paths answering <see cref="RemoteCommand.GetGitChanges" />, <see cref="RemoteCommand.GetGitCommitChanges" /> or <see cref="RemoteCommand.GetSessionChanges" />.
    /// </summary>
    public GitChanges? GitChanges { get; init; }

    /// <summary>
    /// Gets the history chunk answering <see cref="RemoteCommand.GetGitHistory" />.
    /// </summary>
    public GitHistory? GitHistory { get; init; }

    /// <summary>
    /// Gets the diff answering <see cref="RemoteCommand.GetGitDiff" /> or <see cref="RemoteCommand.GetSessionDiff" />.
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

    /// <summary>
    /// Gets the message answering <see cref="RemoteCommand.GenerateCommitMessage" />.
    /// </summary>
    public string? CommitMessage { get; init; }

    /// <summary>
    /// Gets the saved inputs, the queued ones first and then the others, the last saved first, answering <see cref="RemoteCommand.GetSavedInputs" />, <see cref="RemoteCommand.SaveInput" />, <see cref="RemoteCommand.QueueInput" />, <see cref="RemoteCommand.TranscribeSavedInput" />, <see cref="RemoteCommand.DiscardSavedInput" /> or <see cref="RemoteCommand.SetSavedInputAutoCommit" />.
    /// </summary>
    public IReadOnlyList<SavedInput>? SavedInputs { get; init; }

    /// <summary>
    /// Gets the saved input that was taken, answering <see cref="RemoteCommand.TakeSavedInput" />.
    /// </summary>
    public SavedInput? SavedInput { get; init; }

    /// <summary>
    /// Gets the errors of the workspace, the newest first, answering <see cref="RemoteCommand.GetErrors" />.
    /// </summary>
    public IReadOnlyList<ErrorEntry>? Errors { get; init; }

    /// <summary>
    /// Gets the usage window that made the server refuse a <see cref="RemoteCommand.Prompt" />, which it does while a window of the provider is at or past the maximum, unless the request confirmed that the work starts anyway; it comes with an error.
    /// </summary>
    public LimitPause? OverLimit { get; init; }

    /// <summary>
    /// Gets the SVG of the diagram, answering <see cref="RemoteCommand.RenderDiagram" />.
    /// </summary>
    public string? Diagram { get; init; }

    /// <summary>
    /// Gets the work waiting for the local AI, answering <see cref="RemoteCommand.GetQueuedWork" /> and a <see cref="RemoteCommand.Prompt" /> that had to wait, and the work <see cref="RemoteCommand.CancelQueuedWork" /> removed; <see langword="null" /> when nothing waits.
    /// </summary>
    public QueueStatus? Queue { get; init; }

    /// <summary>
    /// Gets the swarm answering the swarm commands.
    /// </summary>
    public SwarmStatus? Swarm { get; init; }

    /// <summary>
    /// Gets the task answering <see cref="RemoteCommand.GetSwarmTask" />.
    /// </summary>
    public SwarmTaskDetail? SwarmTask { get; init; }

    /// <summary>
    /// Gets the report answering <see cref="RemoteCommand.GetResources" />.
    /// </summary>
    public ResourceReport? Resources { get; init; }
}
