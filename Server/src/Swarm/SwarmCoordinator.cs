namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The tasks this server delegates: it snapshots the workspace, gives each task to a worker when the tasks it builds on are done and a worker is free (an air gapped task to the worker it names), and takes the results back as branches of the workspace.
/// </summary>
internal interface ISwarmCoordinator
{
    /// <summary>
    /// Picks up the tasks that had not ended when the server stopped.
    /// </summary>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the tasks are picked up.</returns>
    Task Open(CancellationToken cancellation);

    /// <summary>
    /// Delegates tasks from a workspace as one job.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="tasks">The tasks.</param>
    /// <param name="manual">Whether the user delegates them, so no AI is told that they ended.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The identifiers of the tasks, in order.</returns>
    /// <exception cref="RequestException">A task cannot be delegated.</exception>
    Task<IReadOnlyList<Guid>> Delegate(Guid workspaceId, IReadOnlyList<SwarmPlannedTask> tasks, bool manual, CancellationToken cancellation);

    /// <summary>
    /// Gives waiting tasks to workers as far as it can.
    /// </summary>
    /// <returns>A task that completes once done.</returns>
    Task Dispatch();

    /// <summary>
    /// Notes that a worker acknowledged a task package.
    /// </summary>
    /// <param name="peerId">The worker.</param>
    /// <param name="messageId">The package.</param>
    /// <returns>A task that completes once noted.</returns>
    Task Delivered(string peerId, Guid messageId);

    /// <summary>
    /// Takes the progress of tasks a worker told.
    /// </summary>
    /// <param name="peerId">The worker.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task that completes once taken.</returns>
    Task Progress(string peerId, SwarmProgressBody progress);

    /// <summary>
    /// Takes a result a worker sent.
    /// </summary>
    /// <param name="peerId">The worker.</param>
    /// <param name="result">The result.</param>
    /// <param name="bundle">The changes, when they travel as a bundle.</param>
    /// <returns>A task that completes once taken.</returns>
    Task Receive(string peerId, SwarmResultBody result, ReadOnlyMemory<byte> bundle);

    /// <summary>
    /// Cancels an outgoing task and tells its worker.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <returns>A task that completes once cancelled.</returns>
    /// <exception cref="RequestException">No such task can be cancelled.</exception>
    Task Cancel(Guid taskId);

    /// <summary>
    /// Delegates a failed, cancelled or lost task again, from the same instructions and code.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The identifier of the new task.</returns>
    /// <exception cref="RequestException">No such task can be delegated again.</exception>
    Task<Guid> Retry(Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Removes an ended task with its result branch.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once removed.</returns>
    /// <exception cref="RequestException">No such ended task exists.</exception>
    Task Discard(Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Removes a task whose result was taken over, with its result branch and, in the git remote mode, its branches on the remote. Tasks still waiting to build on it keep its result.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <returns>A task that completes once removed.</returns>
    Task Complete(Guid taskId);

    /// <summary>
    /// Describes the changes of a finished task.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="path">The only file wanted, or <see langword="null" />.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The description.</returns>
    /// <exception cref="RequestException">The task has no result.</exception>
    Task<string> Changes(Guid taskId, string? path, CancellationToken cancellation);

    /// <summary>
    /// Dispatches like <see cref="Dispatch" /> but returns after a moment when that takes long, as a push over a slow link does; it then goes on by itself.
    /// </summary>
    /// <returns>A task that completes once dispatched or the moment is over.</returns>
    Task DispatchSoon();

    /// <summary>
    /// Marks the networked tasks of workers not heard from for a while as lost.
    /// </summary>
    /// <param name="silent">How long a worker may be silent.</param>
    /// <returns>A task that completes once done.</returns>
    Task CheckLost(TimeSpan silent);

    /// <summary>
    /// Ends the tasks given to a server that is no longer paired.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <returns>A task that completes once done.</returns>
    Task Revoke(string peerId);

    /// <summary>
    /// Checks whether the AI of a workspace delegated tasks that are still under way, whose results its work goes on with. Once they ended, telling it and bringing the results in is work of the workspace that waits for its turn.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns><see langword="true" /> if it has.</returns>
    Task<bool> IsAwaited(Guid workspaceId);

    /// <summary>
    /// Tells whether tasks a workspace delegated are still under way.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>Whether any is waiting, on its way or running, its AI has yet to be told how they ended, or a result is still to be brought in.</returns>
    Task<bool> IsUnderWay(Guid workspaceId);

    /// <summary>
    /// Cancels and removes the tasks of a workspace that is deleted.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>A task that completes once done.</returns>
    Task Forget(Guid workspaceId);
}

/// <inheritdoc />
internal sealed class SwarmCoordinator(ISwarmBook book, ISwarmCourier courier, ISwarmRepository repository, IWorkspaceManager workspaces, ILog? log = null) : ISwarmCoordinator
{
    private readonly SemaphoreSlim dispatching = new(1, 1);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Since, int Attempts)> failing = new();

    private readonly int leastAttempts = 3;

    private readonly TimeSpan takePatience = TimeSpan.FromMinutes(30);

    private readonly TimeSpan freshness = TimeSpan.FromSeconds(90);

    private readonly TimeSpan checkGap = TimeSpan.FromMinutes(1);

    private readonly TimeSpan dispatchWait = TimeSpan.FromSeconds(2);

    private readonly TimeSpan tidyPatience = TimeSpan.FromSeconds(20);

    private DateTimeOffset listeningSince = DateTimeOffset.UtcNow;

    private DateTimeOffset checkedAt = DateTimeOffset.UtcNow;

    private readonly int chatLimit = 2000;

    private readonly int reportLimit = 100_000;

    private readonly int dependencyReportLimit = 20_000;

    private readonly int errorLimit = 4000;

    /// <inheritdoc />
    public Task Open(CancellationToken cancellation)
    {
        listeningSince = checkedAt = DateTimeOffset.UtcNow;
        _ = Schedule();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> Delegate(Guid workspaceId, IReadOnlyList<SwarmPlannedTask> tasks, bool manual, CancellationToken cancellation)
    {
        WorkspaceInfo workspace = await workspaces.Get(workspaceId, cancellation);

        if (tasks.Count == 0)
        {
            throw new RequestException("There is no task to delegate.");
        }

        (int most, List<SwarmPeerRecord> peers, HashSet<Guid> existing) = await book.Read(document => (document.Server.MaxTasksPerJob, document.Peers.ToList(), document.Outgoing.Where(task => task.WorkspaceId == workspaceId).Select(task => task.Id).ToHashSet()));

        if (tasks.Count > most)
        {
            throw new RequestException($"A job may have at most {most} tasks.");
        }

        for (int index = 0; index < tasks.Count; index++)
        {
            SwarmTaskRequest request = tasks[index].Request;

            if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Instructions))
            {
                throw new RequestException("Every task needs a title and instructions.");
            }

            if (!Enum.IsDefined(request.Capability))
            {
                throw new RequestException("The capability of a task is not one the AI knows.");
            }

            if (tasks[index].After.Any(earlier => earlier < 0 || earlier >= index) || request.DependsOn.Any(id => !existing.Contains(id)))
            {
                throw new RequestException($"The task '{request.Title}' may only depend on earlier tasks of this workspace.");
            }

            if (request.PeerId is { } peerId)
            {
                SwarmPeerRecord peer = peers.FirstOrDefault(peer => peer.Identity.Id == peerId) ?? throw new RequestException("The worker of a task is not paired.");

                if (!peer.SendWork)
                {
                    throw new RequestException($"Work is not sent to {peer.Name}; turn that on in the swarm settings first.");
                }

                if (peer.Profile is { Capabilities.Count: > 0 } profile && profile.Capabilities.All(capability => capability.Capability != request.Capability))
                {
                    throw new RequestException($"{peer.Name} has no model for the {request.Capability} capability.");
                }
            }
            else if (!peers.Any(peer => peer.SendWork && peer.Mode != SwarmMode.AirGapped))
            {
                throw new RequestException(peers.Any(peer => peer.SendWork) ? "Every task for an air gapped worker has to name it." : "No paired server is set to receive work; set one up in the swarm settings.");
            }
        }

        Guid jobId = Guid.NewGuid();
        List<SwarmOutgoing> made = [];
        string? snapshot = null;
        string? key = null;

        for (int index = 0; index < tasks.Count; index++)
        {
            SwarmTaskRequest request = tasks[index].Request;
            Guid id = Guid.NewGuid();

            if (snapshot is null)
            {
                (snapshot, key) = await repository.Snapshot(workspace.Folder, id, cancellation);
            }
            else
            {
                await repository.Pin(workspace.Folder, id, snapshot, cancellation);
            }

            List<Guid> dependencies = [.. request.DependsOn, .. tasks[index].After.Select(earlier => made[earlier].Id)];
            made.Add(new SwarmOutgoing
            {
                Id = id,
                JobId = jobId,
                WorkspaceId = workspaceId,
                Title = request.Title.Trim(),
                Instructions = request.Instructions.Trim(),
                ExpectedResult = request.ExpectedResult?.Trim() ?? string.Empty,
                Capability = request.Capability,
                RequestedPeerId = request.PeerId,
                PeerName = peers.FirstOrDefault(peer => peer.Identity.Id == request.PeerId)?.Name ?? string.Empty,
                DependsOn = [.. dependencies.Distinct()],
                State = dependencies.Count > 0 ? SwarmTaskState.Waiting : SwarmTaskState.Pending,
                RepoKey = key!,
                SnapshotCommit = snapshot,
                BaseCommit = snapshot,
                IsManual = manual,
                BringIn = request.BringIn,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await book.Change(document =>
        {
            document.Jobs.Add(new SwarmJob { Id = jobId, WorkspaceId = workspaceId, IsManual = manual, CreatedAt = DateTimeOffset.UtcNow });
            document.Outgoing.AddRange(made);
        });
        await DispatchSoon();
        return [.. made.Select(task => task.Id)];
    }

    /// <inheritdoc />
    public async Task Dispatch()
    {
        await dispatching.WaitAsync();

        try
        {
            await Unblock();

            foreach (Guid id in await book.Read(document => document.Outgoing.Where(task => task.State == SwarmTaskState.Pending).OrderBy(task => task.CreatedAt).Select(task => task.Id).ToList()))
            {
                if (await Choose(id) is { } peer)
                {
                    await Send(id, peer);
                }
            }
        }
        finally
        {
            dispatching.Release();
        }

        // Nobody is told about the jobs the user delegated by hand.
        if (await book.Read(document => document.Jobs.Any(job => job is { IsManual: true, IsReported: false } && document.IsOver(job.Id))))
        {
            await book.Change(document => document.Jobs.Where(job => job is { IsManual: true, IsReported: false } && document.IsOver(job.Id)).ToList().ForEach(job => job.IsReported = true), false);
        }
    }

    /// <inheritdoc />
    public Task DispatchSoon() => Task.WhenAny(Schedule(), Task.Delay(dispatchWait));

    /// <inheritdoc />
    public async Task Delivered(string peerId, Guid messageId)
    {
        if (await book.Read(document => document.Outgoing.Any(task => task.MessageId == messageId && task.PeerId == peerId && task.State is SwarmTaskState.InTransit or SwarmTaskState.Lost)))
        {
            await Update(task => task.MessageId == messageId, task => task.State = SwarmTaskState.Queued);
        }
    }

    /// <inheritdoc />
    public async Task Progress(string peerId, SwarmProgressBody progress)
    {
        foreach (SwarmTaskProgress told in progress.Tasks.Where(told => told.State is SwarmTaskState.Queued or SwarmTaskState.Running))
        {
            SwarmTaskState? before = await book.Read(document => document.Outgoing.FirstOrDefault(task => task.Id == told.TaskId && task.PeerId == peerId && task.State is SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running or SwarmTaskState.Lost)?.State);

            if (before is null)
            {
                continue;
            }

            if (before != told.State)
            {
                await Update(task => task.Id == told.TaskId && task.State == before, task =>
                {
                    task.State = told.State;
                    task.Error = null;
                });

                if (before == SwarmTaskState.Lost && await book.Read(document => document.Outgoing.FirstOrDefault(task => task.Id == told.TaskId)?.JobId) is { } revived)
                {
                    await ReportAgain(revived);
                }
            }

            if (told.Messages.Count > 0)
            {
                IReadOnlyList<ChatMessage> kept = await book.LoadChat(told.TaskId);

                if (told.Offset >= 0 && told.Offset <= kept.Count)
                {
                    await book.SaveChat(told.TaskId, [.. kept.Take(told.Offset), .. told.Messages.Where(message => message is { Text: not null }).Take(Math.Max(0, chatLimit - told.Offset))]);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task Receive(string peerId, SwarmResultBody result, ReadOnlyMemory<byte> bundle)
    {
        SwarmOutgoing? task = await book.Read(document => document.Outgoing.FirstOrDefault(task => task.Id == result.TaskId && task.PeerId == peerId));

        if (task is null || task.State is SwarmTaskState.Done or SwarmTaskState.Cancelled)
        {
            if (task is { Mode: SwarmMode.GitRemote, State: SwarmTaskState.Cancelled } && await Folder(task.WorkspaceId) is { } abandoned)
            {
                try
                {
                    await repository.DeleteRemote(abandoned, Branches(task), CancellationToken.None);
                }
                catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
                {
                    // The branches stay on the remote; refusing the result over them would hold up everything behind it.
                }
            }

            return;
        }

        if (result.Outcome == SwarmOutcome.MissingHistory && !task.IsResent)
        {
            await book.Change(document => document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId)?.KnownCommits.Remove(task.RepoKey));
            await Update(entry => entry.Id == task.Id && entry.State is not (SwarmTaskState.Done or SwarmTaskState.Cancelled), entry =>
            {
                entry.IsResent = true;
                entry.State = SwarmTaskState.Pending;
                entry.RequestedPeerId ??= peerId;
                entry.PeerId = null;
                entry.MessageId = null;
            });
            _ = Schedule();
            return;
        }

        string? branch = null;
        string? commit = null;
        string? error = result.Error;
        SwarmTaskState state = result.Outcome switch { SwarmOutcome.Done => SwarmTaskState.Done, SwarmOutcome.Cancelled => SwarmTaskState.Cancelled, _ => SwarmTaskState.Failed };

        // A task the AI could not finish still sends back what it changed.
        if (state is SwarmTaskState.Done or SwarmTaskState.Failed && result.ResultCommit is not null)
        {
            try
            {
                string folder = await Folder(task.WorkspaceId) ?? throw new RequestException("The workspace of the task is gone.");
                string wanted = $"swarm/{task.Id.ToString("N")[..8]}-{Slug(task.Title)}";
                (commit, branch) = task.Mode == SwarmMode.GitRemote
                    ? await repository.Fetch(folder, $"swarm/results/{task.Id:N}", task.Id, task.BaseCommit, wanted, CancellationToken.None)
                    : await repository.Import(folder, bundle, task.Id, task.BaseCommit, wanted, CancellationToken.None);

                if (commit != result.ResultCommit)
                {
                    await repository.Release(folder, task.Id, branch, CancellationToken.None);
                    throw new RequestException("The changes that arrived are not the ones the worker named.");
                }
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
            {
                // Left unacknowledged, the package arrives again, and what was in the way may be gone by then.
                (DateTimeOffset since, int attempts) = failing.AddOrUpdate(task.Id, _ => (DateTimeOffset.UtcNow, 1), (_, before) => (before.Since, before.Attempts + 1));

                // Attempts count too, since by drive they are as far apart as the drive's trips.
                if (exception is not RequestException { IsTemporary: false } && (attempts < leastAttempts || DateTimeOffset.UtcNow - since < takePatience))
                {
                    throw new RequestException($"The result could not be taken yet: {exception.Message}") { IsTemporary = true };
                }

                error = state == SwarmTaskState.Failed && error is not null ? $"{error} Its changes could not be taken: {exception.Message}" : $"The result could not be taken: {exception.Message}";
                state = SwarmTaskState.Failed;
                commit = null;
                branch = null;
            }
        }

        failing.TryRemove(task.Id, out _);

        bool wasLost = task.State == SwarmTaskState.Lost;
        await book.SaveChat(task.Id, [.. result.Messages.Where(message => message is { Text: not null }).Take(chatLimit)]);
        await book.Change(document =>
        {
            if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId) is { } peer && result.Outcome != SwarmOutcome.MissingHistory)
            {
                List<string> known = peer.KnownCommits.TryGetValue(task.RepoKey, out List<string>? commits) ? commits : peer.KnownCommits[task.RepoKey] = [];

                if (state == SwarmTaskState.Done && !known.Contains(task.BaseCommit))
                {
                    known.Add(task.BaseCommit);
                    known.RemoveRange(0, Math.Max(0, known.Count - 20));
                }
            }
        }, false);
        // Taking the changes can take long, during which the task may have been cancelled or delegated again.
        int taken = await Update(entry => entry.Id == task.Id && entry.State is not (SwarmTaskState.Done or SwarmTaskState.Cancelled), entry =>
        {
            entry.State = state;
            entry.Report = Limit(result.Report, reportLimit);
            entry.Error = Limit(error, errorLimit);
            entry.ResultCommit = commit;
            entry.Branch = branch;
        });

        if (taken == 0)
        {
            if (branch is not null && await Folder(task.WorkspaceId) is { } unwanted)
            {
                await repository.Release(unwanted, task.Id, branch, CancellationToken.None);
            }

            return;
        }

        if (wasLost)
        {
            await ReportAgain(task.JobId);
        }

        book.AnnounceTask(task.Id);
        // Sending what this unblocked can take long, and the connection that brought the result waits for this.
        _ = Schedule();
    }

    /// <inheritdoc />
    public async Task Cancel(Guid taskId)
    {
        SwarmOutgoing task = await Find(taskId);

        if (task.State is SwarmTaskState.Done or SwarmTaskState.Failed or SwarmTaskState.Cancelled)
        {
            throw new RequestException("The task already ended.");
        }

        await End(task, SwarmTaskState.Cancelled, "It was cancelled.");
        await DispatchSoon();
    }

    /// <inheritdoc />
    public async Task<Guid> Retry(Guid taskId, CancellationToken cancellation)
    {
        SwarmOutgoing task = await Find(taskId);
        RequireOpen(task.WorkspaceId);

        if (task.State is not (SwarmTaskState.Failed or SwarmTaskState.Cancelled or SwarmTaskState.Lost))
        {
            throw new RequestException("Only a failed, cancelled or lost task can be delegated again.");
        }

        if (task.State == SwarmTaskState.Lost)
        {
            await End(task, SwarmTaskState.Cancelled, "It was delegated again.");
        }

        string folder = await Folder(task.WorkspaceId) ?? throw new RequestException("The workspace of the task is gone.");
        Guid id = Guid.NewGuid();
        await repository.Pin(folder, id, task.SnapshotCommit, cancellation);
        List<SwarmDependency> kept = await book.Read(document => task.Dependencies.ToList());

        foreach (SwarmDependency dependency in kept.Where(dependency => dependency.ResultCommit is not null))
        {
            await repository.PinDependency(folder, id, dependency.TaskId, dependency.ResultCommit!, cancellation);
        }

        await book.Change(document =>
        {
            document.Outgoing.Add(new SwarmOutgoing { Id = id, JobId = task.JobId, WorkspaceId = task.WorkspaceId, Title = task.Title, Instructions = task.Instructions, ExpectedResult = task.ExpectedResult, Capability = task.Capability, RequestedPeerId = task.RequestedPeerId, PeerName = task.RequestedPeerId is null ? string.Empty : task.PeerName, DependsOn = [.. task.DependsOn], Dependencies = kept, State = task.DependsOn.Count > 0 ? SwarmTaskState.Waiting : SwarmTaskState.Pending, RepoKey = task.RepoKey, SnapshotCommit = task.SnapshotCommit, BaseCommit = task.SnapshotCommit, IsManual = task.IsManual, BringIn = task.BringIn, CommitBringIn = task.CommitBringIn, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

            // The tasks that waited for the failed one wait for its replacement.
            foreach (SwarmOutgoing waiting in document.Outgoing.Where(other => other.State == SwarmTaskState.Waiting && other.DependsOn.Contains(taskId)))
            {
                waiting.DependsOn = [.. waiting.DependsOn.Select(dependency => dependency == taskId ? id : dependency)];
            }

            if (document.Jobs.FirstOrDefault(job => job.Id == task.JobId) is { } job)
            {
                job.IsReported = false;
                job.Revision++;
            }
        });
        await DispatchSoon();
        return id;
    }

    /// <inheritdoc />
    public async Task<string> Changes(Guid taskId, string? path, CancellationToken cancellation)
    {
        SwarmOutgoing task = await Find(taskId);

        if (task.ResultCommit is not { } result)
        {
            return task.State == SwarmTaskState.Done ? "The task changed no files." : "The task has no result yet.";
        }

        string folder = await Folder(task.WorkspaceId) ?? throw new RequestException("The workspace of the task is gone.");
        return await repository.Changes(folder, task.BaseCommit, result, path, 20_000, cancellation);
    }

    /// <inheritdoc />
    public async Task Discard(Guid taskId, CancellationToken cancellation)
    {
        SwarmOutgoing task = await Find(taskId);

        if (task.State is SwarmTaskState.Waiting or SwarmTaskState.Pending or SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running)
        {
            throw new RequestException("Cancel the task before discarding it.");
        }

        if (task.State == SwarmTaskState.Lost)
        {
            await End(task, SwarmTaskState.Cancelled, "It was discarded.");
        }

        if (await Folder(task.WorkspaceId) is { } folder)
        {
            await repository.Release(folder, taskId, task.Branch, cancellation);

            if (task.Mode == SwarmMode.GitRemote && task.RemoteUrl is not null)
            {
                await repository.DeleteRemote(folder, Branches(task), cancellation);
            }
        }

        book.DeleteChat(taskId);
        await book.Change(document =>
        {
            document.Outgoing.RemoveAll(entry => entry.Id == taskId);
            document.Jobs.RemoveAll(job => document.Outgoing.All(entry => entry.JobId != job.Id));
        });
        await DispatchSoon();
    }

    /// <inheritdoc />
    public async Task Complete(Guid taskId)
    {
        SwarmOutgoing task = await Find(taskId);
        string? folder = await Folder(task.WorkspaceId);

        if (folder is not null && task.ResultCommit is { } result)
        {
            foreach (Guid waiting in await book.Read(document => document.Outgoing.Where(other => other.State == SwarmTaskState.Waiting && other.DependsOn.Contains(taskId)).Select(other => other.Id).ToList()))
            {
                try
                {
                    await repository.PinDependency(folder, waiting, taskId, result, CancellationToken.None);
                }
                catch (RequestException exception)
                {
                    log?.Write($"A result a waiting task builds on could not be kept: {exception.Message}");
                }
            }
        }

        await book.Change(document =>
        {
            if (document.Outgoing.FirstOrDefault(entry => entry.Id == taskId) is not { } done)
            {
                return;
            }

            foreach (SwarmOutgoing waiting in document.Outgoing.Where(other => other.State == SwarmTaskState.Waiting && other.DependsOn.Contains(taskId)))
            {
                waiting.Dependencies = [.. waiting.Dependencies.Where(dependency => dependency.TaskId != taskId), Capture(done)];
            }

            document.Outgoing.Remove(done);
            document.Jobs.RemoveAll(job => document.Outgoing.All(entry => entry.JobId != job.Id));
        });
        book.DeleteChat(taskId);
        book.AnnounceTask(taskId);

        if (folder is not null)
        {
            await repository.Release(folder, taskId, task.Branch, CancellationToken.None);

            if (task.Mode == SwarmMode.GitRemote && task.RemoteUrl is not null)
            {
                // Whoever took the result over does not wait for a remote that may be slow or unreachable.
                _ = DeleteRemoteApart(folder, Branches(task));
            }
        }

        _ = Schedule();
    }

    /// <inheritdoc />
    public async Task CheckLost(TimeSpan silent)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Silence only counts while this server listened: after a restart, or when the machine slept through the checks, workers first get their chance to be heard.
        if (now - checkedAt > checkGap)
        {
            listeningSince = now;
        }

        checkedAt = now;
        DateTimeOffset cutoff = now - silent;

        if (listeningSince > cutoff)
        {
            return;
        }

        List<Guid> lost = await book.Read(document => document.Outgoing.Where(task => task.Mode != SwarmMode.AirGapped && task.State is SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running && document.Peers.FirstOrDefault(peer => peer.Identity.Id == task.PeerId) is { } peer && (peer.LastSeen ?? DateTimeOffset.MinValue) < cutoff && task.UpdatedAt < cutoff).Select(task => task.Id).ToList());

        if (lost.Count == 0)
        {
            return;
        }

        await Update(task => lost.Contains(task.Id), task =>
        {
            task.State = SwarmTaskState.Lost;
            task.Error = "The worker stopped being heard from.";
        });
        await DispatchSoon();
    }

    /// <inheritdoc />
    public async Task Revoke(string peerId)
    {
        await FailTasksOf(peerId);
        await DispatchSoon();
    }

    /// <inheritdoc />
    public Task<bool> IsAwaited(Guid workspaceId)
        => book.Read(document => document.Jobs.Any(job => job.WorkspaceId == workspaceId && job is { IsManual: false, IsReported: false } && document.Outgoing.Any(task => task.JobId == job.Id) && !document.IsOver(job.Id)));

    /// <inheritdoc />
    public async Task<bool> IsUnderWay(Guid workspaceId)
        => await book.Read(document => document.Outgoing.Any(task => task.WorkspaceId == workspaceId && task.State is SwarmTaskState.Waiting or SwarmTaskState.Pending or SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running)
            || document.Outgoing.Any(task => task.WorkspaceId == workspaceId && (task.TurnId is not null || task is { BringIn: true, State: SwarmTaskState.Done, ResultCommit: not null }))
            || document.Jobs.Any(job => job.WorkspaceId == workspaceId && job is { IsManual: false, IsReported: false } && document.Outgoing.Any(task => task.JobId == job.Id)));

    /// <inheritdoc />
    public async Task Forget(Guid workspaceId)
    {
        List<SwarmOutgoing> tasks = await book.Read(document => document.Outgoing.Where(task => task.WorkspaceId == workspaceId).ToList());

        foreach (SwarmOutgoing task in tasks.Where(task => task.State is SwarmTaskState.Waiting or SwarmTaskState.Pending or SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running or SwarmTaskState.Lost))
        {
            await End(task, SwarmTaskState.Cancelled, "The workspace was deleted.");
        }

        if (tasks.Count > 0 && await Folder(workspaceId) is { } folder)
        {
            await Tidy(folder, tasks);
        }

        foreach (SwarmOutgoing task in tasks)
        {
            book.DeleteChat(task.Id);
        }

        await book.Change(document =>
        {
            document.Outgoing.RemoveAll(task => task.WorkspaceId == workspaceId);
            document.Jobs.RemoveAll(job => job.WorkspaceId == workspaceId);
        });
    }

    private async Task End(SwarmOutgoing task, SwarmTaskState state, string reason)
    {
        if (task.PeerId is { } peerId && task.State is SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running or SwarmTaskState.Lost)
        {
            if (task.MessageId is { } message)
            {
                courier.Withdraw(peerId, message);
            }

            // Sent even when the task was still waiting here: a drive may already carry it.
            try
            {
                await courier.Post(peerId, SwarmMessageKind.Cancel, new SwarmCancelBody { TaskId = task.Id }, ReadOnlyMemory<byte>.Empty);
            }
            catch (RequestException)
            {
            }
        }

        await Update(entry => entry.Id == task.Id, entry =>
        {
            entry.State = state;
            entry.Error = reason;
        });
    }

    private Task FailTasksOf(string peerId) => Update(task => (task.PeerId == peerId || task.RequestedPeerId == peerId) && task.State is SwarmTaskState.Waiting or SwarmTaskState.Pending or SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running or SwarmTaskState.Lost, task =>
    {
        task.State = SwarmTaskState.Failed;
        task.Error = "The worker is no longer paired.";
    });

    private async Task Unblock()
    {
        List<SwarmOutgoing> waiting = await book.Read(document => document.Outgoing.Where(task => task.State == SwarmTaskState.Waiting).OrderBy(task => task.CreatedAt).ToList());

        foreach (SwarmOutgoing task in waiting)
        {
            // A finished task whose result was brought in is gone, but what was taken of it is kept with this one.
            List<(SwarmTaskState? State, SwarmDependency? Result)> dependencies = await book.Read(document => task.DependsOn.Select(id => document.Outgoing.FirstOrDefault(other => other.Id == id) is { } other
                ? ((SwarmTaskState?)other.State, other.State == SwarmTaskState.Done ? Capture(other) : null)
                : task.Dependencies.FirstOrDefault(kept => kept.TaskId == id) is { } taken ? (SwarmTaskState.Done, taken) : ((SwarmTaskState?)null, (SwarmDependency?)null)).ToList());

            // A lost task may still come back, so what builds on it keeps waiting.
            if (dependencies.Any(dependency => dependency.State is null or SwarmTaskState.Failed or SwarmTaskState.Cancelled))
            {
                await Update(entry => entry.Id == task.Id && entry.State == SwarmTaskState.Waiting, entry =>
                {
                    entry.State = SwarmTaskState.Cancelled;
                    entry.Error = "A task it depends on did not finish.";
                });
                continue;
            }

            if (dependencies.Any(dependency => dependency.State != SwarmTaskState.Done))
            {
                continue;
            }

            try
            {
                string folder = await Folder(task.WorkspaceId) ?? throw new RequestException("The workspace of the task is gone.");
                List<SwarmDependency> results = [.. dependencies.Select(dependency => dependency.Result!)];

                foreach (SwarmDependency dependency in results.Where(dependency => dependency.ResultCommit is not null))
                {
                    await repository.PinDependency(folder, task.Id, dependency.TaskId, dependency.ResultCommit!, CancellationToken.None);
                }

                await repository.Pin(folder, task.Id, task.SnapshotCommit, CancellationToken.None);
                await Update(entry => entry.Id == task.Id && entry.State == SwarmTaskState.Waiting, entry =>
                {
                    entry.Dependencies = results;
                    entry.BaseCommit = entry.SnapshotCommit;
                    entry.State = SwarmTaskState.Pending;
                });
            }
            catch (RequestException exception)
            {
                await Update(entry => entry.Id == task.Id && entry.State == SwarmTaskState.Waiting, entry =>
                {
                    entry.State = SwarmTaskState.Failed;
                    entry.Error = exception.Message;
                });
            }
        }
    }

    private SwarmDependency Capture(SwarmOutgoing task) => new() { TaskId = task.Id, Title = task.Title, BaseCommit = task.BaseCommit, ResultCommit = task.ResultCommit, Report = Limit(task.Report, dependencyReportLimit), Includes = task.ResultCommit is null ? [] : task.Dependencies.Included() };

    private async Task<string?> Choose(Guid taskId)
    {
        (SwarmOutgoing? task, string? chosen, string? failure, string? held) = await book.Read<(SwarmOutgoing?, string?, string?, string?)>(document =>
        {
            if (document.Outgoing.FirstOrDefault(task => task.Id == taskId && task.State == SwarmTaskState.Pending) is not { } task)
            {
                return (null, null, null, null);
            }

            // What a worker said some time ago says nothing about one that has since been switched off.
            bool Fresh(SwarmPeerRecord peer) => peer.LastSeen is { } seen && DateTimeOffset.UtcNow - seen < freshness;
            bool Offers(SwarmPeerRecord peer) => peer.Profile is { } profile && profile.Capabilities.Any(capability => capability.Capability == task.Capability);
            int Available(SwarmPeerRecord peer) => (peer.Profile?.FreeSlots ?? 0) - document.Outgoing.Count(other => other.PeerId == peer.Identity.Id && other.State == SwarmTaskState.InTransit);
            bool needs = task.Dependencies.HasChanges;
            bool Builds(SwarmPeerRecord peer) => !needs || peer.Profile?.Version >= SwarmProfileBody.DependencyVersion;

            if (task.RequestedPeerId is { } requested)
            {
                SwarmPeerRecord? peer = document.Peers.FirstOrDefault(peer => peer.Identity.Id == requested);

                if (peer is not { SendWork: true })
                {
                    return (task, null, "Its worker is no longer set to receive work.", null);
                }

                if (!Builds(peer))
                {
                    // What is known of an air gapped worker only changes with the drive, so there is nothing to wait for.
                    if (peer is { Mode: SwarmMode.AirGapped, Profile: not null })
                    {
                        return (task, null, $"It builds on the changes of other tasks, which {peer.Name} cannot take with its version of Auto Remote. Update it, sync the drive both ways, and retry.", null);
                    }

                    return (task, null, null, peer.Profile is null
                        ? $"It builds on the changes of other tasks, and it is not known yet whether {peer.Name} can take that{(peer.Mode == SwarmMode.AirGapped ? "; sync the drive to find out" : string.Empty)}."
                        : $"It builds on the changes of other tasks, which {peer.Name} cannot take with its version of Auto Remote. Update it.");
                }

                bool ready = peer.Mode == SwarmMode.AirGapped || Fresh(peer) && peer.Profile is { AcceptsWork: true } && Offers(peer) && Available(peer) > 0;
                return (task, ready ? requested : null, null, null);
            }

            List<SwarmPeerRecord> able = [.. document.Peers.Where(peer => peer.SendWork && peer.Mode != SwarmMode.AirGapped && Fresh(peer) && peer.Profile is { AcceptsWork: true } && Offers(peer))];
            string? any = able.Where(peer => Builds(peer) && Available(peer) > 0)
                .OrderByDescending(peer => peer.Profile!.LoadedCapabilities.Contains(task.Capability))
                .ThenByDescending(Available)
                .ThenBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.Identity.Id;
            return (task, any, null, able.Count > 0 && !able.Any(Builds) ? "It builds on the changes of other tasks, which no worker that could take it can do with its version of Auto Remote. Update one." : null);
        });

        if (task is null)
        {
            return null;
        }

        if (failure is not null)
        {
            await Update(entry => entry.Id == taskId && entry.State == SwarmTaskState.Pending, entry =>
            {
                entry.State = SwarmTaskState.Failed;
                entry.Error = failure;
            });
        }
        else if (chosen is null && await book.Read(document => document.Outgoing.Any(entry => entry.Id == taskId && entry.State == SwarmTaskState.Pending && entry.Error != held)))
        {
            await Update(entry => entry.Id == taskId && entry.State == SwarmTaskState.Pending, entry => entry.Error = held);
        }

        return chosen;
    }

    // The result branches stay, since they may hold work not applied yet; what only served the exchange goes.
    private async Task Tidy(string folder, IReadOnlyList<SwarmOutgoing> tasks)
    {
        using CancellationTokenSource patience = new(tidyPatience);

        try
        {
            foreach (SwarmOutgoing task in tasks)
            {
                await repository.Release(folder, task.Id, null, patience.Token);
            }

            foreach (SwarmOutgoing task in tasks.Where(task => task.Mode == SwarmMode.GitRemote && task.RemoteUrl is not null))
            {
                await repository.DeleteRemote(folder, Branches(task), patience.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or RequestException or IOException or UnauthorizedAccessException)
        {
            // The workspace is deleted all the same.
        }
    }

    private async Task Send(Guid taskId, string peerId)
    {
        SwarmOutgoing task = await Find(taskId);
        (SwarmMode mode, string name, List<string> known) = await book.Read(document =>
        {
            SwarmPeerRecord peer = document.Peers.First(peer => peer.Identity.Id == peerId);
            return (peer.Mode, peer.Name, peer.KnownCommits.TryGetValue(task.RepoKey, out List<string>? commits) ? commits.ToList() : []);
        });

        try
        {
            string folder = await Folder(task.WorkspaceId) ?? throw new RequestException("The workspace of the task is gone.");
            string? url = null;
            ReadOnlyMemory<byte> bundle = ReadOnlyMemory<byte>.Empty;

            if (mode == SwarmMode.GitRemote)
            {
                url = await repository.Remote(folder, CancellationToken.None);
                await repository.Push(folder, "origin", task.BaseCommit, $"swarm/tasks/{taskId:N}", CancellationToken.None);

                foreach (SwarmDependency dependency in task.Dependencies.Where(dependency => dependency.ResultCommit is not null))
                {
                    await repository.Push(folder, "origin", dependency.ResultCommit!, DependencyBranch(taskId, dependency.TaskId), CancellationToken.None);
                }
            }
            else
            {
                bundle = await repository.Bundle(folder, taskId, known, CancellationToken.None);
            }

            SwarmTaskBody body = new() { TaskId = taskId, JobId = task.JobId, Title = task.Title, Instructions = task.Instructions, ExpectedResult = task.ExpectedResult, Capability = task.Capability, Mode = mode, RepoKey = task.RepoKey, BaseCommit = task.BaseCommit, RemoteUrl = url, Dependencies = [.. task.Dependencies] };
            Guid message = await courier.Post(peerId, SwarmMessageKind.Task, body, bundle);
            bool sent = await book.Change(document =>
            {
                if (document.Outgoing.FirstOrDefault(entry => entry.Id == taskId && entry.State == SwarmTaskState.Pending) is not { } entry || document.Peers.All(peer => peer.Identity.Id != peerId || !peer.SendWork))
                {
                    return false;
                }

                entry.State = SwarmTaskState.InTransit;
                entry.PeerId = peerId;
                entry.PeerName = name;
                entry.Mode = mode;
                entry.RemoteUrl = url;
                entry.MessageId = message;
                entry.Error = null;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                return true;
            });

            if (sent)
            {
                book.AnnounceTask(taskId);
            }
            else
            {
                // The worker may be told to cancel it below and then never runs it under this identifier again, so it cannot go on waiting here.
                await Update(entry => entry.Id == taskId && entry.State == SwarmTaskState.Pending, entry =>
                {
                    entry.State = SwarmTaskState.Failed;
                    entry.PeerName = name;
                    entry.Error = $"{name} stopped receiving work while the task was being sent to it.";
                });
                // It was cancelled, or the peer stopped receiving work, while its code was packed or pushed; a connection or drive may already have taken the package.
                courier.Withdraw(peerId, message);

                try
                {
                    await courier.Post(peerId, SwarmMessageKind.Cancel, new SwarmCancelBody { TaskId = taskId }, ReadOnlyMemory<byte>.Empty);
                }
                catch (RequestException)
                {
                    // It is not paired any more, so there is nobody to tell.
                }
            }
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
            await Update(entry => entry.Id == taskId && entry.State == SwarmTaskState.Pending, entry =>
            {
                entry.State = SwarmTaskState.Failed;
                entry.PeerName = name;
                entry.Error = $"It could not be sent to {name}: {exception.Message}";
            });
        }
    }

    private Task ReportAgain(Guid jobId) => book.Change(document =>
    {
        if (document.Jobs.FirstOrDefault(job => job.Id == jobId) is { } job)
        {
            job.IsReported = false;
            job.Revision++;
        }
    }, false);

    private Task Schedule() => Task.Run(async () =>
    {
        try
        {
            await Dispatch();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The periodic check dispatches again.
            log?.Write($"Sending delegated tasks failed: {exception.Message}");
        }
    });

    // These name only a task, so the side session it belongs to is checked here: one being integrated takes no new files.
    private void RequireOpen(Guid workspaceId)
    {
        if (workspaces.GetSide(workspaceId) is { State: not SideSessionState.Ready })
        {
            throw new RequestException("The side session of this task is being set up, brought into main or removed.");
        }
    }

    private async Task<SwarmOutgoing> Find(Guid taskId) => await book.Read(document => document.Outgoing.FirstOrDefault(task => task.Id == taskId)) ?? throw new RequestException("There is no such delegated task.");

    private async Task<int> Update(Func<SwarmOutgoing, bool> which, Action<SwarmOutgoing> change)
    {
        List<Guid> changed = await book.Change(document =>
        {
            List<SwarmOutgoing> matching = [.. document.Outgoing.Where(which)];

            foreach (SwarmOutgoing task in matching)
            {
                change(task);
                task.UpdatedAt = DateTimeOffset.UtcNow;
            }

            return matching.Select(task => task.Id).ToList();
        });

        foreach (Guid id in changed)
        {
            book.AnnounceTask(id);
        }

        return changed.Count;
    }

    private string? Limit(string? text, int limit) => text is not null && text.Length > limit ? $"{text[..limit]}..." : text;

    private async Task<string?> Folder(Guid workspaceId)
    {
        try
        {
            return (await workspaces.Get(workspaceId, CancellationToken.None)).Folder;
        }
        catch (RequestException)
        {
            return null;
        }
    }

    private async Task DeleteRemoteApart(string folder, IReadOnlyList<string> branches)
    {
        using CancellationTokenSource patience = new(tidyPatience);

        try
        {
            await repository.DeleteRemote(folder, branches, patience.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or RequestException or IOException or UnauthorizedAccessException)
        {
            log?.Write($"The branches of a delegated task were left on the remote: {exception.Message}");
        }
    }

    private string DependencyBranch(Guid taskId, Guid dependencyId) => $"swarm/dependencies/{taskId:N}/{dependencyId:N}";

    private IReadOnlyList<string> Branches(SwarmOutgoing task) => [$"swarm/tasks/{task.Id:N}", $"swarm/results/{task.Id:N}", .. task.Dependencies.Where(dependency => dependency.ResultCommit is not null).Select(dependency => DependencyBranch(task.Id, dependency.TaskId))];

    private string Slug(string title)
    {
        string slug = string.Join('-', new string([.. title.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : ' ')]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "task" : slug[..Math.Min(slug.Length, 40)].TrimEnd('-');
    }
}
