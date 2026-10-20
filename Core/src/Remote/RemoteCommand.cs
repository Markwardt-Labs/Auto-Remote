namespace Markwardt.AutoRemote;

/// <summary>
/// The operations a client can request once connected.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteCommand
{
    /// <summary>
    /// Lists every workspace and group; answered in <see cref="RemoteResponse.Catalog" />.
    /// </summary>
    ListWorkspaces,

    /// <summary>
    /// Creates a workspace in the existing folder <see cref="RemoteRequest.Path" />; answered in <see cref="RemoteResponse.Workspace" />.
    /// </summary>
    CreateWorkspace,

    /// <summary>
    /// Renames the folder of <see cref="RemoteRequest.WorkspaceId" /> to <see cref="RemoteRequest.Name" />, which renames the workspace; answered in <see cref="RemoteResponse.Workspace" />.
    /// </summary>
    RenameWorkspace,

    /// <summary>
    /// Sets <see cref="RemoteRequest.Service" />, <see cref="RemoteRequest.Model" /> and <see cref="RemoteRequest.Effort" /> for <see cref="RemoteRequest.WorkspaceId" />; answered in <see cref="RemoteResponse.Workspace" />.
    /// </summary>
    ConfigureWorkspace,

    /// <summary>
    /// Moves <see cref="RemoteRequest.WorkspaceId" /> into the group <see cref="RemoteRequest.GroupId" />, or out of any group when that is omitted; answered in <see cref="RemoteResponse.Workspace" />.
    /// </summary>
    MoveWorkspace,

    /// <summary>
    /// Creates a group named <see cref="RemoteRequest.Name" /> inside <see cref="RemoteRequest.ParentId" />; answered in <see cref="RemoteResponse.Group" />.
    /// </summary>
    CreateGroup,

    /// <summary>
    /// Moves the group <see cref="RemoteRequest.GroupId" /> into <see cref="RemoteRequest.ParentId" />, or to the top level when that is omitted; answered in <see cref="RemoteResponse.Group" />.
    /// </summary>
    MoveGroup,

    /// <summary>
    /// Renames <see cref="RemoteRequest.GroupId" /> to <see cref="RemoteRequest.Name" />; answered in <see cref="RemoteResponse.Group" />.
    /// </summary>
    RenameGroup,

    /// <summary>
    /// Deletes <see cref="RemoteRequest.GroupId" />; the workspaces and groups in it are kept and move into its parent.
    /// </summary>
    DeleteGroup,

    /// <summary>
    /// Moves <see cref="RemoteRequest.WorkspaceId" /> up or down among the workspaces in its group by <see cref="RemoteRequest.Offset" /> places.
    /// </summary>
    ShiftWorkspace,

    /// <summary>
    /// Moves <see cref="RemoteRequest.GroupId" /> up or down among the groups with the same parent by <see cref="RemoteRequest.Offset" /> places.
    /// </summary>
    ShiftGroup,

    /// <summary>
    /// Pauses the AI's work in <see cref="RemoteRequest.WorkspaceId" />: its process is suspended and the workspace is paused until <see cref="ResumeWorkspace" />, <see cref="StopWorkspace" /> or <see cref="CancelWorkspace" />; refused unless the AI is working.
    /// </summary>
    PauseWorkspace,

    /// <summary>
    /// Lets the paused AI work in <see cref="RemoteRequest.WorkspaceId" /> carry on; refused unless the workspace is paused.
    /// </summary>
    ResumeWorkspace,

    /// <summary>
    /// Stops the work running in <see cref="RemoteRequest.WorkspaceId" /> at once, keeping what it did, and returns the workspace to ready; refused when nothing runs or the workspace is already stopping or canceling; it works for the AI (also paused), a script and a git action, which it undoes.
    /// </summary>
    StopWorkspace,

    /// <summary>
    /// Stops the work running in <see cref="RemoteRequest.WorkspaceId" /> and reverts what it did, returning the workspace to ready once reverted; refused unless the AI is working or paused, and when its work cannot be reverted because the workspace is not a git repository. Scripts and git actions are stopped, not cancelled.
    /// </summary>
    CancelWorkspace,

    /// <summary>
    /// Reads the <see cref="WorkspaceState" /> of <see cref="RemoteRequest.WorkspaceId" />; answered in <see cref="RemoteResponse.State" />.
    /// </summary>
    GetWorkspaceState,

    /// <summary>
    /// Deletes <see cref="RemoteRequest.WorkspaceId" />, and its folder too when <see cref="RemoteRequest.DeleteFolder" /> is set.
    /// </summary>
    DeleteWorkspace,

    /// <summary>
    /// Lists the sub-folders of <see cref="RemoteRequest.Path" /> (the server's default folder when omitted); answered in <see cref="RemoteResponse.Listing" />.
    /// </summary>
    ListFolders,

    /// <summary>
    /// Creates the folder <see cref="RemoteRequest.Name" /> inside <see cref="RemoteRequest.Path" />; answers with the new folder's listing in <see cref="RemoteResponse.Listing" />.
    /// </summary>
    CreateFolder,

    /// <summary>
    /// Reads a chunk of the conversation of <see cref="RemoteRequest.WorkspaceId" />, the newest messages or the ones before <see cref="RemoteRequest.Before" />, at most <see cref="RemoteRequest.Limit" />, and whether the AI is working; answered in <see cref="RemoteResponse.History" />.
    /// </summary>
    GetHistory,

    /// <summary>
    /// Reads the usage limits of <see cref="RemoteRequest.Service" />, as the server last read them (at most a minute old); answered in <see cref="RemoteResponse.Usage" />, with no windows when the service reports none.
    /// </summary>
    GetUsage,

    /// <summary>
    /// Sends <see cref="RemoteRequest.Text" /> to the AI in <see cref="RemoteRequest.WorkspaceId" /> and answers at once; if the AI is already working the text joins that work. The conversation and the work arrive as <see cref="RemoteEvent" /> pushes.
    /// </summary>
    Prompt,

    /// <summary>
    /// Lists the entries of <see cref="RemoteRequest.Path" /> (relative to the workspace folder, empty for the folder itself), without those a <c>.filehide</c> file hides unless <see cref="RemoteRequest.ShowHidden" /> is set; answered in <see cref="RemoteResponse.Files" />.
    /// </summary>
    ListFiles,

    /// <summary>
    /// Searches the files of <see cref="RemoteRequest.WorkspaceId" /> for <see cref="RemoteRequest.Text" />: by file name with a fuzzy match, or for lines containing it when <see cref="RemoteRequest.SearchContent" /> is set. Files a <c>.filehide</c> file hides are skipped unless <see cref="RemoteRequest.ShowHidden" /> is set; answered in <see cref="RemoteResponse.SearchResults" />.
    /// </summary>
    SearchFiles,

    /// <summary>
    /// Reads the text file <see cref="RemoteRequest.Path" /> (relative to the workspace folder); answered in <see cref="RemoteResponse.Content" />.
    /// </summary>
    ReadFile,

    /// <summary>
    /// Watches the folders <see cref="RemoteRequest.Paths" /> (relative to the workspace folder, empty for the folder itself) of <see cref="RemoteRequest.WorkspaceId" /> in place of any folders this connection watched before, none stopping the watch; changes arrive as <see cref="RemoteEventKind.FilesChanged" /> pushes.
    /// </summary>
    WatchFiles,

    /// <summary>
    /// Clones the repository at <see cref="RemoteRequest.Url" /> into a new folder inside the existing folder <see cref="RemoteRequest.Path" /> and creates a workspace in it; the folder is named <see cref="RemoteRequest.Name" />, or after the repository when that is omitted. Answered in <see cref="RemoteResponse.Workspace" />.
    /// </summary>
    CloneWorkspace,

    /// <summary>
    /// Reads the git state of <see cref="RemoteRequest.WorkspaceId" />; answered in <see cref="RemoteResponse.Git" />, which says the workspace is not a repository when it is not.
    /// </summary>
    GetGitStatus,

    /// <summary>
    /// Lists the paths of <see cref="RemoteRequest.WorkspaceId" /> that differ from the head commit, and the ones git ignores; answered in <see cref="RemoteResponse.GitChanges" />.
    /// </summary>
    GetGitChanges,

    /// <summary>
    /// Reads a chunk of the history of <see cref="RemoteRequest.WorkspaceId" />, newest first, skipping <see cref="RemoteRequest.Skip" /> commits and reading at most <see cref="RemoteRequest.Limit" />; answered in <see cref="RemoteResponse.GitHistory" />.
    /// </summary>
    GetGitHistory,

    /// <summary>
    /// Lists the paths the commit <see cref="RemoteRequest.Commit" /> changed; answered in <see cref="RemoteResponse.GitChanges" />.
    /// </summary>
    GetGitCommitChanges,

    /// <summary>
    /// Reads the diff of the file <see cref="RemoteRequest.Path" />, from the commit <see cref="RemoteRequest.Commit" /> against its parent or, when that is omitted, from the working tree against the head; answered in <see cref="RemoteResponse.GitDiff" />.
    /// </summary>
    GetGitDiff,

    /// <summary>
    /// Stages every change of <see cref="RemoteRequest.WorkspaceId" /> and commits it with the message <see cref="RemoteRequest.Text" />, then pushes the branch when there is a remote.
    /// </summary>
    GitCommit,

    /// <summary>
    /// Discards every change of <see cref="RemoteRequest.WorkspaceId" /> that is not committed, new files included.
    /// </summary>
    GitReset,

    /// <summary>
    /// Creates the branch <see cref="RemoteRequest.Name" /> at the head of <see cref="RemoteRequest.WorkspaceId" /> and checks it out.
    /// </summary>
    GitCreateBranch,

    /// <summary>
    /// Creates the tag <see cref="RemoteRequest.Name" /> at the head of <see cref="RemoteRequest.WorkspaceId" />.
    /// </summary>
    GitCreateTag,

    /// <summary>
    /// Sets the <c>origin</c> remote of <see cref="RemoteRequest.WorkspaceId" /> to <see cref="RemoteRequest.Url" />, creating the repository first when the workspace has none.
    /// </summary>
    GitSetRemote,

    /// <summary>
    /// Checks out the branch, tag or commit <see cref="RemoteRequest.Name" />; with <see cref="RemoteRequest.Force" /> any changes that are not committed are discarded first, otherwise they make it fail.
    /// </summary>
    GitCheckout,

    /// <summary>
    /// Deletes the branch <see cref="RemoteRequest.Name" /> locally and on the remote.
    /// </summary>
    GitDeleteBranch,

    /// <summary>
    /// Deletes the tag <see cref="RemoteRequest.Name" /> locally and on the remote.
    /// </summary>
    GitDeleteTag,

    /// <summary>
    /// Compares the branch <see cref="RemoteRequest.Name" /> with <see cref="RemoteRequest.Base" />; answered in <see cref="RemoteResponse.GitBranch" />.
    /// </summary>
    GetGitBranchState,

    /// <summary>
    /// Collapses the commits of the branch <see cref="RemoteRequest.Name" /> that <see cref="RemoteRequest.Base" /> lacks into one with the message <see cref="RemoteRequest.Text" />, then force-pushes the branch.
    /// </summary>
    GitSquash,

    /// <summary>
    /// Rebases the branch <see cref="RemoteRequest.Name" /> onto <see cref="RemoteRequest.Base" />, squashing its own commits first when <see cref="RemoteRequest.Text" /> is given, and has the AI resolve conflicts; then force-pushes the branch.
    /// </summary>
    GitRebase,

    /// <summary>
    /// Brings the branch <see cref="RemoteRequest.Name" /> into <see cref="RemoteRequest.Base" /> as a fast-forward, rebasing it first when needed (squashing with <see cref="RemoteRequest.Text" /> when given), pushes the base and deletes the merged branch everywhere.
    /// </summary>
    GitMerge,

    /// <summary>
    /// Fetches the remote with pruning and moves local branches that have nothing of their own to match it.
    /// </summary>
    GitFetch,

    /// <summary>
    /// Fetches like <see cref="GitFetch" />, then fast-forwards the checked out branch onto its remote counterpart, stashing and restoring any changes that are not committed; the AI resolves conflicts. Does nothing while the AI is working or another git action runs.
    /// </summary>
    GitSync,

    /// <summary>
    /// Sets the git user name <see cref="RemoteRequest.Name" /> and email <see cref="RemoteRequest.Email" /> in the server user's global git configuration.
    /// </summary>
    GitSetIdentity,

    /// <summary>
    /// Cancels the git action running in <see cref="RemoteRequest.WorkspaceId" />, which undoes what it had done so far.
    /// </summary>
    GitCancel,

    /// <summary>
    /// Lists the script and task runs the server holds for <see cref="RemoteRequest.WorkspaceId" />; answered in <see cref="RemoteResponse.Scripts" />.
    /// </summary>
    GetScripts,

    /// <summary>
    /// Runs the <c>.cs</c> script or <c>.task</c> file <see cref="RemoteRequest.Path" /> (relative to the workspace folder) with <c>dotnet run --file</c> in the workspace folder and answers once it started. Fails while the AI, a git action or another script is running. Progress arrives as <see cref="RemoteEvent" /> pushes.
    /// </summary>
    RunScript,

    /// <summary>
    /// Stops the run of <see cref="RemoteRequest.Path" /> in <see cref="RemoteRequest.WorkspaceId" />, the whole task for a task.
    /// </summary>
    StopScript,

    /// <summary>
    /// Writes the line <see cref="RemoteRequest.Text" /> to the standard input of the running script <see cref="RemoteRequest.Path" />.
    /// </summary>
    SendScriptInput,

    /// <summary>
    /// Forgets the finished run of <see cref="RemoteRequest.Path" />, and the runs of the scripts of a task.
    /// </summary>
    RemoveScript,

    /// <summary>
    /// Stores the file <see cref="RemoteRequest.Name" /> with the base64 content <see cref="RemoteRequest.Data" /> on the server for <see cref="RemoteRequest.WorkspaceId" /> and answers with its path in <see cref="RemoteResponse.AttachmentPath" />, for a prompt to refer to. Fails above the size limit.
    /// </summary>
    UploadAttachment,

    /// <summary>
    /// Turns the speech in the base64 WAV audio <see cref="RemoteRequest.Data" /> (16 kHz, mono, 16 bit) into text with the server's speech command and answers with it in <see cref="RemoteResponse.Transcript" />. Fails when the server has no speech command, the audio is larger than the limit or the command fails.
    /// </summary>
    Transcribe
}
