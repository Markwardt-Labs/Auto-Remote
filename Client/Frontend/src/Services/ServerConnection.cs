namespace Markwardt.AutoRemote.Client;

/// <summary>
/// An authenticated connection to a server. Requests may overlap; each call completes when its own response arrives.
/// </summary>
internal interface IServerConnection : IAsyncDisposable
{
    /// <summary>
    /// Raised when the server pushes an event; on the thread the connection was opened on when it has a synchronization context.
    /// </summary>
    event Action<RemoteEvent>? EventReceived;

    /// <summary>
    /// Raised when the connection to the server is lost. It keeps trying to reopen by itself, so <see cref="Reconnected" /> follows once it succeeds.
    /// </summary>
    event Action? Lost;

    /// <summary>
    /// Raised once the connection was reopened after it was lost, which means events were missed meanwhile and whatever follows them must be read again; on the thread the connection was opened on when it has a synchronization context.
    /// </summary>
    event Action? Reconnected;

    /// <summary>
    /// Drops the connection and opens a new one, for a connection that may have died silently, such as when the app was in the background; <see cref="Reconnected" /> is raised once it is open.
    /// </summary>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="IOException">The server cannot be reached.</exception>
    Task Reconnect(CancellationToken cancellation);

    /// <summary>
    /// Lists the server's workspaces and the groups they are organized into.
    /// </summary>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The groups and workspaces, each in its custom order.</returns>
    Task<WorkspaceCatalog> ListWorkspaces(CancellationToken cancellation);

    /// <summary>
    /// Creates a workspace in an existing folder on the server.
    /// </summary>
    /// <param name="folder">The folder's absolute path on the server.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The new workspace.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceInfo> CreateWorkspace(string folder, CancellationToken cancellation);

    /// <summary>
    /// Renames a workspace by renaming its folder.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The new folder name.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The renamed workspace.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceInfo> RenameWorkspace(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Changes the AI service, model and effort a workspace's prompts run with.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="service">The AI service.</param>
    /// <param name="model">The model.</param>
    /// <param name="effort">The effort.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The updated workspace.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceInfo> ConfigureWorkspace(Guid id, AiService service, AiModel model, AiEffort effort, CancellationToken cancellation);

    /// <summary>
    /// Moves a workspace into a group, or out of any.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="groupId">The group, or <see langword="null" /> for none.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The updated workspace.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceInfo> MoveWorkspace(Guid id, Guid? groupId, CancellationToken cancellation);

    /// <summary>
    /// Creates a group.
    /// </summary>
    /// <param name="name">The group's name.</param>
    /// <param name="parentId">The group to create it in, or <see langword="null" /> for the top level.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The new group.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceGroup> CreateGroup(string name, Guid? parentId, CancellationToken cancellation);

    /// <summary>
    /// Renames a group.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The renamed group.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceGroup> RenameGroup(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Moves a group, with everything in it, into another group or to the top level.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="parentId">The new parent group, or <see langword="null" /> for the top level.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The moved group.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceGroup> MoveGroup(Guid id, Guid? parentId, CancellationToken cancellation);

    /// <summary>
    /// Deletes a group; the workspaces and groups in it are kept and move into its parent.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task DeleteGroup(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Moves a workspace up or down among the workspaces in its group.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="offset">How many places to move: negative is up, positive is down.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task ShiftWorkspace(Guid id, int offset, CancellationToken cancellation);

    /// <summary>
    /// Moves a group up or down among the groups with the same parent.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="offset">How many places to move: negative is up, positive is down.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task ShiftGroup(Guid id, int offset, CancellationToken cancellation);

    /// <summary>
    /// Deletes a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="deleteFolder">Whether to delete the workspace's folder too.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task DeleteWorkspace(Guid id, bool deleteFolder, CancellationToken cancellation);

    /// <summary>
    /// Lists the sub-folders of a folder on the server.
    /// </summary>
    /// <param name="path">The folder; <see langword="null" /> for the server's default folder.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The listing.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<FolderListing> ListFolders(string? path, CancellationToken cancellation);

    /// <summary>
    /// Creates a folder on the server.
    /// </summary>
    /// <param name="parent">The folder to create it in.</param>
    /// <param name="name">The new folder's name.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The listing of the new folder.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<FolderListing> CreateFolder(string parent, string name, CancellationToken cancellation);

    /// <summary>
    /// Reads a chunk of a workspace's conversation.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="before">The position in the conversation before which to read, or <see langword="null" /> for the newest messages.</param>
    /// <param name="limit">The most messages to read, or <see langword="null" /> for the server's default.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The chunk in order and whether the AI is working.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<ChatHistory> GetHistory(Guid id, int? before, int? limit, CancellationToken cancellation);

    /// <summary>
    /// Sends a prompt to the AI in a workspace. It is accepted at once, also while the AI works, and the conversation arrives as events.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task Prompt(Guid id, string text, CancellationToken cancellation);

    /// <summary>
    /// Stores a file on the server so that a prompt can refer to it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The file name.</param>
    /// <param name="data">The file content.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The path of the file on the server.</returns>
    /// <exception cref="RemoteException">The server refused, for example because the file is too large.</exception>
    Task<string> UploadAttachment(Guid id, string name, ReadOnlyMemory<byte> data, CancellationToken cancellation);

    /// <summary>
    /// Asks to be told when any of some folders inside a workspace's folder changes, in place of the folders watched before.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="paths">The folders relative to the workspace folder; empty for the workspace folder itself. None stops watching.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task WatchFiles(Guid id, IReadOnlyList<string> paths, CancellationToken cancellation);

    /// <summary>
    /// Lists a folder inside a workspace's folder.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The folder relative to the workspace folder; empty for the workspace folder.</param>
    /// <param name="showHidden">Whether to include the entries that a <c>.filehide</c> file hides.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The folder's entries.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceFiles> ListFiles(Guid id, string path, bool showHidden, CancellationToken cancellation);

    /// <summary>
    /// Searches a workspace's files by name or by content.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The text to find.</param>
    /// <param name="content">Whether to search the contents of the files instead of their names.</param>
    /// <param name="showHidden">Whether to include the files that a <c>.filehide</c> file hides.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The hits.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<IReadOnlyList<FileSearchResult>> SearchFiles(Guid id, string text, bool content, bool showHidden, CancellationToken cancellation);

    /// <summary>
    /// Reads a text file inside a workspace's folder.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The file relative to the workspace folder.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The file text.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<FileContent> ReadFile(Guid id, string path, CancellationToken cancellation);

    /// <summary>
    /// Asks the server to clone a repository into a new folder and create a workspace in it.
    /// </summary>
    /// <param name="url">The repository URL.</param>
    /// <param name="parent">The existing folder on the server to create the clone's folder in.</param>
    /// <param name="name">The new folder's name, or <see langword="null" /> to name it after the repository.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The new workspace.</returns>
    /// <exception cref="RemoteException">The server refused or the clone failed.</exception>
    Task<WorkspaceInfo> CloneWorkspace(string url, string parent, string? name, CancellationToken cancellation);

    /// <summary>
    /// Reads the git state of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The state; not a repository when the workspace has none.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<GitStatus> GetGitStatus(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Lists the paths of a workspace that differ from the head commit, and the ones git ignores.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The paths.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<IReadOnlyList<GitChange>> GetGitChanges(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Reads a chunk of a workspace's git history, newest commit first.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="skip">How many of the newest commits to skip.</param>
    /// <param name="limit">The most commits to read, or <see langword="null" /> for the server's default.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The chunk.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<GitHistory> GetGitHistory(Guid id, int skip, int? limit, CancellationToken cancellation);

    /// <summary>
    /// Lists the paths a commit changed.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="commit">The commit hash.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The paths.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<IReadOnlyList<GitChange>> GetGitCommitChanges(Guid id, string commit, CancellationToken cancellation);

    /// <summary>
    /// Reads the diff of one file.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The file relative to the workspace folder.</param>
    /// <param name="commit">The commit to diff against its parent, or <see langword="null" /> to diff the working tree against the head.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The diff.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<GitDiff> GetGitDiff(Guid id, string path, string? commit, CancellationToken cancellation);

    /// <summary>
    /// Commits every change of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitCommit(Guid id, string message, CancellationToken cancellation);

    /// <summary>
    /// Discards every change of a workspace that is not committed.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitReset(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Creates a branch at the head of a workspace and checks it out.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The branch name.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitCreateBranch(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Creates a tag at the head of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The tag name.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitCreateTag(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Sets the remote of a workspace, making it a repository first when it is not one.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="url">The repository URL.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitSetRemote(Guid id, string url, CancellationToken cancellation);

    /// <summary>
    /// Checks out a branch, tag or commit of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The branch, tag or commit.</param>
    /// <param name="force">Whether to discard changes that are not committed first.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitCheckout(Guid id, string name, bool force, CancellationToken cancellation);

    /// <summary>
    /// Deletes a branch locally and on the remote.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The branch.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitDeleteBranch(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Deletes a tag locally and on the remote.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The tag.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitDeleteTag(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Compares a branch with another.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="baseBranch">The branch it is compared with.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>How they stand.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<GitBranchState> GetGitBranchState(Guid id, string branch, string baseBranch, CancellationToken cancellation);

    /// <summary>
    /// Collapses the commits of a branch that another lacks into one, then force-pushes it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="branch">The branch to squash.</param>
    /// <param name="baseBranch">The branch it is squashed against.</param>
    /// <param name="message">The message of the one commit.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitSquash(Guid id, string branch, string baseBranch, string message, CancellationToken cancellation);

    /// <summary>
    /// Rebases a branch onto another, with the AI resolving conflicts, then force-pushes it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="branch">The branch to rebase.</param>
    /// <param name="onto">The branch to rebase it onto.</param>
    /// <param name="squashMessage">The message to squash the branch's own commits into first, or <see langword="null" /> not to.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused or the conflicts could not be resolved.</exception>
    Task GitRebase(Guid id, string branch, string onto, string? squashMessage, CancellationToken cancellation);

    /// <summary>
    /// Brings a branch into another as a fast-forward and deletes the merged branch everywhere.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="source">The branch to merge.</param>
    /// <param name="target">The branch it is merged into.</param>
    /// <param name="squashMessage">The message to squash the source into first, or <see langword="null" /> not to.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused or the conflicts could not be resolved.</exception>
    Task GitMerge(Guid id, string source, string target, string? squashMessage, CancellationToken cancellation);

    /// <summary>
    /// Fetches the remote of a workspace with pruning.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitFetch(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Fetches and pulls the checked out branch of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitSync(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Sets the git user name and email on the server.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The user name.</param>
    /// <param name="email">The email.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitSetIdentity(Guid id, string name, string email, CancellationToken cancellation);

    /// <summary>
    /// Cancels the git action running in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task GitCancel(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Stops the work running in a workspace at once, keeping what it did.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused: nothing runs, or the workspace is already stopping or canceling.</exception>
    Task StopWorkspace(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Stops the work running in a workspace and reverts what it did.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused: nothing runs, the workspace is already stopping or canceling, or its work cannot be reverted.</exception>
    Task CancelWorkspace(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Reads the state a workspace is in.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The state.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<WorkspaceState> GetWorkspaceState(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Reads the usage limits of an AI service, as the server last read them (at most a minute old).
    /// </summary>
    /// <param name="service">The AI service.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The limits; with no windows when the service reports none.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<UsageStatus> GetUsage(AiService service, CancellationToken cancellation);

    /// <summary>
    /// Lists the script and task runs the server holds for a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The runs with their output.</returns>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task<ScriptRuns> GetScripts(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Runs a <c>.cs</c> script or a <c>.task</c> file of a workspace; it is accepted once it started, and its progress arrives as events.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The file, relative to the workspace folder.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused, for example while the AI or another script is running.</exception>
    Task RunScript(Guid id, string path, CancellationToken cancellation);

    /// <summary>
    /// Stops the run of a script, or of all the scripts of a task.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The file of the run.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task StopScript(Guid id, string path, CancellationToken cancellation);

    /// <summary>
    /// Writes a line to the standard input of a running script.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The script.</param>
    /// <param name="text">The line.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task SendScriptInput(Guid id, string path, string text, CancellationToken cancellation);

    /// <summary>
    /// Forgets a finished run on the server.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The file of the run.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <exception cref="RemoteException">The server refused.</exception>
    Task RemoveScript(Guid id, string path, CancellationToken cancellation);

    /// <summary>
    /// Opens <see cref="IServerConnection" /> instances.
    /// </summary>
    public interface IFactory
    {
        /// <summary>
        /// Connects to a server and authenticates with its access key.
        /// </summary>
        /// <param name="settings">The server to reach.</param>
        /// <param name="cancellation">Cancels the attempt.</param>
        /// <returns>The open connection.</returns>
        /// <exception cref="UnauthorizedAccessException">The server rejected the access key.</exception>
        /// <exception cref="IOException">The server could not be reached in time, refused the connection or closed it.</exception>
        Task<IServerConnection> Connect(ConnectionSettings settings, CancellationToken cancellation);
    }

    /// <inheritdoc />
    public sealed class Factory(IMessageChannel.IFactory channels, ILog log) : IFactory
    {
        private readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

        /// <inheritdoc />
        public async Task<IServerConnection> Connect(ConnectionSettings settings, CancellationToken cancellation)
        {
            (ClientWebSocket socket, IMessageChannel channel) = await Open(settings, cancellation);
            return new ServerConnection(socket, channel, token => Open(settings, token), log, $"{settings.Host}:{settings.Port}");
        }

        private async Task<(ClientWebSocket socket, IMessageChannel channel)> Open(ConnectionSettings settings, CancellationToken cancellation)
        {
            ClientWebSocket socket = new();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            string server = $"{settings.Host}:{settings.Port}";
            string stage = "connecting";

            try
            {
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                limit.CancelAfter(timeout);
                string fingerprint = string.Empty;
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                    return true;
                };

                Uri address = new UriBuilder("wss", settings.Host, settings.Port).Uri;
                log.Write($"Connecting to {address}");

                try
                {
                    await socket.ConnectAsync(address, limit.Token);
                }
                catch (WebSocketException exception)
                {
                    throw new IOException($"Could not connect to {server}: {exception.InnerException?.Message ?? exception.Message}", exception);
                }

                log.Write($"WebSocket and TLS established with {server} (certificate SHA-256 {fingerprint})");
                stage = "the key handshake";
                IMessageChannel channel = channels.Create(new WebSocketLineStream(socket));
                await channel.Send(new HandshakeRequest { AccessKey = settings.AccessKey }, limit.Token);
                HandshakeResponse? reply = await channel.Receive<HandshakeResponse>(limit.Token);

                if (reply is null)
                {
                    throw new IOException("The server closed the connection.");
                }

                if (!reply.Accepted)
                {
                    throw new UnauthorizedAccessException("The server rejected the access key.");
                }

                log.Write($"Connected to {server}");
                return (socket, channel);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                log.Write($"Connection to {server} failed during {stage}: timed out after {timeout.TotalSeconds:0} seconds");
                socket.Dispose();
                throw new IOException("Timed out waiting for the server.");
            }
            catch (Exception exception)
            {
                log.Write($"Connection to {server} failed during {stage}: {exception.GetType().Name}: {exception.Message}");
                socket.Dispose();
                throw;
            }
        }
    }
}

/// <inheritdoc />
internal sealed class ServerConnection : IServerConnection
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServerConnection" /> class and starts reading responses.
    /// </summary>
    /// <param name="socket">The connected WebSocket.</param>
    /// <param name="channel">The authenticated message channel over the WebSocket.</param>
    /// <param name="reopen">Opens a new authenticated connection to the same server, used when the connection is lost.</param>
    /// <param name="log">Receives a line when the connection is lost.</param>
    /// <param name="server">The host and port, for the log.</param>
    public ServerConnection(ClientWebSocket socket, IMessageChannel channel, Func<CancellationToken, Task<(ClientWebSocket socket, IMessageChannel channel)>> reopen, ILog log, string server)
    {
        this.log = log;
        this.server = server;
        this.socket = socket;
        this.channel = channel;
        this.reopen = reopen;
        reading = Read(channel);
    }

    private readonly ILog log;

    private readonly string server;

    private readonly Func<CancellationToken, Task<(ClientWebSocket socket, IMessageChannel channel)>> reopen;

    private readonly SynchronizationContext? context = SynchronizationContext.Current;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<RemoteResponse>> pending = new();

    private readonly SemaphoreSlim writing = new(1, 1);

    private readonly CancellationTokenSource stopping = new();

    private readonly SemaphoreSlim reopening = new(1, 1);

    private ClientWebSocket socket;

    private IMessageChannel channel;

    private Task reading;

    private RemoteRequest? watching;

    private int nextId;

    private volatile bool lost;

    /// <inheritdoc />
    public event Action<RemoteEvent>? EventReceived;

    /// <inheritdoc />
    public event Action? Lost;

    /// <inheritdoc />
    public event Action? Reconnected;

    /// <inheritdoc />
    public async Task Reconnect(CancellationToken cancellation)
    {
        if (stopping.IsCancellationRequested)
        {
            return;
        }

        socket.Abort();
        await reading;
        await Reopen(cancellation);
    }

    /// <inheritdoc />
    public async Task<WorkspaceCatalog> ListWorkspaces(CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.ListWorkspaces }, cancellation)).Catalog ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceInfo> CreateWorkspace(string folder, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.CreateWorkspace, Path = folder }, cancellation)).Workspace ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceInfo> RenameWorkspace(Guid id, string name, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.RenameWorkspace, WorkspaceId = id, Name = name }, cancellation)).Workspace ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceInfo> ConfigureWorkspace(Guid id, AiService service, AiModel model, AiEffort effort, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.ConfigureWorkspace, WorkspaceId = id, Service = service, Model = model, Effort = effort }, cancellation)).Workspace ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceInfo> MoveWorkspace(Guid id, Guid? groupId, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.MoveWorkspace, WorkspaceId = id, GroupId = groupId }, cancellation)).Workspace ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceGroup> CreateGroup(string name, Guid? parentId, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.CreateGroup, Name = name, ParentId = parentId }, cancellation)).Group ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceGroup> MoveGroup(Guid id, Guid? parentId, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.MoveGroup, GroupId = id, ParentId = parentId }, cancellation)).Group ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceGroup> RenameGroup(Guid id, string name, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.RenameGroup, GroupId = id, Name = name }, cancellation)).Group ?? throw Malformed();

    /// <inheritdoc />
    public async Task DeleteGroup(Guid id, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.DeleteGroup, GroupId = id }, cancellation);

    /// <inheritdoc />
    public async Task ShiftWorkspace(Guid id, int offset, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.ShiftWorkspace, WorkspaceId = id, Offset = offset }, cancellation);

    /// <inheritdoc />
    public async Task ShiftGroup(Guid id, int offset, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.ShiftGroup, GroupId = id, Offset = offset }, cancellation);

    /// <inheritdoc />
    public async Task DeleteWorkspace(Guid id, bool deleteFolder, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.DeleteWorkspace, WorkspaceId = id, DeleteFolder = deleteFolder }, cancellation);

    /// <inheritdoc />
    public async Task<FolderListing> ListFolders(string? path, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.ListFolders, Path = path }, cancellation)).Listing ?? throw Malformed();

    /// <inheritdoc />
    public async Task<FolderListing> CreateFolder(string parent, string name, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.CreateFolder, Path = parent, Name = name }, cancellation)).Listing ?? throw Malformed();

    /// <inheritdoc />
    public async Task<ChatHistory> GetHistory(Guid id, int? before, int? limit, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetHistory, WorkspaceId = id, Before = before, Limit = limit }, cancellation)).History ?? throw Malformed();

    /// <inheritdoc />
    public async Task Prompt(Guid id, string text, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.Prompt, WorkspaceId = id, Text = text }, cancellation);

    /// <inheritdoc />
    public async Task<string> UploadAttachment(Guid id, string name, ReadOnlyMemory<byte> data, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.UploadAttachment, WorkspaceId = id, Name = name, Data = Convert.ToBase64String(data.Span) }, cancellation)).AttachmentPath ?? throw Malformed();

    /// <inheritdoc />
    public async Task WatchFiles(Guid id, IReadOnlyList<string> paths, CancellationToken cancellation)
    {
        RemoteRequest request = new() { Id = 0, Command = RemoteCommand.WatchFiles, WorkspaceId = id, Paths = paths };
        await Send(request, cancellation);
        watching = request;
    }

    /// <inheritdoc />
    public async Task<WorkspaceFiles> ListFiles(Guid id, string path, bool showHidden, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.ListFiles, WorkspaceId = id, Path = path, ShowHidden = showHidden ? true : null }, cancellation)).Files ?? throw Malformed();

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileSearchResult>> SearchFiles(Guid id, string text, bool content, bool showHidden, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.SearchFiles, WorkspaceId = id, Text = text, SearchContent = content ? true : null, ShowHidden = showHidden ? true : null }, cancellation)).SearchResults ?? throw Malformed();

    /// <inheritdoc />
    public async Task<FileContent> ReadFile(Guid id, string path, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.ReadFile, WorkspaceId = id, Path = path }, cancellation)).Content ?? throw Malformed();

    /// <inheritdoc />
    public async Task<WorkspaceInfo> CloneWorkspace(string url, string parent, string? name, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.CloneWorkspace, Url = url, Path = parent, Name = name }, cancellation)).Workspace ?? throw Malformed();

    /// <inheritdoc />
    public async Task<GitStatus> GetGitStatus(Guid id, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetGitStatus, WorkspaceId = id }, cancellation)).Git ?? throw Malformed();

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitChange>> GetGitChanges(Guid id, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetGitChanges, WorkspaceId = id }, cancellation)).GitChanges?.Changes ?? throw Malformed();

    /// <inheritdoc />
    public async Task<GitHistory> GetGitHistory(Guid id, int skip, int? limit, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetGitHistory, WorkspaceId = id, Skip = skip, Limit = limit }, cancellation)).GitHistory ?? throw Malformed();

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitChange>> GetGitCommitChanges(Guid id, string commit, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetGitCommitChanges, WorkspaceId = id, Commit = commit }, cancellation)).GitChanges?.Changes ?? throw Malformed();

    /// <inheritdoc />
    public async Task<GitDiff> GetGitDiff(Guid id, string path, string? commit, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetGitDiff, WorkspaceId = id, Path = path, Commit = commit }, cancellation)).GitDiff ?? throw Malformed();

    /// <inheritdoc />
    public async Task GitCommit(Guid id, string message, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitCommit, WorkspaceId = id, Text = message }, cancellation);

    /// <inheritdoc />
    public async Task GitReset(Guid id, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitReset, WorkspaceId = id }, cancellation);

    /// <inheritdoc />
    public async Task GitCreateBranch(Guid id, string name, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitCreateBranch, WorkspaceId = id, Name = name }, cancellation);

    /// <inheritdoc />
    public async Task GitCreateTag(Guid id, string name, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitCreateTag, WorkspaceId = id, Name = name }, cancellation);

    /// <inheritdoc />
    public async Task GitSetRemote(Guid id, string url, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitSetRemote, WorkspaceId = id, Url = url }, cancellation);

    /// <inheritdoc />
    public async Task GitCheckout(Guid id, string name, bool force, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitCheckout, WorkspaceId = id, Name = name, Force = force ? true : null }, cancellation);

    /// <inheritdoc />
    public async Task GitDeleteBranch(Guid id, string name, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitDeleteBranch, WorkspaceId = id, Name = name }, cancellation);

    /// <inheritdoc />
    public async Task GitDeleteTag(Guid id, string name, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitDeleteTag, WorkspaceId = id, Name = name }, cancellation);

    /// <inheritdoc />
    public async Task<GitBranchState> GetGitBranchState(Guid id, string branch, string baseBranch, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetGitBranchState, WorkspaceId = id, Name = branch, Base = baseBranch }, cancellation)).GitBranch ?? throw Malformed();

    /// <inheritdoc />
    public async Task GitSquash(Guid id, string branch, string baseBranch, string message, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitSquash, WorkspaceId = id, Name = branch, Base = baseBranch, Text = message }, cancellation);

    /// <inheritdoc />
    public async Task GitRebase(Guid id, string branch, string onto, string? squashMessage, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitRebase, WorkspaceId = id, Name = branch, Base = onto, Text = squashMessage }, cancellation);

    /// <inheritdoc />
    public async Task GitMerge(Guid id, string source, string target, string? squashMessage, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitMerge, WorkspaceId = id, Name = source, Base = target, Text = squashMessage }, cancellation);

    /// <inheritdoc />
    public async Task GitFetch(Guid id, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitFetch, WorkspaceId = id }, cancellation);

    /// <inheritdoc />
    public async Task GitSync(Guid id, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitSync, WorkspaceId = id }, cancellation);

    /// <inheritdoc />
    public async Task GitSetIdentity(Guid id, string name, string email, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitSetIdentity, WorkspaceId = id, Name = name, Email = email }, cancellation);

    /// <inheritdoc />
    public async Task GitCancel(Guid id, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.GitCancel, WorkspaceId = id }, cancellation);

    /// <inheritdoc />
    public async Task StopWorkspace(Guid id, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.StopWorkspace, WorkspaceId = id }, cancellation);

    /// <inheritdoc />
    public async Task CancelWorkspace(Guid id, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.CancelWorkspace, WorkspaceId = id }, cancellation);

    /// <inheritdoc />
    public async Task<WorkspaceState> GetWorkspaceState(Guid id, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetWorkspaceState, WorkspaceId = id }, cancellation)).State ?? throw Malformed();

    /// <inheritdoc />
    public async Task<UsageStatus> GetUsage(AiService service, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetUsage, Service = service }, cancellation)).Usage ?? throw Malformed();

    /// <inheritdoc />
    public async Task<ScriptRuns> GetScripts(Guid id, CancellationToken cancellation) => (await Send(new() { Id = 0, Command = RemoteCommand.GetScripts, WorkspaceId = id }, cancellation)).Scripts ?? throw Malformed();

    /// <inheritdoc />
    public async Task RunScript(Guid id, string path, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.RunScript, WorkspaceId = id, Path = path }, cancellation);

    /// <inheritdoc />
    public async Task StopScript(Guid id, string path, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.StopScript, WorkspaceId = id, Path = path }, cancellation);

    /// <inheritdoc />
    public async Task SendScriptInput(Guid id, string path, string text, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.SendScriptInput, WorkspaceId = id, Path = path, Text = text }, cancellation);

    /// <inheritdoc />
    public async Task RemoveScript(Guid id, string path, CancellationToken cancellation) => await Send(new() { Id = 0, Command = RemoteCommand.RemoveScript, WorkspaceId = id, Path = path }, cancellation);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        socket.Dispose();
        await reading;
    }

    private InvalidDataException Malformed() => new("The server sent an incomplete response.");

    private async Task<RemoteResponse> Send(RemoteRequest request, CancellationToken cancellation)
    {
        if (lost && !stopping.IsCancellationRequested)
        {
            await Reopen(cancellation);
        }

        return await Transmit(request, cancellation);
    }

    private async Task Reopen(CancellationToken cancellation)
    {
        await reopening.WaitAsync(cancellation);

        try
        {
            if (!lost || stopping.IsCancellationRequested)
            {
                return;
            }

            await reading;
            log.Write($"Reconnecting to {server}");
            (ClientWebSocket opened, IMessageChannel openedChannel) = await reopen(cancellation);
            socket.Dispose();
            socket = opened;
            channel = openedChannel;
            lost = false;
            reading = Read(openedChannel);
            Post(Reconnected);

            if (watching is { } request)
            {
                try
                {
                    await Transmit(request, cancellation);
                }
                catch (RemoteException)
                {
                }
            }
        }
        finally
        {
            reopening.Release();
        }
    }

    private async Task<RemoteResponse> Transmit(RemoteRequest request, CancellationToken cancellation)
    {
        TaskCompletionSource<RemoteResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteRequest numbered = request with { Id = Interlocked.Increment(ref nextId) };
        pending[numbered.Id] = completion;

        if (lost)
        {
            pending.TryRemove(numbered.Id, out _);
            throw new IOException("The connection to the server was lost.");
        }

        await using (cancellation.Register(() => completion.TrySetCanceled(cancellation)))
        {
            try
            {
                await writing.WaitAsync(cancellation);

                try
                {
                    await channel.Send(numbered, cancellation);
                }
                finally
                {
                    writing.Release();
                }

                RemoteResponse response = await completion.Task;
                return response.Error is { } error ? throw new RemoteException(error) : response;
            }
            finally
            {
                pending.TryRemove(numbered.Id, out _);
            }
        }
    }

    private void Post(Action? handler)
    {
        if (context is null)
        {
            handler?.Invoke();
        }
        else
        {
            context.Post(_ => handler?.Invoke(), null);
        }
    }

    private async Task Recover()
    {
        for (int attempt = 0; lost && !stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << Math.Min(attempt, 4), 15)), stopping.Token);
                await Reopen(stopping.Token);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private void Raise(RemoteEvent remoteEvent)
    {
        if (context is null)
        {
            EventReceived?.Invoke(remoteEvent);
        }
        else
        {
            context.Post(_ => EventReceived?.Invoke(remoteEvent), null);
        }
    }

    private async Task Read(IMessageChannel source)
    {
        try
        {
            while (await source.Receive<RemoteResponse>(stopping.Token) is { } response)
            {
                if (response.Event is { } remoteEvent)
                {
                    Raise(remoteEvent);
                }
                else if (pending.TryRemove(response.Id, out TaskCompletionSource<RemoteResponse>? completion))
                {
                    completion.TrySetResult(response);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }
        finally
        {
            lost = true;
            log.Write(stopping.IsCancellationRequested ? $"Disconnected from {server}" : $"Connection to {server} was lost");

            if (!stopping.IsCancellationRequested)
            {
                Post(Lost);
                _ = Recover();
            }

            foreach (TaskCompletionSource<RemoteResponse> completion in pending.Values)
            {
                completion.TrySetException(new IOException("The connection to the server was lost."));
            }
        }
    }
}
