namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The tasks other servers delegate to this one: each runs in a workspace of its own, made from the code of the task with only the permissions the owner gave the sender, on the local AI, in turn with the other work. The results of the tasks it builds on are given to its AI as read only copies beside the code, to combine itself. Its result is one commit on the code it started from, with the AI's report and conversation, also when the AI says it could not finish. The workspace is removed once the sender has the result.
/// </summary>
internal interface ISwarmWorker
{
    /// <summary>
    /// Gets the workspaces that run delegated tasks.
    /// </summary>
    IReadOnlyList<WorkspaceDelegation> Delegations { get; }

    /// <summary>
    /// Continues with the tasks that had not ended when the server stopped.
    /// </summary>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the tasks are picked up again.</returns>
    Task Open(CancellationToken cancellation);

    /// <summary>
    /// Takes a task that arrived; a task taken before is ignored.
    /// </summary>
    /// <param name="peerId">The sender.</param>
    /// <param name="task">The task.</param>
    /// <param name="bundle">The code of the task, when it travels as a bundle.</param>
    /// <param name="isExpired">Whether it was made so long ago that it is only answered, never run.</param>
    /// <returns>A task that completes once the task is recorded; the work goes on afterwards.</returns>
    Task Receive(string peerId, SwarmTaskBody task, ReadOnlyMemory<byte> bundle, bool isExpired);

    /// <summary>
    /// Cancels a task, which is also remembered when the task has not arrived yet, so it is not run when it does.
    /// </summary>
    /// <param name="peerId">The sender of the task.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="reason">Why, which the sender is told.</param>
    /// <returns>A task that completes once the cancellation is underway.</returns>
    Task Cancel(string peerId, Guid taskId, string reason);

    /// <summary>
    /// Cancels the task a workspace runs, as the owner of this server stopping its work does.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>A task that completes once the cancellation is underway.</returns>
    Task CancelByOwner(Guid workspaceId);

    /// <summary>
    /// Takes the word of the AI of a delegated task that it could not finish it, so the task ends as failed with that reason; does nothing for another workspace.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task that completes once noted.</returns>
    Task Unfinished(Guid workspaceId, string reason);

    /// <summary>
    /// Removes the workspace of a task once the sender acknowledged its result.
    /// </summary>
    /// <param name="messageId">The acknowledged package.</param>
    /// <returns>A task that completes once done.</returns>
    Task Delivered(Guid messageId);

    /// <summary>
    /// Ends every task of a server that is no longer paired, without telling it.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <returns>A task that completes once done.</returns>
    Task Revoke(string peerId);

    /// <summary>
    /// Says what this server can do for a paired server now.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <returns>The profile.</returns>
    Task<SwarmProfileBody> Profile(string peerId);

    /// <summary>
    /// Says how the tasks of a paired server are getting on, with the messages not sent before over this connection.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <param name="sent">What was sent over this connection, by task, which is updated; <see langword="null" /> for a delivery that cannot follow a conversation, which sends the state only.</param>
    /// <returns>The progress, or <see langword="null" /> when there is nothing new.</returns>
    Task<SwarmProgressBody?> Progress(string peerId, IDictionary<Guid, (SwarmTaskState State, int Messages)>? sent);

    /// <summary>
    /// Picks up any task that is neither being worked on nor ended, as one whose setup or result failed to be stored would be, and forgets old records.
    /// </summary>
    /// <returns>A task that completes once done.</returns>
    Task Sweep();

    /// <summary>
    /// Checks whether a workspace runs a delegated task.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns><see langword="true" /> if it does.</returns>
    bool IsDelegated(Guid workspaceId);
}

/// <inheritdoc />
internal sealed class SwarmWorker : ISwarmWorker
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmWorker" /> class.
    /// </summary>
    /// <param name="book">What the swarm stores.</param>
    /// <param name="courier">Sends the results.</param>
    /// <param name="repository">The git work.</param>
    /// <param name="workspaces">Makes the workspaces of the tasks.</param>
    /// <param name="prompts">Runs them.</param>
    /// <param name="queue">Takes their turn on the local AI.</param>
    /// <param name="store">Keeps their conversations.</param>
    /// <param name="host">The local models.</param>
    /// <param name="events">Tells when their work ends or fails.</param>
    /// <param name="loadedModels">Asks the model server which models it has loaded.</param>
    public SwarmWorker(ISwarmBook book, ISwarmCourier courier, ISwarmRepository repository, IWorkspaceManager workspaces, IPromptService prompts, IWorkQueue queue, IWorkspaceStore store, IPrompterHost host, IEventHub events, Func<AiEnvironment, CancellationToken, Task<IReadOnlyList<string>>>? loadedModels = null)
    {
        this.book = book;
        this.courier = courier;
        this.repository = repository;
        this.workspaces = workspaces;
        this.prompts = prompts;
        this.queue = queue;
        this.store = store;
        this.host = host;
        loaded = loadedModels ?? LoadedModels;
        events.Subscribe(Follow);
    }

    private readonly ISwarmBook book;

    private readonly ISwarmCourier courier;

    private readonly ISwarmRepository repository;

    private readonly IWorkspaceManager workspaces;

    private readonly IPromptService prompts;

    private readonly IWorkQueue queue;

    private readonly IWorkspaceStore store;

    private readonly IPrompterHost host;

    private readonly Func<AiEnvironment, CancellationToken, Task<IReadOnlyList<string>>> loaded;

    private readonly ConcurrentDictionary<Guid, (Guid TaskId, WorkspaceDelegation Delegation)> delegated = new();

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> finishing = new();

    private readonly ConcurrentDictionary<Guid, string> failures = new();

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Since, int Attempts)> failing = new();

    private readonly ConcurrentDictionary<Guid, byte> completing = new();

    private readonly int leastAttempts = 3;

    private readonly TimeSpan sendPatience = TimeSpan.FromMinutes(30);

    private readonly string dependencyShare = "swarm-dependencies";

    private readonly ConcurrentDictionary<Guid, byte> starting = new();

    private readonly int messageLimit = 400;

    private readonly int placeholderLimit = 1000;

    private readonly TimeSpan kept = TimeSpan.FromDays(90);

    private readonly string instructions = "You are working on a task another server of the user delegated to you. Work only in this workspace. You cannot ask anyone questions: decide yourself and say what you assumed. The workspace is not a git repository, so do not rely on git. When you are done, end with a report that says what you changed, how you checked it (with the commands you ran and their result, or why you could not run any) and anything left open. If you cannot finish the task, call report_unfinished with the reason and still end with that report.";

    private readonly int reasonLimit = 1000;

    private readonly int buildingReportLimit = 3000;

    /// <inheritdoc />
    public IReadOnlyList<WorkspaceDelegation> Delegations => [.. delegated.Values.Select(entry => entry.Delegation)];

    /// <inheritdoc />
    public async Task Open(CancellationToken cancellation)
    {
        List<(Guid Id, SwarmTaskState State, Guid? Workspace, string PeerName, string Title, bool Cleaned)> known = await book.Read(document => document.Incoming.OrderBy(task => task.CreatedAt).Select(task => (task.Id, task.State, task.WorkspaceId, task.PeerName, task.Title, task.IsCleaned)).ToList());

        if (known.Any(task => task.Workspace is not null && !task.Cleaned))
        {
            IgnoreProjectConfiguration();
        }

        foreach ((Guid id, SwarmTaskState state, Guid? workspace, string peer, string title, bool cleaned) in known)
        {
            if (workspace is { } at && !cleaned)
            {
                delegated[at] = (id, new WorkspaceDelegation { WorkspaceId = at, From = peer, Title = title });
            }

            if (!Active(state))
            {
                continue;
            }

            if (workspace is not { } running)
            {
                _ = Prepare(id);
            }
            else if (state == SwarmTaskState.Queued)
            {
                _ = Resume(id, running);
            }
            else if (await book.Read(document => document.Incoming.First(task => task.Id == id).IsWorkDone))
            {
                CompleteApart(id);
            }
            else
            {
                string interrupted = "The work was interrupted by a restart of the worker.";
                // Remembered too in case answering has to wait for the next sweep, which would otherwise take the half done work as done.
                failures[id] = interrupted;
                await Finish(id, SwarmOutcome.Failed, null, interrupted);
            }
        }
    }

    /// <inheritdoc />
    public async Task Receive(string peerId, SwarmTaskBody task, ReadOnlyMemory<byte> bundle, bool isExpired)
    {
        Check(task);

        // A task sent again with its whole history after this worker asked for it starts over; anything else known is a repeat.
        bool known = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == task.TaskId) is { } existing && (existing.PeerId != peerId || existing.State != SwarmTaskState.Failed || existing.Error != MissingHistoryMessage()));

        if (known || !starting.TryAdd(task.TaskId, 0))
        {
            return;
        }

        string? refusal;

        try
        {
            // One this old may be a repeat of a task that was forgotten since, so it is never run.
            refusal = isExpired ? "The task took more than 90 days to arrive, so it was not run." : await Refusal(peerId, task.Capability, task.Mode);

            // The code is stored before the task is recorded, so a failure to store it has the package taken again.
            if (refusal is null && !bundle.IsEmpty)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BundlePath(task.TaskId))!);
                await File.WriteAllBytesAsync(BundlePath(task.TaskId), bundle);
            }

            await book.Change(document =>
            {
                document.Incoming.RemoveAll(entry => entry.Id == task.TaskId);
                string peerName = document.Peers.First(peer => peer.Identity.Id == peerId).Name;

                // Nothing of a refused task is kept but what answering it needs.
                document.Incoming.Add(new SwarmIncoming { Id = task.TaskId, JobId = task.JobId, PeerId = peerId, PeerName = peerName, Title = task.Title, Instructions = refusal is null ? task.Instructions : string.Empty, ExpectedResult = refusal is null ? task.ExpectedResult : string.Empty, Capability = task.Capability, Mode = task.Mode, RepoKey = SafeKey(task.RepoKey), BaseCommit = task.BaseCommit, RemoteUrl = task.RemoteUrl, Dependencies = refusal is null ? [.. task.Dependencies] : [], State = SwarmTaskState.Queued, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            });

            if (refusal is not null)
            {
                await Finish(task.TaskId, SwarmOutcome.Failed, null, refusal);
            }
        }
        finally
        {
            starting.TryRemove(task.TaskId, out _);
        }

        if (refusal is null)
        {
            _ = Prepare(task.TaskId);
        }
    }

    /// <inheritdoc />
    public async Task Cancel(string peerId, Guid taskId, string reason)
    {
        (SwarmTaskState? state, Guid? workspace) = await book.Change<(SwarmTaskState?, Guid?)>(document =>
        {
            if (document.Incoming.FirstOrDefault(entry => entry.Id == taskId) is not { } task)
            {
                if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId) is { } sender && document.Incoming.Count(entry => entry.PeerId == peerId && entry.Instructions.Length == 0) < placeholderLimit)
                {
                    document.Incoming.Add(new SwarmIncoming { Id = taskId, PeerId = peerId, PeerName = sender.Name, Title = "Cancelled task", State = SwarmTaskState.Cancelled, IsCleaned = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
                }

                return (null, null);
            }

            if (task.PeerId != peerId || !Active(task.State))
            {
                return (null, null);
            }

            task.CancelReason ??= reason;
            return (task.State, task.WorkspaceId);
        });

        if (state is null)
        {
            return;
        }

        if (finishing.TryGetValue(taskId, out SemaphoreSlim? busy) && busy.CurrentCount == 0)
        {
            return;
        }

        if (workspace is not { } at)
        {
            if (!starting.ContainsKey(taskId))
            {
                await Finish(taskId, SwarmOutcome.Cancelled, null, reason);
            }

            return;
        }

        if (queue.Get(at) is not null)
        {
            await queue.Cancel(at);
        }

        if (prompts.IsBusy(at))
        {
            await prompts.Interrupt(at);
        }

        if (!prompts.IsBusy(at) && !starting.ContainsKey(taskId))
        {
            await Finish(taskId, SwarmOutcome.Cancelled, null, reason);
        }
    }

    /// <inheritdoc />
    public async Task CancelByOwner(Guid workspaceId)
    {
        if (!delegated.TryGetValue(workspaceId, out (Guid TaskId, WorkspaceDelegation Delegation) entry))
        {
            return;
        }

        if (await book.Read(document => document.Incoming.FirstOrDefault(task => task.Id == entry.TaskId)?.PeerId) is { } peer)
        {
            await Cancel(peer, entry.TaskId, "The owner of the worker stopped it.");
        }
    }

    /// <inheritdoc />
    public async Task Unfinished(Guid workspaceId, string reason)
    {
        if (!delegated.TryGetValue(workspaceId, out (Guid TaskId, WorkspaceDelegation Delegation) entry))
        {
            return;
        }

        string why = string.IsNullOrWhiteSpace(reason) ? "No reason was given." : reason.Trim();

        // Kept at once, since the work may end and the server may stop before the result is sent.
        await book.Change(document =>
        {
            if (document.Incoming.FirstOrDefault(task => task.Id == entry.TaskId) is { } task && Active(task.State))
            {
                task.Unfinished = why.Length > reasonLimit ? $"{why[..reasonLimit]}..." : why;
            }
        }, false);
    }

    /// <inheritdoc />
    public async Task Delivered(Guid messageId)
    {
        Guid? done = await book.Read(document => document.Incoming.FirstOrDefault(task => task.ResultMessageId == messageId && !task.IsCleaned)?.Id);

        if (done is { } id)
        {
            await Clean(id);
        }
    }

    /// <inheritdoc />
    public async Task Revoke(string peerId)
    {
        List<Guid> tasks = await book.Read(document => document.Incoming.Where(task => task.PeerId == peerId).Select(task => task.Id).ToList());

        foreach (Guid id in tasks)
        {
            Guid? workspace = await book.Change(document =>
            {
                SwarmIncoming task = document.Incoming.First(task => task.Id == id);

                if (Active(task.State))
                {
                    task.State = SwarmTaskState.Cancelled;
                    task.Error = "The sender is no longer paired.";
                }

                return task.WorkspaceId;
            });

            if (workspace is { } at)
            {
                await queue.Cancel(at);
                await prompts.Interrupt(at);
            }

            await Clean(id);
        }

        await book.Change(document => document.Incoming.RemoveAll(task => task.PeerId == peerId));
    }

    /// <inheritdoc />
    public async Task<SwarmProfileBody> Profile(string peerId)
    {
        AiEnvironment environment = host.Get();
        (string name, bool accepts, int waiting, SwarmPermissions permissions) = await book.Read(document => (document.Server.Name, document.Server.AcceptDelegatedWork && document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId)?.AcceptWork == true, document.Incoming.Count(task => task.State == SwarmTaskState.Queued && task.WorkspaceId is null), document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId)?.Permissions ?? new SwarmPermissions()));
        List<SwarmCapability> capabilities = [.. Enum.GetValues<AiCapability>().Select(capability => (Capability: capability, Model: Model(environment, capability))).Where(item => item.Model is not null).Select(item => new SwarmCapability { Capability = item.Capability, Model = item.Model!, Context = Context(environment, item.Capability) })];
        IReadOnlyList<string> models = accepts ? await loaded(environment, CancellationToken.None) : [];
        List<AiCapability> warm = capabilities.Where(capability => models.Any(model => model == capability.Model || model.StartsWith($"{capability.Model}-liveai-ctx", StringComparison.Ordinal) || model == $"{capability.Model}:latest")).Select(capability => capability.Capability).ToList();
        return new SwarmProfileBody { Name = name, Version = SwarmProfileBody.DependencyVersion, AcceptsWork = accepts, FreeSlots = accepts ? Math.Max(0, queue.FreeDelegatedSlots - waiting) : 0, Capabilities = capabilities, LoadedCapabilities = warm, Permissions = permissions };
    }

    /// <inheritdoc />
    public async Task<SwarmProgressBody?> Progress(string peerId, IDictionary<Guid, (SwarmTaskState State, int Messages)>? sent)
    {
        List<(Guid Id, SwarmTaskState State, Guid? Workspace)> active = await book.Read(document => document.Incoming.Where(task => task.PeerId == peerId && Active(task.State)).Select(task => (task.Id, task.State, task.WorkspaceId)).ToList());
        List<SwarmTaskProgress> progress = [];

        foreach ((Guid id, SwarmTaskState state, Guid? workspace) in active)
        {
            if (sent is null)
            {
                progress.Add(new SwarmTaskProgress { TaskId = id, State = state });
                continue;
            }

            IReadOnlyList<ChatMessage> messages = workspace is { } at ? await store.LoadMessages(at, CancellationToken.None) ?? [] : [];
            bool first = !sent.TryGetValue(id, out (SwarmTaskState State, int Messages) before);
            int offset = first ? 0 : Math.Min(before.Messages, messages.Count);
            List<ChatMessage> added = [.. messages.Skip(offset).Take(messageLimit)];

            if (first || before.State != state || added.Count > 0)
            {
                progress.Add(new SwarmTaskProgress { TaskId = id, State = state, Offset = offset, Messages = added });
                sent[id] = (state, offset + added.Count);
            }
        }

        return progress.Count > 0 ? new SwarmProgressBody { Tasks = progress } : null;
    }

    /// <inheritdoc />
    public async Task Sweep()
    {
        List<(Guid Id, SwarmTaskState State, Guid? Workspace)> active = await book.Read(document => document.Incoming.Where(task => Active(task.State)).OrderBy(task => task.CreatedAt).Select(task => (task.Id, task.State, task.WorkspaceId)).ToList());

        foreach ((Guid id, SwarmTaskState state, Guid? workspace) in active.Where(task => !starting.ContainsKey(task.Id)))
        {
            if (workspace is not { } at)
            {
                _ = Prepare(id);
            }
            else if (!prompts.IsBusy(at) && queue.Get(at) is null)
            {
                if (state == SwarmTaskState.Running)
                {
                    CompleteApart(id);
                }
                else
                {
                    _ = Resume(id, at);
                }
            }
        }

        // A cleanup that was cut short has no other occasion once the acknowledgment of its result was taken.
        foreach ((Guid id, string peer, Guid? result) in await book.Read(document => document.Incoming.Where(task => !Active(task.State) && !task.IsCleaned && document.Peers.Any(peer => peer.Identity.Id == task.PeerId)).Select(task => (task.Id, task.PeerId, task.ResultMessageId)).ToList()))
        {
            if (result is not { } message || !courier.Waits(peer, message))
            {
                try
                {
                    await Clean(id);
                }
                catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - kept;

        if (await book.Read(document => document.Incoming.Any(task => task.IsCleaned && !Active(task.State) && task.UpdatedAt < cutoff)))
        {
            await book.Change(document => document.Incoming.RemoveAll(task => task.IsCleaned && !Active(task.State) && task.UpdatedAt < cutoff));
        }
    }

    /// <inheritdoc />
    public bool IsDelegated(Guid workspaceId) => delegated.ContainsKey(workspaceId);

    private async Task<IReadOnlyList<string>> LoadedModels(AiEnvironment environment, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(environment.LocalServer, UriKind.Absolute, out Uri? server))
        {
            return [];
        }

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
            using JsonDocument running = JsonDocument.Parse(await client.GetStringAsync(new Uri(server, "/api/ps"), cancellation));
            return [.. running.RootElement.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("name").GetString() ?? string.Empty)];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    // A task counts as starting from its setup until its prompt has started or never will, so a cancellation meanwhile is left to be acted on here.
    private async Task Prepare(Guid id)
    {
        if (!starting.TryAdd(id, 0))
        {
            return;
        }

        try
        {
            if (await SetUp(id) is { } workspace)
            {
                await Start(id, workspace);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await Finish(id, SwarmOutcome.Failed, null, $"The worker could not set the task up: {exception.Message}");
        }
        finally
        {
            starting.TryRemove(id, out _);

            // A revocation meanwhile removed the task while its files may still have been written.
            if (!await book.Read(document => document.Incoming.Any(task => task.Id == id && (Active(task.State) || !task.IsCleaned))))
            {
                await Discard(id);
            }
        }
    }

    // After a restart the permissions are set again, since the server may have stopped before they were.
    private async Task Resume(Guid id, Guid workspaceId)
    {
        if (!starting.TryAdd(id, 0))
        {
            return;
        }

        try
        {
            IgnoreProjectConfiguration();
            await Restrict(id, workspaceId);
            await Start(id, workspaceId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await Finish(id, SwarmOutcome.Failed, null, $"The worker could not resume the task: {exception.Message}");
        }
        finally
        {
            starting.TryRemove(id, out _);
        }
    }

    private async Task<Guid?> SetUp(Guid id)
    {
        SwarmIncoming? task = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id));

        if (task is null || !Active(task.State))
        {
            return null;
        }

        // A setup that a restart cut short may have left code behind, and a workspace for it that has no restrictions.
        if (task.Folder is { } left)
        {
            await Remove(id, left);
        }

        if (task.CancelReason is { } cancelled)
        {
            await Finish(id, SwarmOutcome.Cancelled, null, cancelled);
            return null;
        }

        // Checked again, since the owner's settings may have changed and a refused task is recorded without its instructions.
        if ((await Refusal(task.PeerId, task.Capability, task.Mode) ?? (task.Instructions.Length == 0 ? "This server did not accept the task." : null)) is { } refusal)
        {
            await Finish(id, SwarmOutcome.Failed, null, refusal);
            return null;
        }

        string cache = Cache(task);

        if (task.Mode == SwarmMode.GitRemote)
        {
            string url = task.RemoteUrl ?? throw new RequestException("The task names no remote.");
            await repository.FetchBase(cache, url, $"swarm/tasks/{id:N}", id, task.BaseCommit, CancellationToken.None);

            foreach (SwarmDependency dependency in task.Dependencies.Where(dependency => dependency.ResultCommit is not null))
            {
                await repository.FetchDependency(cache, url, $"swarm/dependencies/{id:N}/{dependency.TaskId:N}", id, dependency.TaskId, dependency.ResultCommit!, CancellationToken.None);
            }
        }
        else
        {
            ReadOnlyMemory<byte> bundle = File.Exists(BundlePath(id)) ? await File.ReadAllBytesAsync(BundlePath(id)) : ReadOnlyMemory<byte>.Empty;

            if (!await repository.Receive(cache, bundle, id, task.BaseCommit, task.Dependencies, CancellationToken.None))
            {
                await Finish(id, SwarmOutcome.MissingHistory, null, MissingHistoryMessage());
                return null;
            }
        }

        IgnoreProjectConfiguration();
        string folder = Path.Combine(await Root(), $"task-{id:N}");
        // Recorded first, so whatever a restart leaves of the setup can be found again.
        await book.Change(document => document.Incoming.First(entry => entry.Id == id).Folder = folder, false);
        await repository.Export(cache, folder, task.BaseCommit, CancellationToken.None);

        // Beside the folder of the task, so nothing of them is taken for its changes.
        foreach (SwarmDependency dependency in task.Dependencies.Where(dependency => dependency.ResultCommit is not null))
        {
            await repository.Materialize(cache, DependencyFolder(id, dependency.TaskId), dependency.BaseCommit, dependency.ResultCommit!, $"# {dependency.Title}\n\n{dependency.Report ?? "The task made no report."}", CancellationToken.None);
        }
        WorkspaceInfo created = await workspaces.Create(folder, AiService.Local, null, null, CancellationToken.None);
        delegated[created.Id] = (id, new WorkspaceDelegation { WorkspaceId = created.Id, From = task.PeerName, Title = task.Title });
        bool wanted = await book.Change(document =>
        {
            if (document.Incoming.FirstOrDefault(entry => entry.Id == id) is not { } entry || !Active(entry.State))
            {
                return false;
            }

            entry.WorkspaceId = created.Id;
            entry.WorkspaceName = created.Name;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });

        // The sender was revoked while the task was set up, so nothing else will remove what was made.
        if (!wanted)
        {
            await Remove(id, folder);
            delegated.TryRemove(created.Id, out _);
            return null;
        }

        await Restrict(id, created.Id);
        return created.Id;
    }

    private async Task<string?> Refusal(string peerId, AiCapability capability, SwarmMode mode)
    {
        (bool accepts, SwarmMode? set) = await book.Read(document =>
        {
            SwarmPeerRecord? peer = document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId);
            return (document.Server.AcceptDelegatedWork && peer?.AcceptWork == true, peer?.Mode);
        });

        if (!accepts)
        {
            return "This server does not accept delegated work from you.";
        }

        // Fetching from and pushing to a remote the sender names uses this server's own git credentials, so its owner has to have chosen that for the sender.
        if (mode == SwarmMode.GitRemote && set != SwarmMode.GitRemote)
        {
            return "This server only takes your code through a git remote once its delivery mode for you is Git remote.";
        }

        return Model(host.Get(), capability) is null ? $"This server has no local model for the {capability} capability." : null;
    }

    private void Check(SwarmTaskBody task)
    {
        if (task.Title.Length is 0 or > 500 || task.Instructions.Length is 0 or > 200_000 || task.ExpectedResult.Length > 20_000 || task.BaseCommit.Length > 64 || task.RemoteUrl is { Length: > 2000 } || !Enum.IsDefined(task.Capability) || !Enum.IsDefined(task.Mode)
            || task.Dependencies is not { Count: <= 100 } || task.Dependencies.Any(dependency => dependency is not { Title.Length: <= 500, BaseCommit.Length: <= 64, Report: null or { Length: <= 100_000 }, ResultCommit: null or { Length: <= 64 }, Includes.Count: <= 1000 } || dependency.Includes.Any(included => included is not { Title.Length: <= 500 })))
        {
            throw new InvalidDataException("The task is not valid.");
        }
    }

    // OpenCode runs plugins and MCP servers that a project configures, which in a delegated task are the sender's files. This turns that off for every OpenCode this server starts from now on, which Live AI passes the server's environment to.
    private void IgnoreProjectConfiguration() => Environment.SetEnvironmentVariable("OPENCODE_DISABLE_PROJECT_CONFIG", "1");

    // The permissions are set before the first prompt and nothing else can change them: the owner's settings commands are refused for the workspace.
    private async Task Restrict(Guid id, Guid workspaceId)
    {
        (SwarmPermissions permissions, AiCapability capability) = await book.Read(document =>
        {
            SwarmIncoming task = document.Incoming.First(task => task.Id == id);
            return (document.Peers.FirstOrDefault(peer => peer.Identity.Id == task.PeerId)?.Permissions ?? new SwarmPermissions(), task.Capability);
        });
        AiEnvironment environment = host.Get();
        await prompts.SetSettings(workspaceId, new WorkspaceSettings
        {
            Capability = capability,
            Effort = environment.NewWorkspaces.Effort,
            IsSandboxed = permissions.IsSandboxed,
            IsWebAllowed = permissions.IsWebAllowed,
            IsImageGenerationAllowed = permissions.IsImageGenerationAllowed && environment.ImageGeneration == ImageGenerationKind.Local,
            IsDelegationAllowed = false,
            Instructions = instructions
        }, CancellationToken.None);
    }

    private async Task Start(Guid id, Guid workspaceId)
    {
        SwarmIncoming? task = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id));

        if (task is null || !Active(task.State))
        {
            return;
        }

        if (task.CancelReason is { } cancelled)
        {
            await Finish(id, SwarmOutcome.Cancelled, null, cancelled);
            return;
        }

        if (Directory.Exists(DependencyRoot(id)))
        {
            prompts.Share(workspaceId, dependencyShare, [DependencyRoot(id)]);
        }

        string text = $"# {task.Title}\n\n{task.Instructions}{(task.ExpectedResult.Length > 0 ? $"\n\n## Expected result\n\n{task.ExpectedResult}" : string.Empty)}{BuildOn(task)}";

        try
        {
            WorkTicket ticket = await queue.Submit(workspaceId, text, WorkSource.Delegated, CancellationToken.None);

            if (!await ticket.Started)
            {
                string reason = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id)?.CancelReason) ?? "It was removed from the queue of the worker.";
                await Finish(id, SwarmOutcome.Cancelled, null, reason);
                return;
            }
        }
        catch (RequestException exception)
        {
            await Finish(id, SwarmOutcome.Failed, null, $"The worker could not start the task: {exception.Message}");
            return;
        }

        // Cancelled or revoked while it started: the work that just began is stopped.
        task = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id));

        if (task is null || !Active(task.State) || task.CancelReason is not null)
        {
            await prompts.Interrupt(workspaceId);

            if (task is { CancelReason: { } reason } && Active(task.State))
            {
                await Finish(id, SwarmOutcome.Cancelled, null, reason);
            }

            return;
        }

        await MarkRunning(id);

        if (!prompts.IsBusy(workspaceId) && queue.Get(workspaceId) is null)
        {
            await Complete(id);
        }
    }

    private string BuildOn(SwarmIncoming task)
    {
        if (task.Dependencies.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder text = new("\n\n## Results this task builds on\n\n");
        text.Append(task.Dependencies.HasChanges
            ? "Earlier tasks made the results below. Their changes are not in your files yet: first combine all of them into your folder, then do this task on top. Each folder is outside your workspace and read only: files holds all files as that task left them, changes.txt lists what it changed against the code it started from, changes.diff is that change as a diff and report.md is its report. Your files may differ from what a task started from, so apply its changes rather than copying whole files over, and decide conflicts yourself. Your report has to say anything of them you could not take over."
            : "Earlier tasks made the reports below; they changed no files.");

        foreach (SwarmDependency dependency in task.Dependencies)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n\n### \"{dependency.Title}\"");

            if (dependency.ResultCommit is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n\nFolder: {DependencyFolder(task.Id, dependency.TaskId)}");

                if (dependency.Includes.Count > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $"\nIts changes should already include those of: {string.Join(", ", dependency.Includes.Select(included => $"\"{included.Title}\""))}, so do not apply those twice.");
                }
            }

            if (!string.IsNullOrWhiteSpace(dependency.Report))
            {
                text.Append(CultureInfo.InvariantCulture, $"\n\nReport:\n{(dependency.Report.Length > buildingReportLimit ? $"{dependency.Report[..buildingReportLimit]}..." : dependency.Report)}");
            }
        }

        return text.ToString();
    }

    private Task MarkRunning(Guid id) => book.Change(document =>
    {
        if (document.Incoming.FirstOrDefault(entry => entry.Id == id) is { State: SwarmTaskState.Queued } task)
        {
            task.State = SwarmTaskState.Running;
            task.UpdatedAt = DateTimeOffset.UtcNow;
        }
    });

    private void Follow(RemoteEvent remoteEvent)
    {
        if (!delegated.TryGetValue(remoteEvent.WorkspaceId, out (Guid TaskId, WorkspaceDelegation Delegation) entry))
        {
            return;
        }

        if (remoteEvent.Kind == RemoteEventKind.Failed)
        {
            failures[entry.TaskId] = remoteEvent.Error ?? "The AI failed.";
        }
        else if (remoteEvent.Kind == RemoteEventKind.WorkingChanged)
        {
            _ = Task.Run(async () =>
            {
                if (remoteEvent.Working == true)
                {
                    await MarkRunning(entry.TaskId);
                }
                else if (await book.Read(document => document.Incoming.FirstOrDefault(task => task.Id == entry.TaskId)?.State) == SwarmTaskState.Running)
                {
                    await Complete(entry.TaskId);
                }
            });
        }
    }

    // Sending the changes back can take as long as a push does, which nothing else may wait for.
    private void CompleteApart(Guid id)
    {
        if (!completing.TryAdd(id, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Complete(id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The task stays active, and the next sweep tries again.
            }
            finally
            {
                completing.TryRemove(id, out _);
            }
        });
    }

    private async Task Complete(Guid id)
    {
        SwarmIncoming? task = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id));

        if (task is null || !Active(task.State) || task.WorkspaceId is not { } workspace || prompts.IsBusy(workspace) || queue.Get(workspace) is not null)
        {
            return;
        }

        if (task.CancelReason is { } cancelled)
        {
            await Finish(id, SwarmOutcome.Cancelled, null, cancelled);
            return;
        }

        string? report = (await Conversation(workspace)).LastOrDefault(message => message is { Role: MessageRole.Assistant, IsWork: false })?.Text;
        task = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id));
        string? unfinished = task?.Unfinished is { } why ? $"The AI could not finish it: {why}" : null;
        string? failure = failures.GetValueOrDefault(id) ?? unfinished;

        await Finish(id, failure is null ? SwarmOutcome.Done : SwarmOutcome.Failed, report, failure, unfinished is not null);
    }

    private async Task<IReadOnlyList<ChatMessage>> Conversation(Guid workspace)
    {
        // Messages are written in the background after their events, so the end of the conversation may take a moment.
        IReadOnlyList<ChatMessage> messages = await store.LoadMessages(workspace, CancellationToken.None) ?? [];

        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(50);
            IReadOnlyList<ChatMessage> next = await store.LoadMessages(workspace, CancellationToken.None) ?? [];

            if (next.Count == messages.Count && next.Count > 1)
            {
                return next;
            }

            messages = next;
        }

        return messages;
    }

    private Task Finish(Guid id, SwarmOutcome outcome, string? report, string? error) => Finish(id, outcome, report, error, false);

    // Work the AI could not finish sends back what it changed too.
    private async Task Finish(Guid id, SwarmOutcome outcome, string? report, string? error, bool keepsChanges)
    {
        SemaphoreSlim gate = finishing.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            SwarmIncoming? task = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id));

            if (task is null || !Active(task.State))
            {
                return;
            }

            string? commit = null;
            ReadOnlyMemory<byte> bundle = ReadOnlyMemory<byte>.Empty;
            IReadOnlyList<ChatMessage> messages = task.WorkspaceId is { } at ? await store.LoadMessages(at, CancellationToken.None) ?? [] : [];

            if ((outcome == SwarmOutcome.Done || keepsChanges) && task.Folder is { } folder)
            {
                await book.Change(document => document.Incoming.First(entry => entry.Id == id).IsWorkDone = true, false);

                try
                {
                    commit = await repository.Collect(Cache(task), folder, id, task.BaseCommit, $"Delegated task: {task.Title}", CancellationToken.None);

                    if (commit is not null && task.Mode == SwarmMode.GitRemote)
                    {
                        await repository.PushResult(Cache(task), task.RemoteUrl!, id, $"swarm/results/{id:N}", CancellationToken.None);
                    }
                    else if (commit is not null)
                    {
                        bundle = await repository.BundleResult(Cache(task), id, task.BaseCommit, CancellationToken.None);
                    }
                }
                catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
                {
                    // The task stays active meanwhile, and the sweep tries again.
                    (DateTimeOffset since, int attempts) = failing.AddOrUpdate(id, _ => (DateTimeOffset.UtcNow, 1), (_, before) => (before.Since, before.Attempts + 1));

                    if (exception is not RequestException { IsTemporary: false } && (attempts < leastAttempts || DateTimeOffset.UtcNow - since < sendPatience))
                    {
                        return;
                    }

                    outcome = SwarmOutcome.Failed;
                    error = keepsChanges && error is not null ? $"{error} The changes could not be sent back: {exception.Message}" : $"The changes could not be sent back: {exception.Message}";
                    commit = null;
                    bundle = ReadOnlyMemory<byte>.Empty;
                }
            }

            // The sender or the owner may have cancelled while the changes were collected and pushed.
            if (outcome != SwarmOutcome.Cancelled && await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id)?.CancelReason) is { } cancelledMeanwhile)
            {
                (outcome, report, error, commit, bundle) = (SwarmOutcome.Cancelled, null, cancelledMeanwhile, null, ReadOnlyMemory<byte>.Empty);
            }

            SwarmResultBody result = new() { TaskId = id, Outcome = outcome, Report = report, Error = error, ResultCommit = commit, Messages = [.. messages.TakeLast(messageLimit)] };
            Guid? message = null;

            try
            {
                message = await courier.Post(task.PeerId, SwarmMessageKind.Result, result, bundle);
            }
            catch (RequestException)
            {
                // The sender is no longer paired.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The task stays active, and the sweep tries to answer it again.
                return;
            }

            await book.Change(document =>
            {
                SwarmIncoming entry = document.Incoming.First(entry => entry.Id == id);
                entry.State = outcome switch { SwarmOutcome.Done => SwarmTaskState.Done, SwarmOutcome.Cancelled => SwarmTaskState.Cancelled, _ => SwarmTaskState.Failed };
                entry.Report = report;
                entry.Error = error;
                entry.ResultMessageId = message;
                entry.IsCleaned = entry.WorkspaceId is null && entry.Folder is null;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
            });
            DeleteBundle(id);
            failures.TryRemove(id, out _);
            failing.TryRemove(id, out _);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Clean(Guid id)
    {
        SwarmIncoming? task = await book.Read(document => document.Incoming.FirstOrDefault(entry => entry.Id == id));

        if (task is null)
        {
            return;
        }

        if (task.WorkspaceId is { } workspaceId)
        {
            try
            {
                WorkspaceInfo workspace = await workspaces.Get(workspaceId, CancellationToken.None);
                await queue.Cancel(workspaceId);
                await workspaces.Delete(workspaceId, false, CancellationToken.None);
                await prompts.Forget(workspace, true);
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
            {
            }

            delegated.TryRemove(workspaceId, out _);
        }

        // Only folders this worker made for a task are deleted.
        if (task.Folder is { } folder && Path.GetFileName(folder) == $"task-{id:N}" && Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (task.RepoKey.Length > 0)
        {
            await repository.Forget(Cache(task), id, CancellationToken.None);
        }

        if (task.WorkspaceId is { } shared)
        {
            prompts.Unshare(shared, dependencyShare);
        }

        DeleteDependencies(id);
        DeleteBundle(id);
        await book.Change(document =>
        {
            if (document.Incoming.FirstOrDefault(entry => entry.Id == id) is { } entry)
            {
                entry.IsCleaned = true;
                entry.WorkspaceId = null;
            }
        });
    }

    private async Task Discard(Guid id)
    {
        try
        {
            await Remove(id, Path.Combine(await Root(), $"task-{id:N}"));
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
        }
    }

    // Only folders this worker made for a task are deleted, with any workspace made in them.
    private async Task Remove(Guid id, string folder)
    {
        if (Path.GetFileName(folder) != $"task-{id:N}")
        {
            return;
        }

        foreach (WorkspaceInfo workspace in (await workspaces.List(CancellationToken.None)).Workspaces.Where(workspace => workspace.Folder == folder).ToList())
        {
            await queue.Cancel(workspace.Id);
            await workspaces.Delete(workspace.Id, false, CancellationToken.None);
            await prompts.Forget(workspace, true);
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }

        DeleteDependencies(id);
    }

    private void DeleteDependencies(Guid id)
    {
        try
        {
            if (Directory.Exists(DependencyRoot(id)))
            {
                Directory.Delete(DependencyRoot(id), true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A task that travelled through a git remote never had a bundle, nor the folder for one.
    private void DeleteBundle(Guid id)
    {
        if (File.Exists(BundlePath(id)))
        {
            File.Delete(BundlePath(id));
        }
    }

    private async Task<string> Root()
    {
        string configured = await book.Read(document => document.Server.DelegatedFolder);
        string root = configured.Length > 0 ? configured : Path.Combine(book.Folder, "work");
        Directory.CreateDirectory(root);
        return root;
    }

    private string Cache(SwarmIncoming task) => Path.Combine(book.Folder, "repos", $"{task.PeerId}-{task.RepoKey}.git");

    private string BundlePath(Guid id) => Path.Combine(book.Folder, "incoming", $"{id:N}.bundle");

    private string DependencyRoot(Guid id) => Path.Combine(book.Folder, "dependencies", $"{id:N}");

    private string DependencyFolder(Guid id, Guid dependencyId) => Path.Combine(DependencyRoot(id), $"{dependencyId:N}");

    private string SafeKey(string key) => key.Length is > 0 and <= 64 && key.All(char.IsAsciiHexDigit) ? key : "repository";

    private string MissingHistoryMessage() => "The worker lacks commits the code builds on.";

    private bool Active(SwarmTaskState state) => state is SwarmTaskState.Queued or SwarmTaskState.Running;

    private string? Model(AiEnvironment environment, AiCapability capability)
    {
        string model = capability switch
        {
            AiCapability.Fast => environment.LocalModels.Fast,
            AiCapability.Balanced => environment.LocalModels.Balanced,
            AiCapability.Strong => environment.LocalModels.Strong,
            _ => environment.LocalModels.Max
        };

        return string.IsNullOrWhiteSpace(model) ? null : model.Trim();
    }

    private int? Context(AiEnvironment environment, AiCapability capability) => capability switch
    {
        AiCapability.Fast => environment.LocalContexts?.Fast,
        AiCapability.Balanced => environment.LocalContexts?.Balanced,
        AiCapability.Strong => environment.LocalContexts?.Strong,
        _ => environment.LocalContexts?.Max
    };
}
