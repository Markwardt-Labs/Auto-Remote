namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The swarm as the rest of the server uses it: its settings and paired servers, delegating and following tasks, and the state of the workspaces that run delegated tasks.
/// </summary>
internal interface ISwarmService
{
    /// <summary>
    /// Gets the workspaces that run delegated tasks.
    /// </summary>
    IReadOnlyList<WorkspaceDelegation> Delegations { get; }

    /// <summary>
    /// Loads the swarm, picks up its unfinished work and starts connecting to the paired servers.
    /// </summary>
    /// <param name="config">The server configuration.</param>
    /// <param name="cancellation">Ends the connections when the server stops.</param>
    /// <returns>A task that completes once started.</returns>
    Task Open(ServerConfig config, CancellationToken cancellation);

    /// <summary>
    /// Reads everything the swarm screen shows.
    /// </summary>
    /// <param name="report">What the last action did, to carry along.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The status.</returns>
    Task<SwarmStatus> Status(string? report, CancellationToken cancellation);

    /// <summary>
    /// Changes the settings of this server.
    /// </summary>
    /// <param name="settings">The settings, whose read only values are ignored.</param>
    /// <returns>A task that completes once changed.</returns>
    /// <exception cref="RequestException">A value is not usable.</exception>
    Task SetServer(SwarmServer settings);

    /// <summary>
    /// Changes the settings of this server for a paired one.
    /// </summary>
    /// <param name="settings">The settings, whose read only values are ignored.</param>
    /// <returns>A task that completes once changed.</returns>
    /// <exception cref="RequestException">A value is not usable or the server is not paired.</exception>
    Task SetPeer(SwarmPeer settings);

    /// <summary>
    /// Forgets a paired server and ends the tasks between the two.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <returns>A task that completes once forgotten.</returns>
    /// <exception cref="RequestException">It is not paired.</exception>
    Task Revoke(string peerId);

    /// <summary>
    /// Makes a pairing code.
    /// </summary>
    void CreateCode();

    /// <summary>
    /// Pairs with the server that made a code.
    /// </summary>
    /// <param name="address">Its address.</param>
    /// <param name="code">The code.</param>
    /// <param name="cancellation">Cancels the pairing.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="RequestException">It cannot be reached or refused the code.</exception>
    Task<string> PairWithCode(string address, string code, CancellationToken cancellation);

    /// <summary>
    /// Writes a pairing invitation to the drive.
    /// </summary>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="RequestException">The drive cannot be written.</exception>
    Task<string> WriteInvitation(CancellationToken cancellation);

    /// <summary>
    /// Pairs with the server of a pairing on the drive.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once paired.</returns>
    /// <exception cref="RequestException">No such pairing waits.</exception>
    Task ConfirmDrivePairing(string peerId, CancellationToken cancellation);

    /// <summary>
    /// Reads from and writes to the drive.
    /// </summary>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="RequestException">The drive cannot be used.</exception>
    Task<string> SyncDrive(CancellationToken cancellation);

    /// <summary>
    /// Delegates one task by hand.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="task">The task.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once delegated.</returns>
    /// <exception cref="RequestException">It cannot be delegated.</exception>
    Task Delegate(Guid workspaceId, SwarmTaskRequest task, CancellationToken cancellation);

    /// <summary>
    /// Reads everything about a task.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The task.</returns>
    /// <exception cref="RequestException">There is no such task.</exception>
    Task<SwarmTaskDetail> Detail(Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Cancels an outgoing or incoming task.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <returns>A task that completes once cancelled.</returns>
    /// <exception cref="RequestException">It cannot be cancelled.</exception>
    Task Cancel(Guid taskId);

    /// <summary>
    /// Delegates an ended outgoing task again.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once delegated.</returns>
    /// <exception cref="RequestException">It cannot be delegated again.</exception>
    Task Retry(Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Applies the changes of a finished task to its workspace, which must be ready.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="RequestException">It cannot be applied now.</exception>
    Task<string> Apply(Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Removes an ended task.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once removed.</returns>
    /// <exception cref="RequestException">It cannot be removed.</exception>
    Task Discard(Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Checks whether a workspace runs a delegated task.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns><see langword="true" /> if it does.</returns>
    bool IsDelegated(Guid workspaceId);

    /// <summary>
    /// Cancels the task a delegated workspace runs, as the owner stopping it does.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>A task that completes once underway.</returns>
    Task CancelByOwner(Guid workspaceId);

    /// <summary>
    /// Deals with a workspace being deleted: a delegated one's task is cancelled, and the tasks delegated from any other are cancelled and removed.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>A task that completes once done.</returns>
    Task Forget(Guid workspaceId);
}

/// <inheritdoc />
internal sealed class SwarmService(ISwarmBook book, ISwarmCourier courier, ISwarmWorker worker, ISwarmCoordinator coordinator, ISwarmDrive drive, ISwarmLink link, ISwarmDialer dialer, IWorkspaceManager workspaces, IWorkspaceActivity activity, IWorkspaceStore store, ILog log) : ISwarmService
{
    private readonly TimeSpan check = TimeSpan.FromSeconds(15);

    private readonly TimeSpan silence = TimeSpan.FromMinutes(3);

    private readonly int summaryLength = 400;

    private bool open;

    /// <inheritdoc />
    public IReadOnlyList<WorkspaceDelegation> Delegations => open ? worker.Delegations : [];

    /// <inheritdoc />
    public async Task Open(ServerConfig config, CancellationToken cancellation)
    {
        await book.Open(config.DataFolder, cancellation);
        open = true;
        await worker.Open(cancellation);
        await coordinator.Open(cancellation);
        _ = link.Run(cancellation);
        _ = Check(cancellation);
    }

    /// <inheritdoc />
    public async Task<SwarmStatus> Status(string? report, CancellationToken cancellation)
    {
        Dictionary<Guid, string> names = (await workspaces.List(cancellation)).Workspaces.ToDictionary(workspace => workspace.Id, workspace => workspace.Name);
        (SwarmServer server, List<SwarmPeer> peers, List<SwarmTaskInfo> tasks, string folder) = await book.Read(document => (
            document.Server with { Fingerprint = book.Identity.Public.Fingerprint },
            document.Peers.OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).Select(Describe).ToList(),
            document.Outgoing.Select(task => Describe(task, names)).Concat(document.Incoming.Where(task => task.Instructions.Length > 0 || !task.IsCleaned).Select(task => Describe(task, names))).OrderByDescending(task => task.CreatedAt).ToList(),
            document.Server.DriveFolder));
        return new SwarmStatus { Server = server, Peers = [.. peers.Select(peer => peer with { IsConnected = link.IsConnected(peer.Id) })], DrivePairings = await drive.Pairings(folder), PairingCode = link.Code, Tasks = tasks, Report = report };
    }

    /// <inheritdoc />
    public Task SetServer(SwarmServer settings)
    {
        string name = (settings.Name ?? string.Empty).Trim();
        string delegated = (settings.DelegatedFolder ?? string.Empty).Trim();
        string driveFolder = (settings.DriveFolder ?? string.Empty).Trim();

        if (name.Length is 0 or > 100)
        {
            throw new RequestException("The server needs a name of at most 100 characters.");
        }

        if (settings.MaxTasksPerJob is < 1 or > 100)
        {
            throw new RequestException("A job may have from 1 to 100 tasks.");
        }

        if (delegated.Length > 0 && !Path.IsPathFullyQualified(delegated) || driveFolder.Length > 0 && !Path.IsPathFullyQualified(driveFolder))
        {
            throw new RequestException("The folders must be full paths on the machine of the server.");
        }

        return book.Change(document => document.Server = new SwarmServer { Name = name, AcceptDelegatedWork = settings.AcceptDelegatedWork, DelegatedFolder = delegated, MaxTasksPerJob = settings.MaxTasksPerJob, DriveFolder = driveFolder });
    }

    /// <inheritdoc />
    public async Task SetPeer(SwarmPeer settings)
    {
        string address = (settings.Address ?? string.Empty).Trim();

        if (!Enum.IsDefined(settings.Mode))
        {
            throw new RequestException("The delivery mode is not one the server knows.");
        }

        if (address.Length > 0)
        {
            dialer.Parse(address);
        }

        bool known = await book.Change(document =>
        {
            if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == settings.Id) is not { } peer)
            {
                return false;
            }

            peer.SendWork = settings.SendWork;
            peer.AcceptWork = settings.AcceptWork;
            peer.Mode = settings.Mode;
            peer.Address = address;
            peer.Permissions = settings.Permissions ?? new SwarmPermissions();
            peer.Problem = null;
            return true;
        });

        if (!known)
        {
            throw new RequestException("That server is not paired.");
        }

        link.Disconnect(settings.Id);
        await coordinator.DispatchSoon();
    }

    /// <inheritdoc />
    public async Task Revoke(string peerId)
    {
        if (!await book.Read(document => document.Peers.Any(peer => peer.Identity.Id == peerId)))
        {
            throw new RequestException("That server is not paired.");
        }

        // Nothing more is taken from it or sent to it while its tasks end.
        await book.Change(document =>
        {
            if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId) is { } peer)
            {
                peer.AcceptWork = false;
                peer.SendWork = false;
                peer.Mode = SwarmMode.AirGapped;
                peer.Address = string.Empty;
            }
        });
        link.Disconnect(peerId);
        await worker.Revoke(peerId);
        await coordinator.Revoke(peerId);
        courier.Remove(peerId);
        await book.Change(document => document.Peers.RemoveAll(peer => peer.Identity.Id == peerId));
        link.Disconnect(peerId);
    }

    /// <inheritdoc />
    public void CreateCode()
    {
        link.CreateCode();
        book.Announce();
    }

    /// <inheritdoc />
    public async Task<string> PairWithCode(string address, string code, CancellationToken cancellation)
    {
        string name = await link.Pair(address, code, cancellation);
        return $"Paired with {name}. Turn on what the two may do with each other in its settings.";
    }

    /// <inheritdoc />
    public async Task<string> WriteInvitation(CancellationToken cancellation)
    {
        await drive.Invite(await DriveFolder(), cancellation);
        return "Wrote an invitation to the drive. Take it to the other server, confirm it there, then bring the drive back and confirm its answer here.";
    }

    /// <inheritdoc />
    public async Task ConfirmDrivePairing(string peerId, CancellationToken cancellation)
    {
        await drive.Confirm(await DriveFolder(), peerId, cancellation);
        book.Announce();
    }

    /// <inheritdoc />
    public async Task<string> SyncDrive(CancellationToken cancellation)
    {
        string report = await drive.Sync(await DriveFolder(), cancellation);
        book.Announce();
        return report;
    }

    /// <inheritdoc />
    public Task Delegate(Guid workspaceId, SwarmTaskRequest task, CancellationToken cancellation)
    {
        if (worker.IsDelegated(workspaceId))
        {
            throw new RequestException("A delegated workspace cannot delegate further.");
        }

        return coordinator.Delegate(workspaceId, [new SwarmPlannedTask(task, [])], true, cancellation);
    }

    /// <inheritdoc />
    public async Task<SwarmTaskDetail> Detail(Guid taskId, CancellationToken cancellation)
    {
        Dictionary<Guid, string> names = (await workspaces.List(cancellation)).Workspaces.ToDictionary(workspace => workspace.Id, workspace => workspace.Name);
        (SwarmTaskDetail? detail, Guid? conversation) = await book.Read<(SwarmTaskDetail?, Guid?)>(document =>
        {
            if (document.Outgoing.FirstOrDefault(task => task.Id == taskId) is { } outgoing)
            {
                return (new SwarmTaskDetail { Task = Describe(outgoing, names), Instructions = outgoing.Instructions, ExpectedResult = outgoing.ExpectedResult, Report = outgoing.Report }, null);
            }

            return document.Incoming.FirstOrDefault(task => task.Id == taskId) is { } incoming
                ? (new SwarmTaskDetail { Task = Describe(incoming, names), Instructions = incoming.Instructions, ExpectedResult = incoming.ExpectedResult, Report = incoming.Report }, incoming.WorkspaceId)
                : (null, null);
        });

        if (detail is null)
        {
            throw new RequestException("There is no such delegated task.");
        }

        IReadOnlyList<ChatMessage> messages = detail.Task.IsIncoming ? conversation is { } workspace ? await store.LoadMessages(workspace, cancellation) ?? [] : [] : await book.LoadChat(taskId);
        return detail with { Messages = Window(messages) };
    }

    /// <inheritdoc />
    public async Task Cancel(Guid taskId)
    {
        if (await book.Read(document => document.Incoming.FirstOrDefault(task => task.Id == taskId)?.PeerId) is { } peer)
        {
            await worker.Cancel(peer, taskId, "The owner of the worker cancelled it.");
            return;
        }

        await coordinator.Cancel(taskId);
    }

    /// <inheritdoc />
    public Task Retry(Guid taskId, CancellationToken cancellation) => coordinator.Retry(taskId, cancellation);

    /// <inheritdoc />
    public async Task<string> Apply(Guid taskId, CancellationToken cancellation)
    {
        Guid? workspace = await book.Read(document => document.Outgoing.FirstOrDefault(task => task.Id == taskId)?.WorkspaceId);

        if (workspace is { } id && activity.Get(id) != WorkspaceState.Ready)
        {
            throw new RequestException(activity.Describe(id));
        }

        SwarmApplyResult applied = await coordinator.Apply(taskId, cancellation);
        return applied.Conflicts.Count > 0
            ? $"Applied the changes with conflicts to resolve in {string.Join(", ", applied.Conflicts)}."
            : $"Applied the changes to {applied.Files} file{(applied.Files == 1 ? string.Empty : "s")}. They are not committed.";
    }

    /// <inheritdoc />
    public async Task Discard(Guid taskId, CancellationToken cancellation)
    {
        if (await book.Read(document => document.Incoming.FirstOrDefault(task => task.Id == taskId)) is { } incoming)
        {
            if (incoming.State is SwarmTaskState.Queued or SwarmTaskState.Running || !incoming.IsCleaned)
            {
                throw new RequestException("The task is kept until its sender has the result.");
            }

            // The record stays, without its text, so the same task arriving again is still recognized.
            await book.Change(document =>
            {
                if (document.Incoming.FirstOrDefault(task => task.Id == taskId) is { } task)
                {
                    task.Instructions = string.Empty;
                    task.Report = null;
                }
            });
            return;
        }

        await coordinator.Discard(taskId, cancellation);
    }

    /// <inheritdoc />
    public bool IsDelegated(Guid workspaceId) => open && worker.IsDelegated(workspaceId);

    /// <inheritdoc />
    public Task CancelByOwner(Guid workspaceId) => worker.CancelByOwner(workspaceId);

    /// <inheritdoc />
    public async Task Forget(Guid workspaceId)
    {
        if (!open)
        {
            return;
        }

        if (worker.IsDelegated(workspaceId))
        {
            await worker.CancelByOwner(workspaceId);
        }
        else
        {
            await coordinator.Forget(workspaceId);
        }
    }

    private async Task Check(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(check, cancellation);
                await worker.Sweep();
                await coordinator.CheckLost(silence);
                await coordinator.DispatchSoon();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Whatever went wrong, the checks go on.
                log.Write($"Checking the swarm failed: {exception.Message}");
            }
        }
    }

    private async Task<string> DriveFolder() => await book.Read(document => document.Server.DriveFolder);

    // Only as much of a conversation as fits in one answer is sent, the newest of it.
    private IReadOnlyList<ChatMessage> Window(IReadOnlyList<ChatMessage> messages)
    {
        int characters = 0;
        int start = messages.Count;

        while (start > 0 && messages.Count - start < 200 && (start == messages.Count || characters + messages[start - 1].Text.Length <= 60_000))
        {
            characters += messages[--start].Text.Length;
        }

        return [.. messages.Skip(start)];
    }

    private SwarmPeer Describe(SwarmPeerRecord peer) => new()
    {
        Id = peer.Identity.Id,
        Name = peer.Name,
        Fingerprint = peer.Identity.Fingerprint,
        SendWork = peer.SendWork,
        AcceptWork = peer.AcceptWork,
        Mode = peer.Mode,
        Address = peer.Address,
        Permissions = peer.Permissions,
        Capabilities = peer.Profile?.Capabilities ?? [],
        AcceptsOurWork = peer.Profile?.AcceptsWork == true,
        FreeSlots = peer.Profile?.FreeSlots ?? 0,
        ProfileAt = peer.ProfileAt,
        LastSeen = peer.LastSeen,
        Problem = peer.Problem
    };

    private SwarmTaskInfo Describe(SwarmOutgoing task, Dictionary<Guid, string> names) => new()
    {
        Id = task.Id,
        JobId = task.JobId,
        Title = task.Title,
        PeerName = task.PeerName,
        WorkspaceId = task.WorkspaceId,
        WorkspaceName = names.GetValueOrDefault(task.WorkspaceId, string.Empty),
        Capability = task.Capability,
        State = task.State,
        Error = task.Error,
        Summary = Summary(task.Report),
        Branch = task.Branch,
        IsApplied = task.IsApplied,
        IsManual = task.IsManual,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt
    };

    private SwarmTaskInfo Describe(SwarmIncoming task, Dictionary<Guid, string> names) => new()
    {
        Id = task.Id,
        JobId = task.JobId,
        IsIncoming = true,
        Title = task.Title,
        PeerName = task.PeerName,
        WorkspaceId = task.WorkspaceId,
        WorkspaceName = task.WorkspaceId is { } id ? names.GetValueOrDefault(id, task.WorkspaceName ?? string.Empty) : task.WorkspaceName ?? string.Empty,
        Capability = task.Capability,
        State = task.State,
        Error = task.Error,
        Summary = Summary(task.Report),
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt
    };

    private string? Summary(string? report) => report is null ? null : report.Length > summaryLength ? $"{report[..summaryLength]}..." : report;
}
