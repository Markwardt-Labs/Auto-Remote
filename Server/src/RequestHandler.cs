namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Carries out the requests of an authenticated client.
/// </summary>
internal interface IRequestHandler
{
    /// <summary>
    /// Prepares the handler to serve requests.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="cancellation">Cancels the setup; once the handler is open, cancelling it also ends all AI work.</param>
    /// <exception cref="InvalidDataException">The stored workspaces are invalid.</exception>
    Task Open(ServerConfig config, CancellationToken cancellation);

    /// <summary>
    /// Carries out one request; failures are reported in the response instead of thrown.
    /// </summary>
    /// <param name="request">The client request.</param>
    /// <param name="owner">Identifies the connection the request came from, which owns the folder it watches.</param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>The response to send back.</returns>
    Task<RemoteResponse> Handle(RemoteRequest request, object owner, CancellationToken cancellation);

    /// <summary>
    /// Releases what a connection holds once it ends.
    /// </summary>
    /// <param name="owner">Identifies the connection.</param>
    void Disconnect(object owner);
}

/// <inheritdoc />
internal sealed class RequestHandler(IWorkspaceManager workspaces, IFolderBrowser folders, IWorkspaceFileBrowser files, IGitService git, IWorkspaceVersioning versioning, IPromptService prompts, IScriptRunner scripts, IUsageService usage, IEventHub events, IWorkspaceActivity activity, IWorkspaceControl control) : IRequestHandler
{
    private readonly ConcurrentDictionary<object, IReadOnlyList<IDisposable>> watches = new();

    /// <inheritdoc />
    public async Task Open(ServerConfig config, CancellationToken cancellation)
    {
        prompts.Configure(config, cancellation);
        scripts.Configure(config, cancellation);
        usage.Configure(config);
        folders.Configure(config.DefaultFolder);
        await workspaces.Open(Path.Combine(config.DataFolder, "workspaces.json"), cancellation);
    }

    /// <inheritdoc />
    public async Task<RemoteResponse> Handle(RemoteRequest request, object owner, CancellationToken cancellation)
    {
        try
        {
            return await Dispatch(request, owner, cancellation) with { Id = request.Id };
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
            return new RemoteResponse { Id = request.Id, Error = exception.Message };
        }
    }

    private async Task<RemoteResponse> Dispatch(RemoteRequest request, object owner, CancellationToken cancellation)
    {
        switch (request.Command)
        {
            case RemoteCommand.ListWorkspaces:
                return new RemoteResponse { Id = 0, Catalog = await workspaces.List(cancellation) };
            case RemoteCommand.CreateWorkspace:
                return new RemoteResponse { Id = 0, Workspace = await workspaces.Create(Require(request.Path, "path"), cancellation) };
            case RemoteCommand.RenameWorkspace:
                await RequireIdle(request);
                return new RemoteResponse { Id = 0, Workspace = await workspaces.Rename(RequireWorkspace(request), Require(request.Name, "name"), cancellation) };
            case RemoteCommand.ConfigureWorkspace:
                await prompts.Release(RequireWorkspace(request));
                return new RemoteResponse { Id = 0, Workspace = await workspaces.Configure(RequireWorkspace(request), request.Service ?? throw new RequestException("The request needs a service."), request.Model ?? throw new RequestException("The request needs a model."), request.Effort ?? throw new RequestException("The request needs an effort."), cancellation) };
            case RemoteCommand.GetUsage:
                return new RemoteResponse { Id = 0, Usage = await usage.Get(request.Service ?? throw new RequestException("The request needs a service."), cancellation) };
            case RemoteCommand.MoveWorkspace:
                return new RemoteResponse { Id = 0, Workspace = await workspaces.Move(RequireWorkspace(request), request.GroupId, cancellation) };
            case RemoteCommand.CreateGroup:
                return new RemoteResponse { Id = 0, Group = await workspaces.CreateGroup(Require(request.Name, "name"), request.ParentId, cancellation) };
            case RemoteCommand.MoveGroup:
                return new RemoteResponse { Id = 0, Group = await workspaces.MoveGroup(RequireGroup(request), request.ParentId, cancellation) };
            case RemoteCommand.RenameGroup:
                return new RemoteResponse { Id = 0, Group = await workspaces.RenameGroup(RequireGroup(request), Require(request.Name, "name"), cancellation) };
            case RemoteCommand.DeleteGroup:
                await workspaces.DeleteGroup(RequireGroup(request), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.ShiftWorkspace:
                await workspaces.ShiftWorkspace(RequireWorkspace(request), request.Offset ?? throw new RequestException("The request needs an offset."), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.ShiftGroup:
                await workspaces.ShiftGroup(RequireGroup(request), request.Offset ?? throw new RequestException("The request needs an offset."), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.DeleteWorkspace:
                await RequireIdle(request);
                await workspaces.Delete(RequireWorkspace(request), request.DeleteFolder, cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.ListFolders:
                return new RemoteResponse { Id = 0, Listing = await folders.List(request.Path, cancellation) };
            case RemoteCommand.CreateFolder:
                return new RemoteResponse { Id = 0, Listing = await folders.Create(Require(request.Path, "path"), Require(request.Name, "name"), cancellation) };
            case RemoteCommand.GetHistory:
                return new RemoteResponse { Id = 0, History = await prompts.History(RequireWorkspace(request), request.Before, request.Limit, cancellation) };
            case RemoteCommand.Prompt:
                RequireAiInput(request);
                await prompts.Prompt(RequireWorkspace(request), Require(request.Text, "text"), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.UploadAttachment:
                RequireAiInput(request);
                return new RemoteResponse { Id = 0, AttachmentPath = await prompts.Attach(RequireWorkspace(request), Require(request.Name, "name"), Require(request.Data, "data"), cancellation) };
            case RemoteCommand.ListFiles:
                return new RemoteResponse { Id = 0, Files = await files.List(await workspaces.Get(RequireWorkspace(request), cancellation), request.Path ?? string.Empty, request.ShowHidden == true, cancellation) };
            case RemoteCommand.SearchFiles:
                return new RemoteResponse { Id = 0, SearchResults = await files.Search(await workspaces.Get(RequireWorkspace(request), cancellation), Require(request.Text, "text"), request.SearchContent == true, request.ShowHidden == true, cancellation) };
            case RemoteCommand.ReadFile:
                return new RemoteResponse { Id = 0, Content = await files.Read(await workspaces.Get(RequireWorkspace(request), cancellation), Require(request.Path, "path"), cancellation) };
            case RemoteCommand.WatchFiles:
                Watch(owner, await workspaces.Get(RequireWorkspace(request), cancellation), request.Paths ?? []);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.CloneWorkspace:
                return new RemoteResponse { Id = 0, Workspace = await workspaces.Create(await git.Clone(Require(request.Path, "path"), Require(request.Url, "URL"), request.Name, cancellation), cancellation) };
            case RemoteCommand.GetGitStatus:
                return new RemoteResponse { Id = 0, Git = (await git.Status(await FolderOf(request, cancellation), cancellation)) with { Busy = versioning.IsBusy(RequireWorkspace(request)) } };
            case RemoteCommand.GetGitChanges:
                return new RemoteResponse { Id = 0, GitChanges = new GitChanges { Changes = await git.Changes(await FolderOf(request, cancellation), cancellation) } };
            case RemoteCommand.GetGitHistory:
                return new RemoteResponse { Id = 0, GitHistory = await git.History(await FolderOf(request, cancellation), request.Skip ?? 0, Math.Clamp(request.Limit ?? 30, 1, 200), cancellation) };
            case RemoteCommand.GetGitCommitChanges:
                return new RemoteResponse { Id = 0, GitChanges = new GitChanges { Changes = await git.CommitChanges(await FolderOf(request, cancellation), Require(request.Commit, "commit"), cancellation) } };
            case RemoteCommand.GetGitDiff:
                return new RemoteResponse { Id = 0, GitDiff = await git.Diff(await FolderOf(request, cancellation), Require(request.Path, "path"), request.Commit, cancellation) };
            case RemoteCommand.GetGitBranchState:
                return new RemoteResponse { Id = 0, GitBranch = await versioning.BranchState(await WorkspaceOf(request, cancellation), Require(request.Name, "name"), Require(request.Base, "base"), cancellation) };
            case RemoteCommand.GitCommit:
                RequireReady(request);
                await Act(request, true, (folder, token) => git.Commit(folder, Require(request.Text, "message").Trim() is { Length: > 0 } message ? message : throw new RequestException("Enter a commit message."), token), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitReset:
                RequireReady(request);
                await Act(request, true, (folder, token) => git.Reset(folder, token), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitCreateBranch:
                RequireReady(request);
                await Act(request, true, (folder, token) => git.CreateBranch(folder, Require(request.Name, "name").Trim(), token), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitCreateTag:
                RequireReady(request);
                await Act(request, true, (folder, token) => git.CreateTag(folder, Require(request.Name, "name").Trim(), token), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitSetRemote:
                RequireReady(request);
                await Act(request, false, (folder, token) => git.SetRemote(folder, Require(request.Url, "URL").Trim(), token), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitCheckout:
                RequireReady(request);
                await versioning.Checkout(await WorkspaceOf(request, cancellation), Require(request.Name, "name").Trim(), request.Force == true, cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitDeleteBranch:
                RequireReady(request);
                await versioning.DeleteBranch(await WorkspaceOf(request, cancellation), Require(request.Name, "name").Trim(), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitDeleteTag:
                RequireReady(request);
                await versioning.DeleteTag(await WorkspaceOf(request, cancellation), Require(request.Name, "name").Trim(), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitSquash:
                RequireReady(request);
                await versioning.Squash(await WorkspaceOf(request, cancellation), Require(request.Name, "name").Trim(), Require(request.Base, "base").Trim(), Require(request.Text, "message"), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitRebase:
                RequireReady(request);
                await versioning.Rebase(await WorkspaceOf(request, cancellation), Require(request.Name, "name").Trim(), Require(request.Base, "base").Trim(), string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim(), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitMerge:
                RequireReady(request);
                await versioning.Merge(await WorkspaceOf(request, cancellation), Require(request.Name, "name").Trim(), Require(request.Base, "base").Trim(), string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim(), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitFetch:
                RequireReady(request);
                await versioning.Fetch(await WorkspaceOf(request, cancellation), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitSync:
                await versioning.Sync(await WorkspaceOf(request, cancellation), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitSetIdentity:
                await git.SetIdentity(await FolderOf(request, cancellation), Require(request.Name, "name"), Require(request.Email, "email"), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GitCancel:
                await (activity.Get(RequireWorkspace(request)) == WorkspaceState.Managing ? control.Cancel(RequireWorkspace(request)) : versioning.Cancel(RequireWorkspace(request)));
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.StopWorkspace:
                await control.Stop(RequireWorkspace(request));
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.CancelWorkspace:
                await control.Cancel(RequireWorkspace(request));
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GetWorkspaceState:
                await workspaces.Get(RequireWorkspace(request), cancellation);
                return new RemoteResponse { Id = 0, State = activity.Get(RequireWorkspace(request)) };
            case RemoteCommand.GetScripts:
                await workspaces.Get(RequireWorkspace(request), cancellation);
                return new RemoteResponse { Id = 0, Scripts = scripts.List(RequireWorkspace(request)) };
            case RemoteCommand.RunScript:
                RequireReady(request);
                await scripts.Run(await WorkspaceOf(request, cancellation), Require(request.Path, "path"), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.StopScript:
                scripts.Stop(RequireWorkspace(request), Require(request.Path, "path"));
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.SendScriptInput:
                await scripts.SendInput(RequireWorkspace(request), Require(request.Path, "path"), Require(request.Text, "text"), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.RemoveScript:
                scripts.Remove(RequireWorkspace(request), Require(request.Path, "path"));
                return new RemoteResponse { Id = 0 };
            default:
                throw new RequestException("Unknown command.");
        }
    }

    /// <inheritdoc />
    public void Disconnect(object owner)
    {
        if (watches.TryRemove(owner, out IReadOnlyList<IDisposable>? watching))
        {
            Stop(watching);
        }
    }

    private void RequireReady(RemoteRequest request)
    {
        if (request.WorkspaceId is { } id && activity.Get(id) != WorkspaceState.Ready)
        {
            throw new RequestException(activity.Describe(id));
        }
    }

    private void RequireAiInput(RemoteRequest request)
    {
        if (request.WorkspaceId is { } id && activity.Get(id) is not (WorkspaceState.Ready or WorkspaceState.Working))
        {
            throw new RequestException(activity.Describe(id));
        }
    }

    private async Task RequireIdle(RemoteRequest request)
    {
        if (request.WorkspaceId is { } id)
        {
            RequireReady(request);
            await prompts.Release(id);
        }
    }

    private void Stop(IEnumerable<IDisposable> watching)
    {
        foreach (IDisposable watch in watching)
        {
            watch.Dispose();
        }
    }

    private void Watch(object owner, WorkspaceInfo workspace, IReadOnlyList<string> paths)
    {
        List<IDisposable> created = [];

        try
        {
            foreach (string path in paths.Distinct())
            {
                created.Add(files.Watch(workspace, path, changed => events.Publish(new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = workspace.Id, Path = changed })));
            }
        }
        catch
        {
            Stop(created);
            throw;
        }

        IReadOnlyList<IDisposable>? previous = null;
        watches.AddOrUpdate(owner, created, (_, existing) =>
        {
            previous = existing;
            return created;
        });

        if (previous is not null)
        {
            Stop(previous);
        }
    }

    private async Task<WorkspaceInfo> WorkspaceOf(RemoteRequest request, CancellationToken cancellation) => await workspaces.Get(RequireWorkspace(request), cancellation);

    private async Task Act(RemoteRequest request, bool revert, Func<string, CancellationToken, Task> action, CancellationToken cancellation)
    {
        WorkspaceInfo workspace = await WorkspaceOf(request, cancellation);
        await versioning.Perform(workspace, revert, token => action(workspace.Folder, token), cancellation);
    }

    private async Task<string> FolderOf(RemoteRequest request, CancellationToken cancellation) => (await workspaces.Get(RequireWorkspace(request), cancellation)).Folder;

    private Guid RequireGroup(RemoteRequest request) => request.GroupId ?? throw new RequestException("The request needs a group.");

    private Guid RequireWorkspace(RemoteRequest request) => request.WorkspaceId ?? throw new RequestException("The request needs a workspace.");

    private string Require(string? value, string field) => value ?? throw new RequestException($"The request needs a {field}.");
}
