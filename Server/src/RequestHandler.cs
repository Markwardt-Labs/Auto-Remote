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
internal sealed class RequestHandler(IWorkspaceManager workspaces, IFolderBrowser folders, IWorkspaceFileBrowser files, IGitService git, IWorkspaceVersioning versioning, ISavedInputs savedInputs, IErrorHistory errors, IWindowGuard guard, IPromptService prompts, IDiagramService diagrams, IScriptRunner scripts, IUsageService usage, IWorkspaceActivity activity, IWorkspaceControl control, ISpeechService speech, IPrompterHost host, IWorkspaceLoops loops, IWorkspaceWatcher watcher, IServerUpdater updater, ILog log, IWorkQueue queue, ISwarmService swarm) : IRequestHandler
{
    private readonly ConcurrentDictionary<object, IReadOnlyList<IDisposable>> watches = new();

    // A delegated task runs code another server sent, under permissions its sender was given, so its workspace takes nothing else: no further prompts or settings, and no git, which would run whatever configuration the AI wrote into a repository it made there.
    private readonly HashSet<RemoteCommand> refusedWhenDelegated =
    [
        RemoteCommand.Prompt, RemoteCommand.UploadAttachment, RemoteCommand.StartLoop, RemoteCommand.RenameWorkspace, RemoteCommand.MoveWorkspaceToFolder, RemoteCommand.SetWorkspaceSettings, RemoteCommand.GenerateCommitMessage, RemoteCommand.RunScript, RemoteCommand.DelegateTask,
        RemoteCommand.GetGitChanges, RemoteCommand.GetGitHistory, RemoteCommand.GetGitCommitChanges, RemoteCommand.GetGitDiff, RemoteCommand.GetGitBranchState, RemoteCommand.GitCommit, RemoteCommand.GitReset, RemoteCommand.GitCreateBranch, RemoteCommand.GitCreateTag, RemoteCommand.GitSetRemote, RemoteCommand.GitCheckout, RemoteCommand.GitDeleteBranch, RemoteCommand.GitDeleteTag, RemoteCommand.GitSquash, RemoteCommand.GitRebase, RemoteCommand.GitMerge, RemoteCommand.GitFetch, RemoteCommand.GitSync, RemoteCommand.GitSetIdentity
    ];

    private readonly HashSet<string> displayVariables = ["DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_SESSION_TYPE"];

    /// <inheritdoc />
    public async Task Open(ServerConfig config, CancellationToken cancellation)
    {
        await host.Configure(config, cancellation);
        prompts.Configure(cancellation);
        scripts.Configure(config, cancellation);
        speech.Configure(config);
        folders.Configure(config.DefaultFolder);
        await workspaces.Open(Path.Combine(config.DataFolder, "workspaces.json"), cancellation);
        await errors.Open(cancellation);
        guard.Open(cancellation);
        await queue.Open(config.DataFolder, cancellation);
        await swarm.Open(config, cancellation);
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

    private Task<WorkspaceInfo> CreateWorkspace(string folder, RemoteRequest request, CancellationToken cancellation)
        => workspaces.Create(folder, request.Service ?? host.DefaultService, request.ApiKey, request.GroupId, cancellation);

    private async Task<RemoteResponse> Dispatch(RemoteRequest request, object owner, CancellationToken cancellation)
    {
        if (request.WorkspaceId is { } target && swarm.IsDelegated(target))
        {
            if (refusedWhenDelegated.Contains(request.Command))
            {
                throw new RequestException("This workspace runs a task delegated by another server, so it takes nothing else.");
            }

            if (request.Command == RemoteCommand.GetGitStatus)
            {
                return new RemoteResponse { Id = 0, Git = new GitStatus { IsRepo = false } };
            }

            if (request.Command is RemoteCommand.StopWorkspace or RemoteCommand.CancelWorkspace)
            {
                await swarm.CancelByOwner(target);
                return new RemoteResponse { Id = 0 };
            }
        }

        switch (request.Command)
        {
            case RemoteCommand.ListWorkspaces:
                return new RemoteResponse { Id = 0, Catalog = await Catalog(cancellation) };
            case RemoteCommand.CreateWorkspace:
                return new RemoteResponse { Id = 0, Workspace = await CreateWorkspace(Require(request.Path, "path"), request, cancellation) };
            case RemoteCommand.RenameWorkspace:
                await RequireIdle(request);
                watcher.Forget(RequireWorkspace(request));
                return new RemoteResponse { Id = 0, Workspace = await workspaces.Rename(RequireWorkspace(request), Require(request.Name, "name"), cancellation) };
            case RemoteCommand.MoveWorkspaceToFolder:
                await RequireIdle(request);
                watcher.Forget(RequireWorkspace(request));
                return new RemoteResponse { Id = 0, Workspace = await workspaces.MoveToFolder(RequireWorkspace(request), Require(request.Path, "path"), cancellation) };
            case RemoteCommand.StartLoop:
                RequireAiInput(request);
                await loops.Start(RequireWorkspace(request), request.Loop ?? throw new RequestException("The request needs a loop."), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.CancelLoop:
                await loops.Cancel(RequireWorkspace(request));
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GetLoop:
                return new RemoteResponse { Id = 0, Loop = loops.Get(RequireWorkspace(request)) };
            case RemoteCommand.GetWorkspaceSettings:
                return new RemoteResponse { Id = 0, Settings = await prompts.GetSettings(RequireWorkspace(request), cancellation) };
            case RemoteCommand.SetWorkspaceSettings:
                return new RemoteResponse { Id = 0, Settings = await SetSettings(request, owner, cancellation) };
            case RemoteCommand.GetAiEnvironment:
                return new RemoteResponse { Id = 0, AiEnvironment = host.Get() };
            case RemoteCommand.SetAiEnvironment:
                AiEnvironment changed = await host.Set(request.AiEnvironment ?? throw new RequestException("The request needs an environment."), cancellation);
                await prompts.RefreshInstructions(cancellation);
                queue.Pump();
                return new RemoteResponse { Id = 0, AiEnvironment = changed };
            case RemoteCommand.ConfigureOllamaService:
                return new RemoteResponse { Id = 0, AiEnvironment = await host.ConfigureOllamaService(request.Enabled ?? throw new RequestException("The request needs enabled."), request.Password, cancellation) };
            case RemoteCommand.GetUsage:
                return new RemoteResponse { Id = 0, Usage = await usage.Get(request.Service ?? throw new RequestException("The request needs a service."), cancellation) };
            case RemoteCommand.ListLocalModels:
                return new RemoteResponse { Id = 0, Names = await host.ListLocalModels(request.Url, cancellation) };
            case RemoteCommand.ListImageModels:
                return new RemoteResponse { Id = 0, Names = await host.ListImageModels(request.Url, cancellation) };
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
            case RemoteCommand.ShiftGroup:
                await workspaces.ShiftGroup(RequireGroup(request), request.Offset ?? throw new RequestException("The request needs an offset."), cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.DeleteWorkspace:
                await RequireIdle(request);
                WorkspaceInfo deleting = await workspaces.Get(RequireWorkspace(request), cancellation);
                watcher.Forget(deleting.Id);
                await swarm.Forget(deleting.Id);
                await queue.Cancel(deleting.Id);
                await workspaces.Delete(deleting.Id, request.DeleteFolder, cancellation);
                await prompts.Forget(deleting, request.DeleteFolder);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.ListFolders:
                return new RemoteResponse { Id = 0, Listing = await folders.List(request.Path, cancellation) };
            case RemoteCommand.CreateFolder:
                return new RemoteResponse { Id = 0, Listing = await folders.Create(Require(request.Path, "path"), Require(request.Name, "name"), cancellation) };
            case RemoteCommand.GetHistory:
                return new RemoteResponse { Id = 0, History = (await prompts.History(RequireWorkspace(request), request.Before, request.Limit, request.Characters, cancellation)) with { LimitPause = guard.Get(RequireWorkspace(request)) } };
            case RemoteCommand.Prompt:
                RequireAiInput(request);
                return await Prompt(RequireWorkspace(request), Require(request.Text, "text"), request.IgnoreLimit == true, cancellation);
            case RemoteCommand.UploadAttachment:
                RequireAiInput(request);
                return new RemoteResponse { Id = 0, AttachmentPath = await prompts.Attach(RequireWorkspace(request), Require(request.Name, "name"), Require(request.Data, "data"), cancellation) };
            case RemoteCommand.TranscribeSavedInput:
                return new RemoteResponse { Id = 0, SavedInputs = await savedInputs.Transcribe(RequireWorkspace(request), Require(request.Data, "data"), cancellation) };
            case RemoteCommand.DiscardSavedInput:
                return new RemoteResponse { Id = 0, SavedInputs = await savedInputs.Discard(RequireWorkspace(request), request.InputId ?? throw new RequestException("The request needs a saved input."), cancellation) };
            case RemoteCommand.UpdateServer:
                await updater.Update(cancellation);
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.ListFiles:
                return new RemoteResponse { Id = 0, Files = await files.List(await workspaces.Get(RequireWorkspace(request), cancellation), request.Path ?? string.Empty, request.ShowHidden == true, cancellation) };
            case RemoteCommand.SearchFiles:
                return new RemoteResponse { Id = 0, SearchResults = await files.Search(await workspaces.Get(RequireWorkspace(request), cancellation), Require(request.Text, "text"), request.SearchContent == true, request.ShowHidden == true, cancellation) };
            case RemoteCommand.ReadFile:
                return new RemoteResponse { Id = 0, Content = await files.Read(await workspaces.Get(RequireWorkspace(request), cancellation), Require(request.Path, "path"), cancellation) };
            case RemoteCommand.WatchWorkspace:
                Watch(owner, await workspaces.Get(RequireWorkspace(request), cancellation));
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.CloneWorkspace:
                return new RemoteResponse { Id = 0, Workspace = await CreateWorkspace(await git.Clone(Require(request.Path, "path"), Require(request.Url, "URL"), request.Name, cancellation), request, cancellation) };
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
            case RemoteCommand.SetShowWork:
                return new RemoteResponse { Id = 0, Workspace = await workspaces.SetShowWork(RequireWorkspace(request), request.ShowWork ?? throw new RequestException("The request needs ShowWork."), cancellation) };
            case RemoteCommand.RenderDiagram:
                return new RemoteResponse { Id = 0, Diagram = await diagrams.Render(Require(request.Text, "text"), cancellation) };
            case RemoteCommand.CancelLimitPause:
                guard.Cancel(RequireWorkspace(request));
                await loops.Cancel(RequireWorkspace(request));
                return new RemoteResponse { Id = 0 };
            case RemoteCommand.GetErrors:
                return new RemoteResponse { Id = 0, Errors = await errors.List(RequireWorkspace(request), cancellation) };
            case RemoteCommand.GetSwarm:
                return await Swarm(() => Task.CompletedTask, cancellation);
            case RemoteCommand.SetSwarm:
                return await Swarm(() => swarm.SetServer(request.SwarmServer ?? throw new RequestException("The request needs swarm settings.")), cancellation);
            case RemoteCommand.SetSwarmPeer:
                return await Swarm(() => swarm.SetPeer(request.SwarmPeer ?? throw new RequestException("The request needs a paired server.")), cancellation);
            case RemoteCommand.RevokeSwarmPeer:
                return await Swarm(() => swarm.Revoke(Require(request.PeerId, "peer")), cancellation);
            case RemoteCommand.CreatePairingCode:
                return await Swarm(() =>
                {
                    swarm.CreateCode();
                    return Task.CompletedTask;
                }, cancellation);
            case RemoteCommand.PairWithCode:
                return await Swarm(async () => await swarm.PairWithCode(Require(request.Address, "address"), Require(request.Code, "code"), cancellation), cancellation);
            case RemoteCommand.WriteDriveInvitation:
                return await Swarm(async () => await swarm.WriteInvitation(cancellation), cancellation);
            case RemoteCommand.ConfirmDrivePairing:
                return await Swarm(() => swarm.ConfirmDrivePairing(Require(request.PeerId, "peer"), cancellation), cancellation);
            case RemoteCommand.SyncDrive:
                return await Swarm(async () => await swarm.SyncDrive(cancellation), cancellation);
            case RemoteCommand.DelegateTask:
                return await Swarm(() => swarm.Delegate(RequireWorkspace(request), request.Task ?? throw new RequestException("The request needs a task."), cancellation), cancellation);
            case RemoteCommand.GetSwarmTask:
                return new RemoteResponse { Id = 0, SwarmTask = await swarm.Detail(RequireTask(request), cancellation) };
            case RemoteCommand.CancelSwarmTask:
                return await Swarm(() => swarm.Cancel(RequireTask(request)), cancellation);
            case RemoteCommand.RetrySwarmTask:
                return await Swarm(() => swarm.Retry(RequireTask(request), cancellation), cancellation);
            case RemoteCommand.ApplySwarmTask:
                return await Swarm(async () => await swarm.Apply(RequireTask(request), cancellation), cancellation);
            case RemoteCommand.DiscardSwarmTask:
                return await Swarm(() => swarm.Discard(RequireTask(request), cancellation), cancellation);
            case RemoteCommand.GetQueuedWork:
                await workspaces.Get(RequireWorkspace(request), cancellation);
                return new RemoteResponse { Id = 0, Queue = queue.Get(RequireWorkspace(request)) };
            case RemoteCommand.CancelQueuedWork:
                return new RemoteResponse { Id = 0, Queue = await CancelQueued(RequireWorkspace(request), request.Keep == true, cancellation) };
            case RemoteCommand.GetSavedInputs:
                return new RemoteResponse { Id = 0, SavedInputs = await savedInputs.List(RequireWorkspace(request), cancellation) };
            case RemoteCommand.SaveInput:
                return new RemoteResponse { Id = 0, SavedInputs = await savedInputs.Save(RequireWorkspace(request), request.Text ?? string.Empty, cancellation) };
            case RemoteCommand.TakeSavedInput:
                return new RemoteResponse { Id = 0, SavedInput = await savedInputs.Take(RequireWorkspace(request), request.InputId ?? throw new RequestException("The request needs a saved input."), cancellation) };
            case RemoteCommand.GenerateCommitMessage:
                RequireReady(request);
                return new RemoteResponse { Id = 0, CommitMessage = await versioning.CommitMessage(await WorkspaceOf(request, cancellation), cancellation) };
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
                await (activity.Get(RequireWorkspace(request)) == WorkspaceState.Managing ? control.Stop(RequireWorkspace(request)) : versioning.Cancel(RequireWorkspace(request)));
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
                await scripts.Run(await WorkspaceOf(request, cancellation), Require(request.Path, "path"), DisplayOf(request, owner), cancellation);
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

    private Dictionary<string, string> DisplayOf(RemoteRequest request, object owner)
    {
        Dictionary<string, string> display = [];

        if (request.Environment is null || !IsLocal(owner))
        {
            return display;
        }

        foreach ((string name, string value) in request.Environment)
        {
            if (displayVariables.Contains(name) && value.Length is > 0 and <= 4096 && !value.Contains('\0', StringComparison.Ordinal))
            {
                display[name] = value;
            }
        }

        return display;
    }

    private bool IsLocal(object owner) => owner is TcpClient { Client: { RemoteEndPoint: IPEndPoint remote, LocalEndPoint: IPEndPoint local } } && (IPAddress.IsLoopback(remote.Address) || remote.Address.Equals(local.Address));

    private void RequireReady(RemoteRequest request)
    {
        if (request.WorkspaceId is { } id && activity.Get(id) != WorkspaceState.Ready)
        {
            throw new RequestException(activity.Describe(id));
        }
    }

    private async Task<RemoteResponse> Prompt(Guid id, string text, bool ignoreLimit, CancellationToken cancellation)
    {
        if (ignoreLimit)
        {
            guard.Exempt(id);
        }
        else if (await guard.Over(id, (await workspaces.Get(id, cancellation)).Service, text, cancellation) is { } over)
        {
            return new RemoteResponse { Id = 0, Error = $"The {over.Window} usage window is at {over.PercentUsed:0.#}%, at or past the maximum of {over.MaxPercent:0.#}%. Confirm to start the work anyway.", OverLimit = over };
        }

        WorkTicket ticket;

        try
        {
            ticket = await queue.Submit(id, text, WorkSource.Direct, cancellation);
        }
        catch (RequestException)
        {
            if (ignoreLimit)
            {
                guard.Unexempt(id);
            }

            throw;
        }

        return new RemoteResponse { Id = 0, Queue = ticket.Queued ? queue.Get(id) : null };
    }

    private async Task<RemoteResponse> Swarm(Func<Task<string?>> action, CancellationToken cancellation)
    {
        string? report = await action();
        return new RemoteResponse { Id = 0, Swarm = await swarm.Status(report, cancellation) };
    }

    private Task<RemoteResponse> Swarm(Func<Task> action, CancellationToken cancellation) => Swarm(async () =>
    {
        await action();
        return (string?)null;
    }, cancellation);

    private Guid RequireTask(RemoteRequest request) => request.TaskId ?? throw new RequestException("The request needs a task.");

    private async Task<QueueStatus?> CancelQueued(Guid id, bool keep, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);

        if (swarm.IsDelegated(id))
        {
            QueueStatus? waiting = queue.Get(id);
            await swarm.CancelByOwner(id);
            return waiting;
        }

        QueueStatus? removed = await queue.Cancel(id);

        if (keep && removed is { Text.Length: > 0 })
        {
            await savedInputs.Keep(id, removed.Text, cancellation);
        }

        return removed;
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
            await loops.Cancel(id);
            await prompts.Release(id);
            await scripts.StopAll(id);
        }
    }

    private void Stop(IEnumerable<IDisposable> watching)
    {
        foreach (IDisposable watch in watching)
        {
            watch.Dispose();
        }
    }

    private void Watch(object owner, WorkspaceInfo workspace)
    {
        IReadOnlyList<IDisposable> leased = [watcher.Watch(workspace)];
        IReadOnlyList<IDisposable>? previous = null;
        watches.AddOrUpdate(owner, leased, (_, existing) =>
        {
            previous = existing;
            return leased;
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

    private async Task<WorkspaceSettings> SetSettings(RemoteRequest request, object owner, CancellationToken cancellation)
    {
        Guid id = RequireWorkspace(request);
        WorkspaceSettings wanted = request.Settings ?? throw new RequestException("The request needs settings.");
        WorkspaceSettings before = await prompts.GetSettings(id, cancellation);
        WorkspaceSettings after = await prompts.SetSettings(id, wanted, cancellation);
        List<string> changes = [];

        void Note<T>(string name, T was, T now)
        {
            if (!EqualityComparer<T>.Default.Equals(was, now))
            {
                changes.Add($"{name} {was} -> {now}");
            }
        }

        Note("capability", before.Capability, after.Capability);
        Note("effort", before.Effort, after.Effort);
        Note("shell", before.IsShellAllowed, after.IsShellAllowed);
        Note("files outside the workspace", before.IsSystemAccessAllowed, after.IsSystemAccessAllowed);
        Note("web", before.IsWebAllowed, after.IsWebAllowed);
        Note("images", before.IsImageGenerationAllowed, after.IsImageGenerationAllowed);
        Note("delegation", before.IsDelegationAllowed, after.IsDelegationAllowed);
        Note("instructions", before.Instructions ?? string.Empty, after.Instructions ?? string.Empty);
        Note("status interval", before.StatusMinutes, after.StatusMinutes);
        Note("review prompt", before.ReviewPrompt ?? string.Empty, after.ReviewPrompt ?? string.Empty);
        Note("expand prompt", before.ExpandPrompt ?? string.Empty, after.ExpandPrompt ?? string.Empty);

        if (changes.Count > 0)
        {
            string from = owner is TcpClient { Client.RemoteEndPoint: { } endpoint } ? endpoint.ToString()! : "a client";
            log.Write($"Settings of {(await workspaces.Get(id, cancellation)).Name} changed by {from}: {string.Join(", ", changes)}");
        }

        return after;
    }

    private async Task<WorkspaceCatalog> Catalog(CancellationToken cancellation)
    {
        WorkspaceCatalog catalog = await workspaces.List(cancellation);
        return catalog with { Looping = [.. catalog.Workspaces.Where(workspace => loops.Get(workspace.Id) is not null).Select(workspace => workspace.Id)], Queued = queue.Waiting, Delegations = swarm.Delegations };
    }

    private Guid RequireWorkspace(RemoteRequest request) => request.WorkspaceId ?? throw new RequestException("The request needs a workspace.");

    private string Require(string? value, string field) => value ?? throw new RequestException($"The request needs a {field}.");
}
