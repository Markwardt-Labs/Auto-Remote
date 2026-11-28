namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Gets the results of delegated tasks into the workspaces they were delegated from, which their AI does: it gets a turn of its own with read only copies of what each task left, and confirms each one with a tool, which removes the task. The results of the tasks an AI delegated come with the news that its job ended. A result that is still not confirmed after the second turn stays with its task, to be brought in when the user asks.
/// </summary>
internal interface ISwarmBringIns
{
    /// <summary>
    /// Gives the workspaces back the access to what they were being asked to bring in when the server stopped, and takes the requests it asks for again out of the restored queue. Called before restored work can start.
    /// </summary>
    /// <param name="cancellation">Ends the work when the server stops.</param>
    /// <returns>A task that completes once done.</returns>
    Task Open(CancellationToken cancellation);

    /// <summary>
    /// Takes up the turns that were under way, which the scheduler has restored by now, and starts giving out new ones.
    /// </summary>
    /// <returns>A task that completes once started.</returns>
    Task Start();

    /// <summary>
    /// Gets what waits to be brought into a workspace or told to its AI.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>What does not have its turn yet.</returns>
    IReadOnlyList<BringInItem> Pending(Guid workspaceId);

    /// <summary>
    /// Tells whether a turn waits for a workspace or is on its way to it, which goes before the queued inputs and a loop's next prompt.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns><see langword="true" /> if so.</returns>
    bool IsDelivering(Guid workspaceId);

    /// <summary>
    /// Has the result of a finished task brought into its workspace.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <returns>What happens.</returns>
    /// <exception cref="RequestException">There is nothing to bring in, or it is under way already.</exception>
    Task<string> Ask(Guid taskId);

    /// <summary>
    /// Sets whether the result of a task is brought in once it is done. Turning it off also cancels bringing it in that the AI has not taken yet.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="enabled">Whether it is.</param>
    /// <returns>A task that completes once set.</returns>
    /// <exception cref="RequestException">There is no such task.</exception>
    Task Set(Guid taskId, bool enabled);

    /// <summary>
    /// Sets whether the changes are committed after something that waits was brought in.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="id">What waits, as <see cref="Pending" /> names it.</param>
    /// <param name="commit">Whether they are.</param>
    /// <returns>A task that completes once set.</returns>
    /// <exception cref="RequestException">Nothing of that name waits.</exception>
    Task SetCommit(Guid workspaceId, Guid id, bool commit);

    /// <summary>
    /// Takes the AI's word that it brought a result in, which removes the task.
    /// </summary>
    /// <param name="workspaceId">The workspace of the AI.</param>
    /// <param name="bringInId">The identifier the request gave.</param>
    /// <returns>What happened, for the AI, or <see langword="null" /> when no result of that workspace has the identifier.</returns>
    Task<string?> Finish(Guid workspaceId, string bringInId);

    /// <summary>
    /// Makes a side session that starts from the result of a finished task, to go on with it by hand; the task is removed.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="name">The name of the session, or <see langword="null" /> to name it after the task.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The session, which is being set up.</returns>
    /// <exception cref="RequestException">The task cannot be continued or the session cannot be made.</exception>
    Task<WorkspaceInfo> Continue(Guid taskId, string? name, CancellationToken cancellation);

    /// <summary>
    /// Takes back what is being done to bring in the result of a task that is about to be removed.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <returns>A task that completes once done.</returns>
    Task Withdraw(Guid taskId);

    /// <summary>
    /// Takes back everything that was to be brought into a workspace whose tasks are about to be forgotten.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>A task that completes once done.</returns>
    Task Forget(Guid workspaceId);
}

/// <inheritdoc />
internal sealed class SwarmBringIns : ISwarmBringIns
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmBringIns" /> class.
    /// </summary>
    /// <param name="book">The delegated tasks.</param>
    /// <param name="coordinator">Removes the tasks whose results were taken over.</param>
    /// <param name="repository">Writes the results out.</param>
    /// <param name="workspaces">Knows the workspaces.</param>
    /// <param name="prompts">Gives the AI the turn and the access to the results.</param>
    /// <param name="queue">Takes the turns of workspaces on the local AI.</param>
    /// <param name="activity">Tells what the workspaces do.</param>
    /// <param name="guard">Holds turns back at a usage limit.</param>
    /// <param name="snapshots">Keeps work that took a result over from being reverted.</param>
    /// <param name="events">Tells about failures.</param>
    /// <param name="log">The server log.</param>
    /// <param name="sideSessions">Makes the sessions results are continued in.</param>
    /// <param name="following">Gives the scheduler, which is made later.</param>
    /// <param name="journal">The activity of the workspaces.</param>
    /// <param name="reopen">Forgets that a side session was done, since it is given more work.</param>
    public SwarmBringIns(ISwarmBook book, ISwarmCoordinator coordinator, ISwarmRepository repository, IWorkspaceManager workspaces, IPromptService prompts, IWorkQueue queue, IWorkspaceActivity activity, IWindowGuard? guard, IWorkspaceSnapshots snapshots, IEventHub events, ILog log, ISideSessions sideSessions, Func<IFollowUps?> following, IActivityLog? journal = null, Func<Guid, Task>? reopen = null)
    {
        this.book = book;
        this.coordinator = coordinator;
        this.repository = repository;
        this.workspaces = workspaces;
        this.prompts = prompts;
        this.queue = queue;
        this.activity = activity;
        this.guard = guard;
        this.snapshots = snapshots;
        this.events = events;
        this.log = log;
        this.sideSessions = sideSessions;
        this.following = following;
        this.journal = journal;
        this.reopen = reopen;
        book.Watch(Look);
    }

    private readonly ISwarmBook book;

    private readonly ISwarmCoordinator coordinator;

    private readonly ISwarmRepository repository;

    private readonly IWorkspaceManager workspaces;

    private readonly IPromptService prompts;

    private readonly IWorkQueue queue;

    private readonly IWorkspaceActivity activity;

    private readonly IWindowGuard? guard;

    private readonly IWorkspaceSnapshots snapshots;

    private readonly IEventHub events;

    private readonly ILog log;

    private readonly ISideSessions sideSessions;

    private readonly Func<IFollowUps?> following;

    private readonly IActivityLog? journal;

    private readonly Func<Guid, Task>? reopen;

    // Every change of what is brought in is decided one at a time.
    private readonly SemaphoreSlim deciding = new(1, 1);

    private readonly SemaphoreSlim signal = new(0);

    // The workspaces a turn is on its way to, until the scheduler knows about it.
    private readonly ConcurrentDictionary<Guid, Guid> sending = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> notBefore = new();

    private readonly ConcurrentDictionary<Guid, int> unavailable = new();

    private readonly ConcurrentDictionary<Guid, int> unprepared = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> lost = new();

    private readonly ConcurrentDictionary<Guid, byte> continuing = new();

    private volatile Dictionary<Guid, List<BringInItem>> pending = [];

    private volatile HashSet<Guid> unconfirmedTurns = [];

    private readonly int deliveryLimit = 2;

    private readonly int preparationLimit = 3;

    private readonly int listedChanges = 200;

    private readonly int summaryLimit = 1500;

    private readonly int nameLimit = 60;

    private readonly TimeSpan longestPause = TimeSpan.FromMinutes(15);

    private readonly string unconfirmed = "Bringing it in was not confirmed.";

    private CancellationToken stopping;

    private IDisposable? observing;

    /// <summary>
    /// Gets how long to wait before asking again after a turn could not be had, which grows while that goes on.
    /// </summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets how often it is looked whether a waiting turn can be had.
    /// </summary>
    internal TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets how long a turn nothing knows about any more is waited for before it counts as stopped.
    /// </summary>
    internal TimeSpan Patience { get; init; } = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task Open(CancellationToken cancellation)
    {
        stopping = cancellation;

        // The scheduler goes on by itself with an interrupted turn that told about delegated tasks; the others are asked for again.
        foreach ((Guid workspace, string text) in await book.Read(document => document.Turns.Where(turn => turn.Source != ActivitySource.DelegatedResults).Select(turn => (turn.WorkspaceId, turn.Text)).ToList()))
        {
            await queue.Withdraw(workspace, text);
        }

        List<(Guid Workspace, Guid Task, string BringIn)> shared = await book.Read(document => document.Outgoing.Where(task => task.BringInId is not null).Select(task => (task.WorkspaceId, task.Id, task.BringInId!)).ToList());

        foreach ((Guid workspace, Guid task, string bringInId) in shared.Where(entry => Directory.Exists(ResultFolder(entry.Task))))
        {
            prompts.Share(workspace, Key(bringInId), [ResultFolder(task)]);
        }

        Tidy([.. shared.Select(entry => ResultFolder(entry.Task))]);
    }

    /// <inheritdoc />
    public async Task Start()
    {
        if (following() is not { } followed)
        {
            return;
        }

        observing?.Dispose();
        observing = followed.Observe(Worked);

        foreach (SwarmTurn turn in await book.Read(document => document.Turns.ToList()))
        {
            FollowState state = followed.Describe(turn.WorkspaceId);

            if (turn.Source == ActivitySource.DelegatedResults && (state.Retry?.Id == turn.Id || state.Step?.Id == turn.Id))
            {
                followed.Require(turn.WorkspaceId, turn.Id, () => Shortfall(turn.Id));
            }
            else if (turn.Source != ActivitySource.DelegatedResults || !state.IsCommitDue && !state.IsCommitting)
            {
                if (turn.IsStarted)
                {
                    await Ended(turn.Id, new WorkEnd(ActivityOutcome.Failed, false, "The server stopped too often while the AI was bringing it in."));
                }
                else
                {
                    followed.NotSent(turn.WorkspaceId, turn.Id);
                    await Revert(turn.Id, TimeSpan.Zero);
                }
            }
        }

        events.Subscribe(Follow);
        _ = Task.Run(() => Pump(stopping), CancellationToken.None);
    }

    /// <inheritdoc />
    public IReadOnlyList<BringInItem> Pending(Guid workspaceId) => pending.TryGetValue(workspaceId, out List<BringInItem>? items) ? items : [];

    /// <inheritdoc />
    public bool IsDelivering(Guid workspaceId) => sending.ContainsKey(workspaceId) || pending.ContainsKey(workspaceId);

    /// <inheritdoc />
    public async Task<string> Ask(Guid taskId)
    {
        await deciding.WaitAsync();

        try
        {
            (Guid workspace, SwarmTaskState state, bool changed, bool busy) = await book.Read(document => Find(document, taskId) is var task ? (task.WorkspaceId, task.State, task.ResultCommit is not null, task.TurnId is not null) : default);

            if (state != SwarmTaskState.Done || !changed)
            {
                throw new RequestException(state == SwarmTaskState.Done ? "The task changed no files." : "Only a finished task has changes to bring in.");
            }

            if (busy)
            {
                throw new RequestException("The task is being brought in already.");
            }

            RequireOpen(workspace);
            notBefore.TryRemove(taskId, out _);
            unprepared.TryRemove(taskId, out _);
            await book.Change(document =>
            {
                SwarmOutgoing task = Find(document, taskId);
                task.BringIn = true;
                task.IsAsked = true;
                task.Deliveries = 0;
                task.Error = null;
                task.UpdatedAt = DateTimeOffset.UtcNow;
            });
        }
        finally
        {
            deciding.Release();
        }

        book.AnnounceTask(taskId);
        Wake();
        return "The AI of the workspace brings it in in a turn of its own once it is free.";
    }

    /// <inheritdoc />
    public async Task Set(Guid taskId, bool enabled)
    {
        await deciding.WaitAsync();

        try
        {
            (Guid workspace, Guid? turn) = await book.Read(document => Find(document, taskId) is var task ? (task.WorkspaceId, task.TurnId) : default);

            if (enabled)
            {
                RequireOpen(workspace);
                notBefore.TryRemove(taskId, out _);
                unprepared.TryRemove(taskId, out _);
            }
            else if (turn is { } waiting)
            {
                await CancelWaiting(waiting);
            }

            await book.Change(document =>
            {
                SwarmOutgoing task = Find(document, taskId);
                task.BringIn = enabled;
                task.IsAsked &= enabled;
                task.UpdatedAt = DateTimeOffset.UtcNow;

                if (enabled && task.TurnId is null)
                {
                    task.Deliveries = 0;
                    task.Error = task.State == SwarmTaskState.Done ? null : task.Error;
                }
            });

            if (!enabled)
            {
                await Drop(taskId);
            }
        }
        finally
        {
            deciding.Release();
        }

        book.AnnounceTask(taskId);
        Wake();
    }

    /// <inheritdoc />
    public async Task SetCommit(Guid workspaceId, Guid id, bool commit)
    {
        await deciding.WaitAsync();

        try
        {
            bool found = await book.Change(document =>
            {
                if (document.Outgoing.FirstOrDefault(task => task.Id == id && task.WorkspaceId == workspaceId) is not { } named)
                {
                    return false;
                }

                // The news of a job that brings nothing in is committed if any of its tasks asks for that.
                bool news = !document.IsToBringIn(named) && document.DueReports(workspaceId).Any(job => job.Id == named.JobId);

                foreach (SwarmOutgoing task in news ? document.Outgoing.Where(task => task.JobId == named.JobId) : [named])
                {
                    task.CommitBringIn = commit;
                }

                return true;
            });

            if (!found)
            {
                throw new RequestException("Nothing of that kind waits to be brought in.");
            }
        }
        finally
        {
            deciding.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> Finish(Guid workspaceId, string bringInId)
    {
        await deciding.WaitAsync();

        try
        {
            string id = bringInId.Trim();
            (Guid TaskId, string Title, string Peer, string? Report)? found = await book.Read<(Guid, string, string, string?)?>(document => document.Outgoing.FirstOrDefault(task => task.WorkspaceId == workspaceId && task.BringInId == id && task.State == SwarmTaskState.Done) is { } task ? (task.Id, task.Title, task.PeerName, task.Report) : null);

            if (found is not { } done)
            {
                return null;
            }

            // Reverting the work would now throw away the only copy of what the task did.
            snapshots.Protect(workspaceId, "This work brought in the result of a delegated task, which cancelling it would lose. Stop it instead.");
            Record(workspaceId, done.Title, done.Peer, done.Report, await ChangedFiles(done.TaskId));
            prompts.Unshare(workspaceId, Key(id));
            await coordinator.Complete(done.TaskId);
            Delete(ResultFolder(done.TaskId));
            return $"The result of the delegated task \"{done.Title}\" is brought in and the task was removed.";
        }
        finally
        {
            deciding.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Continue(Guid taskId, string? name, CancellationToken cancellation)
    {
        if (!continuing.TryAdd(taskId, 0))
        {
            throw new RequestException("The task is being continued already.");
        }

        try
        {
            (Guid WorkspaceId, string Title, string Peer, string? Report, string Base, string Result, bool BringIn, bool IsAsked) task;
            await deciding.WaitAsync(cancellation);

            try
            {
                SwarmTaskState state = await book.Read(document => Find(document, taskId).State);

                if (state != SwarmTaskState.Done)
                {
                    throw new RequestException("Only a finished task can be continued.");
                }

                if (await book.Read(document => Find(document, taskId).TurnId is not null))
                {
                    throw new RequestException("The task is being brought in. Cancel that first, or let it finish.");
                }

                task = await book.Read(document => Find(document, taskId) is var found ? (found.WorkspaceId, found.Title, found.PeerName, found.Report, found.BaseCommit, found.ResultCommit ?? found.BaseCommit, found.BringIn, found.IsAsked) : default);
                RequireOpen(task.WorkspaceId);
                // Held back, so it is not brought in while the session is made.
                await book.Change(document =>
                {
                    SwarmOutgoing held = Find(document, taskId);
                    held.BringIn = false;
                    held.IsAsked = false;
                });
                await Drop(taskId);
            }
            finally
            {
                deciding.Release();
            }

            WorkspaceInfo session;

            try
            {
                WorkspaceInfo origin = await workspaces.Get(task.WorkspaceId, cancellation);
                Guid main = origin.Side?.ParentId ?? origin.Id;
                string chosen = string.IsNullOrWhiteSpace(name) ? FreeName(main, task.Title) : name;
                string report = $"This session goes on with the delegated task \"{task.Title}\"{(task.Peer.Length > 0 ? $", done on {task.Peer}" : string.Empty)}. Its files are as the task left them.\n\n{task.Report ?? "The task made no report."}";
                session = await sideSessions.Continue(origin.Id, chosen, new SideSessionContinuation { BaseCommit = task.Base, ResultCommit = task.Result, Report = report }, cancellation);
                following()?.Tell(session.Id, $"{report}\n\n(This report is shown in the chat of this session.)");
                following()?.Tell(origin.Id, $"The user goes on with the delegated task \"{task.Title}\" in a side session of its own, so it is not to be brought into this workspace.");
            }
            catch
            {
                await book.Change(document =>
                {
                    if (document.Outgoing.FirstOrDefault(entry => entry.Id == taskId) is { } held)
                    {
                        held.BringIn = task.BringIn;
                        held.IsAsked = task.IsAsked;
                    }
                });
                Wake();
                throw;
            }

            await coordinator.Complete(taskId);
            return session;
        }
        finally
        {
            continuing.TryRemove(taskId, out _);
        }
    }

    /// <inheritdoc />
    public async Task Withdraw(Guid taskId)
    {
        await deciding.WaitAsync();

        try
        {
            if (await book.Read(document => document.Outgoing.FirstOrDefault(task => task.Id == taskId && task.State == SwarmTaskState.Done)?.TurnId) is { } turn)
            {
                await CancelWaiting(turn);
            }

            // Also one the AI is bringing in right now: with the task gone there is nothing left to confirm.
            if (await book.Read<(Guid, string)?>(document => document.Outgoing.FirstOrDefault(task => task.Id == taskId && task.State == SwarmTaskState.Done && task.BringInId is not null) is { } task ? (task.WorkspaceId, task.BringInId!) : null) is (Guid workspace, string bringInId))
            {
                prompts.Unshare(workspace, Key(bringInId));
                Delete(ResultFolder(taskId));
            }
        }
        finally
        {
            deciding.Release();
        }
    }

    /// <inheritdoc />
    public async Task Forget(Guid workspaceId)
    {
        await deciding.WaitAsync();

        try
        {
            foreach (Guid turn in await book.Read(document => document.Turns.Where(turn => turn.WorkspaceId == workspaceId).Select(turn => turn.Id).ToList()))
            {
                await CancelWaiting(turn);
                await book.Change(document => document.Turns.RemoveAll(entry => entry.Id == turn));
            }

            foreach (Guid task in await book.Read(document => document.Outgoing.Where(task => task.WorkspaceId == workspaceId).Select(task => task.Id).ToList()))
            {
                await book.Change(document => Find(document, task).TurnId = null, false);
                await Drop(task);
            }
        }
        finally
        {
            deciding.Release();
        }
    }

    // Called by the book while nothing else can change it, so what waits is known the moment it does.
    private void Look(SwarmDocument document)
    {
        Dictionary<Guid, List<BringInItem>> waiting = [];

        foreach (Guid workspace in document.Outgoing.Select(task => task.WorkspaceId).Distinct())
        {
            List<SwarmOutgoing> results = [.. document.Outgoing.Where(task => task.WorkspaceId == workspace && document.IsToBringIn(task)).OrderBy(task => task.CreatedAt)];
            List<BringInItem> items = [.. results.Select(task => new BringInItem(task.Id, $"Bring in {task.Title}", task.CommitBringIn))];

            foreach (SwarmJob job in document.DueReports(workspace).Where(job => results.All(task => task.JobId != job.Id)))
            {
                List<SwarmOutgoing> tasks = [.. document.Outgoing.Where(task => task.JobId == job.Id).OrderBy(task => task.CreatedAt)];
                items.Add(new BringInItem(tasks[0].Id, tasks.Count == 1 ? $"Delegated task ended: {tasks[0].Title}" : $"{tasks.Count} delegated tasks ended", tasks.Any(task => task.CommitBringIn)));
            }

            if (items.Count > 0)
            {
                waiting[workspace] = items;
            }
        }

        pending = waiting;
        unconfirmedTurns = [.. document.Outgoing.Where(task => task is { BringIn: true, TurnId: not null }).Select(task => task.TurnId!.Value)];
    }

    private string? Shortfall(Guid turnId) => unconfirmedTurns.Contains(turnId) ? unconfirmed : null;

    private SwarmOutgoing Find(SwarmDocument document, Guid taskId) => document.Outgoing.FirstOrDefault(task => task.Id == taskId) ?? throw new RequestException("There is no such delegated task.");

    private string ResultFolder(Guid taskId) => Path.Combine(book.Folder, "results", $"{taskId:N}");

    private string Key(string bringInId) => $"swarm-result-{bringInId}";

    private void RequireOpen(Guid workspaceId)
    {
        if (workspaces.GetSide(workspaceId) is { State: not SideSessionState.Ready })
        {
            throw new RequestException("The side session of this task is being set up, brought into main or removed.");
        }
    }

    private void Wake()
    {
        if (signal.CurrentCount == 0)
        {
            signal.Release();
        }
    }

    private void Follow(RemoteEvent remoteEvent)
    {
        if (remoteEvent.Kind is RemoteEventKind.StateChanged or RemoteEventKind.SwarmChanged or RemoteEventKind.LimitPauseChanged)
        {
            Wake();
        }
    }

    private async Task Pump(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await signal.WaitAsync(Interval, cancellation);

                foreach (Guid workspace in pending.Keys.ToList())
                {
                    try
                    {
                        await Deliver(workspace);
                    }
                    catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                    {
                        log.Write($"Results of delegated tasks could not be given to their workspace: {exception.Message}");
                    }
                }

                try
                {
                    await Tend();
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                {
                    log.Write($"The turns of delegated results could not be looked after: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    // A turn can be over without being told, as when other work took the place of one that waited.
    private async Task Tend()
    {
        if (following() is not { } followed)
        {
            return;
        }

        foreach ((Guid id, Guid workspace) in await book.Read(document => document.Turns.Select(turn => (turn.Id, turn.WorkspaceId)).ToList()))
        {
            FollowState state = followed.Describe(workspace);

            if (sending.ContainsKey(workspace) || state.Step?.Id == id || state.Retry?.Id == id || state.IsCommitting || state.IsCommitDue || activity.Get(workspace) != WorkspaceState.Ready || prompts.IsBusy(workspace) || queue.Get(workspace) is not null || queue.IsStarting(workspace))
            {
                lost.TryRemove(id, out _);
            }
            else if (DateTimeOffset.UtcNow - lost.GetOrAdd(id, _ => DateTimeOffset.UtcNow) >= Patience)
            {
                lost.TryRemove(id, out _);
                await Ended(id, new WorkEnd(ActivityOutcome.Stopped));
            }
        }
    }

    private bool Due(Guid id) => !notBefore.TryGetValue(id, out DateTimeOffset soonest) || DateTimeOffset.UtcNow >= soonest;

    private (List<Guid> Tasks, Dictionary<Guid, int> Jobs) Plan(SwarmDocument document, Guid workspaceId)
    {
        List<SwarmJob> reports = [.. document.DueReports(workspaceId)];
        // The results of a job that is not told yet come with the news of it, so they are put off together.
        HashSet<Guid> later = [.. reports.Where(job => !Due(job.Id)).Select(job => job.Id)];
        List<Guid> tasks = [.. document.Outgoing.Where(task => task.WorkspaceId == workspaceId && document.IsToBringIn(task) && !later.Contains(task.JobId) && !continuing.ContainsKey(task.Id) && Due(task.Id)).OrderBy(task => task.CreatedAt).Select(task => task.Id)];
        return (tasks, reports.Where(job => !later.Contains(job.Id)).ToDictionary(job => job.Id, job => job.Revision));
    }

    private async Task Deliver(Guid workspaceId)
    {
        if (sending.ContainsKey(workspaceId) || following() is not { } followed || !followed.MayBringIn(workspaceId) || guard?.IsPaused(workspaceId) == true || (await book.Read(document => Plan(document, workspaceId))) is { Tasks.Count: 0, Jobs.Count: 0 })
        {
            return;
        }

        WorkspaceInfo workspace;

        try
        {
            workspace = await workspaces.Get(workspaceId, stopping);
        }
        catch (RequestException)
        {
            return;
        }

        // Nothing but the user may give a workspace work past a usage limit.
        if (workspace.Side is { State: not SideSessionState.Ready } || guard is not null && await guard.Hold(workspaceId, workspace.Service, stopping))
        {
            return;
        }

        SwarmTurn turn;
        await deciding.WaitAsync(stopping);

        try
        {
            (List<Guid> wanted, Dictionary<Guid, int> jobs) = await book.Read(document => Plan(document, workspaceId));
            Dictionary<Guid, IReadOnlyList<string>> ready = [];

            foreach (Guid task in wanted)
            {
                if (await Prepare(workspace, task) is { } changes)
                {
                    ready[task] = changes;
                }
            }

            if (ready.Count == 0 && jobs.Count == 0)
            {
                return;
            }

            turn = new SwarmTurn { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Tasks = [.. ready.Keys], Jobs = jobs };
            Dictionary<Guid, string> ids = await book.Read(document => ready.Keys.ToDictionary(id => id, id => Find(document, id).BringInId ?? Guid.NewGuid().ToString("N")[..12]));

            await book.Read(document =>
            {
                List<SwarmOutgoing> results = [.. document.Outgoing.Where(task => ready.ContainsKey(task.Id)).OrderBy(task => task.CreatedAt)];
                List<SwarmOutgoing> told = [.. document.Outgoing.Where(task => jobs.ContainsKey(task.JobId)).OrderBy(task => task.CreatedAt)];
                turn.Source = jobs.Count > 0 || results.Any(task => !task.IsManual) ? ActivitySource.DelegatedResults : ActivitySource.BringIn;
                turn.Commit = (results.Count > 0 ? results : told).Any(task => task.CommitBringIn);
                turn.Text = Describe(document, workspace, results, told, ids, ready);
                return true;
            });

            // Before the book changes, so there is no moment in which nothing says the turn is coming.
            sending[workspaceId] = turn.Id;
            await book.Change(document =>
            {
                document.Turns.Add(turn);

                foreach (SwarmOutgoing task in document.Outgoing.Where(task => ready.ContainsKey(task.Id)))
                {
                    task.TurnId = turn.Id;
                    task.BringInId = ids[task.Id];
                    task.Deliveries++;
                    task.Error = null;
                    task.UpdatedAt = DateTimeOffset.UtcNow;
                }
            });

            foreach ((Guid task, string id) in ids)
            {
                prompts.Share(workspaceId, Key(id), [ResultFolder(task)]);
            }
        }
        catch
        {
            sending.TryRemove(workspaceId, out _);
            throw;
        }
        finally
        {
            deciding.Release();
        }

        foreach (Guid task in turn.Tasks)
        {
            book.AnnounceTask(task);
        }

        // Apart from the pump: starting an AI can take long, and other workspaces must not wait for it.
        _ = Task.Run(() => Send(followed, turn), CancellationToken.None);
    }

    private async Task<IReadOnlyList<string>?> Prepare(WorkspaceInfo workspace, Guid taskId)
    {
        (string title, string start, string result, string? report) = await book.Read(document => Find(document, taskId) is var task ? (task.Title, task.BaseCommit, task.ResultCommit!, task.Report) : default);
        string folder = ResultFolder(taskId);

        try
        {
            // The report is written last, so a copy that has it is whole.
            if (File.Exists(Path.Combine(folder, "report.md")))
            {
                return await ChangedFiles(taskId);
            }

            IReadOnlyList<string> changes = await repository.Materialize(workspace.Folder, folder, start, result, $"# {title}\n\n{report ?? "The task made no report."}", stopping);
            unprepared.TryRemove(taskId, out _);
            return changes;
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
            if (unprepared.AddOrUpdate(taskId, 1, (_, before) => before + 1) < preparationLimit)
            {
                notBefore[taskId] = DateTimeOffset.UtcNow + RetryDelay;
            }
            else
            {
                await GiveUp(taskId, workspace.Id, title, $"Its result could not be written out: {exception.Message}");
            }

            return null;
        }
    }

    private async Task<IReadOnlyList<string>> ChangedFiles(Guid taskId)
    {
        string list = Path.Combine(ResultFolder(taskId), "changes.txt");
        return File.Exists(list) ? [.. (await File.ReadAllTextAsync(list)).Split('\n', StringSplitOptions.RemoveEmptyEntries)] : [];
    }

    private string Describe(SwarmDocument document, WorkspaceInfo workspace, IReadOnlyList<SwarmOutgoing> results, IReadOnlyList<SwarmOutgoing> told, IReadOnlyDictionary<Guid, string> ids, IReadOnlyDictionary<Guid, IReadOnlyList<string>> changes)
    {
        StringBuilder text = new();
        string Names(IReadOnlyList<SwarmOutgoing> tasks) => string.Join(", ", tasks.Select(task => $"\"{task.Title}\""));
        text.Append(told.Count > 0
            ? $"[Auto Remote] Your delegated tasks have ended: {Names(told)}."
            : $"[Auto Remote] Bring in the result{(results.Count == 1 ? string.Empty : "s")} of {(results.Count == 1 ? "the delegated task" : "delegated tasks")} {Names(results)}.");

        if (told.Count > 0)
        {
            text.Append('\n');
        }

        foreach (SwarmOutgoing task in told)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- Task {task.Id:N} \"{task.Title}\" on {(task.PeerName.Length > 0 ? task.PeerName : "no worker")}: {task.State}{(task.Error is { } error ? $" ({error})" : string.Empty)}");

            if (ids.ContainsKey(task.Id))
            {
                text.Append(". Its result is below, to bring in.");
                continue;
            }

            text.Append(task switch
            {
                { State: SwarmTaskState.Waiting } => " (it waits on a lost task; it runs if that one comes back)",
                { State: SwarmTaskState.Done, ResultCommit: null } => ". It changed no files.",
                { State: SwarmTaskState.Done, BringIn: false } => ". Its result is kept apart for the user to decide about, so do not bring it in; review_task shows it.",
                { State: SwarmTaskState.Done } => ". Its result is brought in separately; leave it for then.",
                { Branch: { } partial } => $". What it changed before it ended is on the branch {partial}, and review_task shows it. It is not a finished result.",
                _ => string.Empty
            });

            if (!string.IsNullOrWhiteSpace(task.Report))
            {
                text.Append(CultureInfo.InvariantCulture, $"\n  Report: {Cut(task.Report, summaryLimit).Replace("\n", "\n  ", StringComparison.Ordinal)}");
            }
        }

        if (results.Count > 0)
        {
            text.Append(results.Count == 1 ? "\n\nResult to bring into this workspace:" : $"\n\nResults to bring into this workspace ({results.Count}); decide how to combine them:");
        }

        foreach (SwarmOutgoing task in results)
        {
            IReadOnlyList<string> changed = changes[task.Id];
            text.Append(CultureInfo.InvariantCulture, $"\n\n## \"{task.Title}\" (integration {ids[task.Id]})\n\nTask {task.Id:N}{(task.PeerName.Length > 0 ? $", done on {task.PeerName}" : string.Empty)}. Its folder is {ResultFolder(task.Id)}: files holds all files as the task left them, changes.txt lists what it changed against the code it started from, changes.diff is that change as a diff and report.md is its report.");
            IReadOnlyList<SwarmIncludedTask> included = task.Dependencies.Included();

            if (included.Count > 0)
            {
                string Where(SwarmIncludedTask other) => ids.TryGetValue(other.TaskId, out string? id) ? $"integration {id}, also here" : document.Outgoing.Any(entry => entry.Id == other.TaskId) ? "which is not part of this request" : "which is not waiting here any more";
                text.Append(CultureInfo.InvariantCulture, $"\n\nThis task was given the results of these tasks to combine with its own work, so its changes should already include theirs: {string.Join("; ", included.Select(other => $"\"{other.Title}\" ({Where(other)})"))}. Check that, and do not bring the same change in twice; confirm each of them that is listed here once its changes are in.");
            }

            if (!string.IsNullOrWhiteSpace(task.Report))
            {
                text.Append(CultureInfo.InvariantCulture, $"\n\nReport:\n{Cut(task.Report, summaryLimit)}");
            }

            text.Append(CultureInfo.InvariantCulture, $"\n\nFiles it changed:\n{string.Join('\n', changed.Take(listedChanges))}{(changed.Count > listedChanges ? $"\n(and {changed.Count - listedChanges} more in changes.txt)" : string.Empty)}");
        }

        if (results.Count > 0)
        {
            text.Append($"""


                How to bring {(results.Count == 1 ? "it" : "them")} in:
                - Your file tools can read the folders above but not change them; do not change them with the shell either. Change only this workspace, {workspace.Folder}.
                - A task started from the files this workspace had when it was delegated, which may have changed since. Go by its change list and diff, and bring over all of its changes and its intent, adapted to the workspace as it is now. Keep newer work: copying whole files over could undo it.
                - Check the changes yourself rather than trusting the report, and run the tests that fit when you can.
                - How you combine is up to you: use git or edit the files, whichever you see fit. Decide conflicts yourself without asking. Do not commit: Auto Remote commits when your turn ends, if the user set it to.
                - When all of a result is brought over, call finish_integration with its integration id, which removes that task for good. Do not call it for one you could not bring over; call report_unfinished with the reason.
                """.ReplaceLineEndings("\n"));
        }

        if (told.Any(task => task.State != SwarmTaskState.Done))
        {
            text.Append("\n\nDeal with the tasks that did not succeed as your work needs: do that part yourself, delegate it again, or say what is left.");
        }

        return text.ToString();
    }

    private async Task Send(IFollowUps followed, SwarmTurn turn)
    {
        Guid id = turn.WorkspaceId;
        WorkStep step = new() { Id = turn.Id, Source = turn.Source, Text = turn.Text, Commit = turn.Commit };

        try
        {
            if (!await book.Read(document => document.Turns.Any(entry => entry.Id == turn.Id)))
            {
                return;
            }

            if (!followed.TryExpect(id, step, null, () => Shortfall(turn.Id)))
            {
                // Something else is on its way to the workspace.
                await Revert(turn.Id, RetryDelay);
                return;
            }

            WorkspaceInfo workspace = await workspaces.Get(id, stopping);

            if (workspace.Side is not null && reopen is not null)
            {
                await reopen(id);
            }

            // Turned on only once the turn has the workspace, so work that got there first keeps its own setting.
            void Taken()
            {
                if (turn.Commit)
                {
                    followed.Arm(id);
                }
            }

            Task<bool> beginning = Task.FromResult(true);

            if (workspace.Service == AiService.Local)
            {
                beginning = (await queue.SubmitWhenFree(id, turn.Text, WorkSource.Integration, Taken, stopping)).Started;
            }
            else
            {
                await prompts.PromptWhenIdle(id, turn.Text, Taken, stopping);
            }

            if (beginning is { IsCompletedSuccessfully: true, Result: true })
            {
                await Begun(turn.Id);
            }

            followed.Sent(id, turn.Id);
            sending.TryRemove(new KeyValuePair<Guid, Guid>(id, turn.Id));

            // It was cancelled while it was on its way.
            if (!await book.Read(document => document.Turns.Any(entry => entry.Id == turn.Id)))
            {
                if (queue.Get(id) is not null)
                {
                    await queue.Withdraw(id, turn.Text);
                }

                return;
            }

            if (await beginning.WaitAsync(stopping))
            {
                await Begun(turn.Id);
            }
            else
            {
                // The scheduler hears why it never started, which counts for more than that it did not.
                await Task.Delay(TimeSpan.FromMilliseconds(500), stopping);
                await Ended(turn.Id, new WorkEnd(ActivityOutcome.Stopped));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !stopping.IsCancellationRequested)
        {
            followed.NotSent(id, turn.Id);
            log.Write($"Results of delegated tasks could not be sent to their workspace: {exception.Message}");
            // Each attempt that reaches the AI leaves the message in the chat, so failures are tried ever more rarely.
            await Revert(turn.Id, Pause(id));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sending.TryRemove(new KeyValuePair<Guid, Guid>(id, turn.Id));
            Wake();
        }
    }

    private TimeSpan Pause(Guid workspaceId)
    {
        int before = unavailable.AddOrUpdate(workspaceId, 1, (_, count) => count + 1) - 1;
        double wait = RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(before, 20));
        return TimeSpan.FromMilliseconds(Math.Min(wait, longestPause.TotalMilliseconds));
    }

    // The news of a job counts as given once the turn that carries it started, so a restart before that gives it again.
    private async Task Begun(Guid turnId)
    {
        await deciding.WaitAsync();

        try
        {
            if (await book.Read(document => document.Turns.Any(turn => turn.Id == turnId && !turn.IsStarted)))
            {
                await book.Change(document =>
                {
                    SwarmTurn turn = document.Turns.First(turn => turn.Id == turnId);
                    turn.IsStarted = true;
                    Report(document, turn, true);
                }, false);
            }
        }
        finally
        {
            deciding.Release();
        }
    }

    private void Report(SwarmDocument document, SwarmTurn turn, bool told)
    {
        foreach (SwarmJob job in document.Jobs.Where(job => turn.Jobs.TryGetValue(job.Id, out int revision) && job.Revision == revision))
        {
            job.IsReported = told;
        }
    }

    // A turn that was never had: everything is as before it, to be asked for again.
    private async Task Revert(Guid turnId, TimeSpan wait)
    {
        await deciding.WaitAsync();

        try
        {
            if (await book.Read(document => document.Turns.FirstOrDefault(turn => turn.Id == turnId)) is not { } turn)
            {
                return;
            }

            Delay([.. turn.Tasks, .. turn.Jobs.Keys], wait);
            await book.Change(document => Take(document, turnId));
        }
        finally
        {
            deciding.Release();
        }
    }

    private void Delay(IReadOnlyList<Guid> ids, TimeSpan wait)
    {
        foreach (Guid id in ids)
        {
            notBefore[id] = DateTimeOffset.UtcNow + wait;
        }
    }

    private void Take(SwarmDocument document, Guid turnId)
    {
        if (document.Turns.FirstOrDefault(turn => turn.Id == turnId) is not { } turn)
        {
            return;
        }

        if (!turn.IsStarted)
        {
            Report(document, turn, false);
        }

        foreach (SwarmOutgoing task in document.Outgoing.Where(task => task.TurnId == turnId))
        {
            task.TurnId = null;
            task.Deliveries = Math.Max(0, task.Deliveries - 1);
        }

        document.Turns.Remove(turn);
    }

    // Takes back a turn the AI has not taken yet, which waits for the local AI. Called while deciding.
    private async Task CancelWaiting(Guid turnId)
    {
        if (await book.Read(document => document.Turns.FirstOrDefault(turn => turn.Id == turnId)) is not { IsStarted: false } turn || queue.Get(turn.WorkspaceId) is null)
        {
            return;
        }

        await book.Change(document => Take(document, turnId));
        prompts.Withdraw(turn.WorkspaceId, turn.Text);
        await queue.Withdraw(turn.WorkspaceId, turn.Text);
    }

    private void Worked(Guid workspaceId, WorkStep step, WorkEnd end) => _ = Task.Run(async () =>
    {
        try
        {
            await Ended(step.Id, end);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or RequestException)
        {
            log.Write($"Results of delegated tasks could not be moved on: {exception.Message}");
        }
    }, CancellationToken.None);

    // The turn is over: what it did not confirm is asked for again, or left with its task.
    private async Task Ended(Guid turnId, WorkEnd end)
    {
        List<(Guid Id, Guid Workspace, string Title)> given = [];
        List<Guid> changed = [];
        await deciding.WaitAsync();

        try
        {
            if (await book.Read(document => document.Turns.FirstOrDefault(turn => turn.Id == turnId)) is not { } turn)
            {
                return;
            }

            // A turn the AI never really had, or that the user stopped, does not count against anything.
            bool counts = !end.CouldNotRun && end.Outcome is not (ActivityOutcome.Stopped or ActivityOutcome.Waiting);
            bool gaveUp = end.Outcome == ActivityOutcome.Unfinished && end.Error != unconfirmed;
            bool told = !end.CouldNotRun && (turn.IsStarted || end.Outcome is not (ActivityOutcome.Stopped or ActivityOutcome.Waiting));
            TimeSpan wait = end.CouldNotRun ? Pause(turn.WorkspaceId) : RetryDelay;

            if (!end.CouldNotRun)
            {
                unavailable.TryRemove(turn.WorkspaceId, out _);
            }

            Delay([.. turn.Tasks, .. turn.Jobs.Keys], wait);
            await book.Change(document =>
            {
                Report(document, turn, told);

                foreach (SwarmOutgoing task in document.Outgoing.Where(task => task.TurnId == turnId))
                {
                    task.TurnId = null;
                    task.UpdatedAt = DateTimeOffset.UtcNow;
                    changed.Add(task.Id);

                    if (task.BringIn && counts && (gaveUp || task.Deliveries >= deliveryLimit))
                    {
                        given.Add((task.Id, task.WorkspaceId, task.Title));
                    }
                    else if (!counts)
                    {
                        task.Deliveries = Math.Max(0, task.Deliveries - 1);
                    }
                }

                document.Turns.RemoveAll(entry => entry.Id == turnId);
            });

            foreach ((Guid id, Guid workspace, string title) in given)
            {
                await GiveUp(id, workspace, title, end.Error ?? unconfirmed);
            }

            // What the user turned off while the AI had it is kept apart now.
            foreach (Guid id in await book.Read(document => document.Outgoing.Where(task => changed.Contains(task.Id) && !task.BringIn).Select(task => task.Id).ToList()))
            {
                await Drop(id);
            }
        }
        finally
        {
            deciding.Release();
        }

        foreach (Guid id in changed)
        {
            book.AnnounceTask(id);
        }

        Wake();
    }

    // The task keeps its result and is not brought in again until the user asks.
    private async Task GiveUp(Guid taskId, Guid workspaceId, string title, string reason)
    {
        await book.Change(document =>
        {
            if (document.Outgoing.FirstOrDefault(task => task.Id == taskId) is { } task)
            {
                task.BringIn = false;
                task.IsAsked = false;
                task.Error = $"Needs attention: it was not brought in. {reason}";
                task.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
        unprepared.TryRemove(taskId, out _);
        await Drop(taskId);
        following()?.Tell(workspaceId, $"Bringing in the result of the delegated task \"{title}\" was not confirmed, so it stays with the task. This workspace may hold part of its changes.");
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = workspaceId, Time = DateTimeOffset.UtcNow, Error = $"The result of the delegated task '{title}' was not brought in and stays with the task: {reason}", IsNotice = true });
    }

    // Ends the access to the copy of a result that has no turn and removes the copy. Called while deciding.
    private async Task Drop(Guid taskId)
    {
        (Guid Workspace, string BringIn)? shared = await book.Read<(Guid, string)?>(document => document.Outgoing.FirstOrDefault(task => task.Id == taskId && task.TurnId is null && task.BringInId is not null) is { } task ? (task.WorkspaceId, task.BringInId!) : null);

        if (shared is { } ended)
        {
            prompts.Unshare(ended.Workspace, Key(ended.BringIn));
            await book.Change(document =>
            {
                SwarmOutgoing task = Find(document, taskId);
                task.BringInId = null;
                task.Deliveries = 0;
            }, false);
        }

        if (await book.Read(document => document.Outgoing.All(task => task.Id != taskId || task.TurnId is null)))
        {
            Delete(ResultFolder(taskId));
        }
    }

    private void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write($"The copy of a delegated result was left in '{folder}': {exception.Message}");
        }
    }

    // Copies a stop of the server or a removal left behind.
    private void Tidy(IReadOnlyList<string> kept)
    {
        string root = Path.Combine(book.Folder, "results");

        if (Directory.Exists(root))
        {
            foreach (string folder in Directory.GetDirectories(root).Except(kept, StringComparer.Ordinal).ToList())
            {
                Delete(folder);
            }
        }
    }

    // What the task said about its work stays with the activity of the workspace it went into, since the task goes.
    private void Record(Guid workspaceId, string title, string peer, string? report, IReadOnlyList<string> files)
    {
        if (journal is null || following()?.Describe(workspaceId).Step is not { } step)
        {
            return;
        }

        journal.Change(workspaceId, step.Id, entry => entry with
        {
            Handoff = string.Join("\n\n", new[] { entry.Handoff, $"{title}: {Cut(report ?? "The task made no report.", summaryLimit)}" }.Where(part => !string.IsNullOrEmpty(part))),
            Files = [.. entry.Files, .. files.Take(listedChanges).Select(file => $"{title}: {file}")]
        });
        journal.Note(workspaceId, step.Id, $"Brought in the delegated task \"{title}\"{(peer.Length > 0 ? $" done on {peer}" : string.Empty)}.");
    }

    private string FreeName(Guid mainId, string title)
    {
        string name = new string([.. title.Select(character => char.IsControl(character) ? ' ' : character)]).Trim();
        name = name.Length == 0 ? "task" : name[..Math.Min(name.Length, nameLimit - 4)].TrimEnd();
        string chosen = name;

        for (int number = 2; chosen.Equals("main", StringComparison.OrdinalIgnoreCase) || workspaces.Sides(mainId).Any(entry => entry.Side.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase)); number++)
        {
            chosen = $"{name} {number}";
        }

        return chosen;
    }

    private string Cut(string text, int limit) => text.Length > limit ? $"{text[..limit]}..." : text;
}
