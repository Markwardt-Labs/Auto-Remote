namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Brings side sessions into their main workspace: the main AI is asked at once, with read access to the session's files and changes, and finishes with a tool, which removes the session. Until then the session stays locked; it can be cancelled, which gives it back. A request that was not finished is given once more after a restart, a failure or work that ended without finishing; after that, or after the work was stopped, it goes along with main's next work.
/// </summary>
internal interface ISideIntegrations
{
    /// <summary>
    /// Takes up the integrations that were under way when the server stopped.
    /// </summary>
    /// <param name="cancellation">Cancels the work when the server stops.</param>
    /// <returns>A task that completes once they are taken up.</returns>
    Task Open(CancellationToken cancellation);

    /// <summary>
    /// Starts integrating a side session whose AI, loop, queued work and scripts have stopped.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>A task that completes once the session is locked; the rest happens after.</returns>
    /// <exception cref="RequestException">It is not ready or still runs something.</exception>
    Task Integrate(Guid id);

    /// <summary>
    /// Cancels the integration of a side session, which can be used again. What reached main stays.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>A task that completes once the session is unlocked.</returns>
    /// <exception cref="RequestException">It is not being integrated, or its integration finished first.</exception>
    Task Cancel(Guid id);

    /// <summary>
    /// Finishes an integration, which removes the side session for good. Does nothing when no integration with that identifier is under way, such as one that was cancelled.
    /// </summary>
    /// <param name="mainId">The main workspace whose AI finished it.</param>
    /// <param name="integrationId">The identifier given with the request.</param>
    /// <returns>What happened, for the AI.</returns>
    Task<string> Finish(Guid mainId, string integrationId);

    /// <summary>
    /// Notes that the AI of a side session completed its task, which starts its integration once nothing of it runs when it is set to integrate when finished.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>What happens, for the AI.</returns>
    Task<string> MarkComplete(Guid id);

    /// <summary>
    /// Forgets that a side session's task was completed, since it was given more work.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>A task that completes once done.</returns>
    Task Reopen(Guid id);

    /// <summary>
    /// Sets whether a side session is integrated by itself once its AI marks its task complete.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <param name="enabled">Whether it is.</param>
    /// <returns>A task that completes once set.</returns>
    Task SetWhenFinished(Guid id, bool enabled);

    /// <summary>
    /// Tells whether a request to integrate waits to be given to a workspace's AI, which goes before a loop's next prompt.
    /// </summary>
    /// <param name="mainId">The main workspace.</param>
    /// <returns><see langword="true" /> if one waits.</returns>
    bool IsDelivering(Guid mainId);

    /// <summary>
    /// Cancels every integration into a workspace, which is about to be deleted.
    /// </summary>
    /// <param name="mainId">The main workspace.</param>
    /// <returns>A task that completes once done.</returns>
    Task Forget(Guid mainId);
}

/// <inheritdoc />
internal sealed class SideIntegrations(IWorkspaceManager workspaces, ISideSessions sessions, ISideSessionRepository repository, IPromptService prompts, IWorkQueue queue, IWorkspaceActivity activity, IWorkspaceLoops loops, IScriptRunner scripts, IWindowGuard guard, IWorkspaceSnapshots snapshots, IEventHub events, ILog log, ISwarmService? swarm = null, TimeSpan? interval = null, TimeSpan? retryDelay = null) : ISideIntegrations
{
    private readonly TimeSpan every = interval ?? TimeSpan.FromSeconds(5);

    private readonly TimeSpan retryAfter = retryDelay ?? TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> preparing = new();

    private readonly ConcurrentDictionary<Guid, byte> sending = new();

    // Side sessions whose work was stopped or failed, which takes back what its AI said about being done.
    private readonly ConcurrentDictionary<Guid, byte> abandoned = new();

    private readonly HashSet<string> cancelled = [];

    private readonly Lock handing = new();

    private readonly ConcurrentDictionary<Guid, byte> halted = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> notBefore = new();

    private readonly SemaphoreSlim pumping = new(1, 1);

    private readonly int listedChanges = 200;

    private readonly string handoffQuestion = "Another AI is about to bring this side session's work into the main session. Describe only what was done in this side session since it was made, not what was done in the main session before it: the goal, the decisions that matter, how it was checked and anything to watch out for. Answer with that description alone, in at most 300 words.";

    private CancellationToken stopping;

    /// <inheritdoc />
    public async Task Open(CancellationToken cancellation)
    {
        stopping = cancellation;
        events.Subscribe(Follow);

        // Whether the work that marked its task complete ever ended well cannot be told after a restart.
        foreach ((Guid id, StoredSideSession _) in workspaces.Sides().Where(entry => entry.Side.MarkedComplete))
        {
            await workspaces.UpdateSide(id, current => current with { MarkedComplete = false });
        }

        foreach ((Guid id, StoredSideSession side) in workspaces.Sides().Where(entry => entry.Side is { State: SideSessionState.Integrating, Integration: not null }))
        {
            StoredIntegration integration = side.Integration!;

            if (integration.Request is not { } request)
            {
                _ = Task.Run(() => Prepare(id, integration.Id), CancellationToken.None);
                continue;
            }

            Share(side, integration);
            // The queue restores the work of the user, which a request may have been put together with.
            await queue.Withdraw(side.ParentId, request);

            if (integration.InWork)
            {
                await Change(id, integration.Id, current => current with { InWork = false, WaitsForWork = current.Deliveries >= 2 });
            }
        }

        _ = Task.Run(() => Watch(cancellation), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task Integrate(Guid id)
    {
        string integrationId = Guid.NewGuid().ToString("N")[..12];

        using (await sessions.Lock(id))
        {
            StoredSideSession side = Find(id);

            if (side.State != SideSessionState.Ready)
            {
                throw new RequestException(side.State == SideSessionState.Integrating ? "The side session is already being integrated." : "The side session is not ready.");
            }

            if (Busy(id) is { } reason)
            {
                throw new RequestException(reason);
            }

            // Their results come back to this session, which would be gone by then.
            if (swarm is not null && await swarm.IsDelegating(id))
            {
                throw new RequestException("Tasks the side session delegated are still under way, or their results have not reached its AI yet. Cancel or discard them, or let them finish first.");
            }

            await workspaces.UpdateSide(id, current => current with { State = SideSessionState.Integrating, Integration = new StoredIntegration { Id = integrationId }, MarkedComplete = false, Error = null, UnsentTask = null });

            // A prompt takes the workspace before it looks at the state, so one of the two sees the other.
            if (Busy(id) is { } started)
            {
                await workspaces.UpdateSide(id, current => current with { State = SideSessionState.Ready, Integration = null, MarkedComplete = side.MarkedComplete });
                throw new RequestException(started);
            }
        }

        _ = Task.Run(() => Prepare(id, integrationId), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task Cancel(Guid id)
    {
        StoredSideSession side;
        StoredIntegration integration;

        using (await sessions.Lock(id))
        {
            side = Find(id);
            integration = side is { State: SideSessionState.Integrating, Integration: { } current } ? current : throw new RequestException("The side session is not being integrated.");
            // From here on a finish with this identifier removes nothing.
            await workspaces.UpdateSide(id, entry => entry with { State = SideSessionState.Ready, Integration = null });
        }

        bool onItsWay;

        lock (handing)
        {
            cancelled.Add(integration.Id);
            onItsWay = sending.ContainsKey(id);
        }

        await Withdraw(id, side, integration);

        // A request that is still being sent is dealt with by whoever sends it, once it is known whether it arrived.
        if (!onItsWay && integration.Deliveries > 0)
        {
            await TellToStop(side.ParentId, side.Name, integration.Id);
        }
    }

    /// <inheritdoc />
    public async Task<string> Finish(Guid mainId, string integrationId)
    {
        string id = integrationId.Trim();
        string nothing = "There is no integration with that id under way here; it may have been cancelled. Nothing was removed.";

        if (workspaces.Sides(mainId).FirstOrDefault(entry => entry.Side.Integration?.Id == id) is not { Side: not null } found)
        {
            return nothing;
        }

        using (await sessions.Lock(found.Id))
        {
            if (workspaces.GetSide(found.Id) is not { State: SideSessionState.Integrating, Integration: { } integration } side || integration.Id != id)
            {
                return nothing;
            }

            // Reverting main's work would now throw away the only copy of what the session did.
            snapshots.Protect(mainId, "This work integrated a side session, which cancelling it would lose. Stop it instead.");
            await Withdraw(found.Id, side, integration);

            if (swarm is not null)
            {
                await swarm.Forget(found.Id);
            }

            await sessions.Remove(found.Id);
            return $"The integration is finished and the side session \"{side.Name}\" was removed.";
        }
    }

    /// <inheritdoc />
    public async Task<string> MarkComplete(Guid id)
    {
        if (activity.Get(id) is WorkspaceState.Stopping or WorkspaceState.Canceling)
        {
            return "The work is being stopped, so the task is not marked complete.";
        }

        abandoned.TryRemove(id, out _);

        if (await workspaces.UpdateSide(id, current => current with { MarkedComplete = true }) is not { } side)
        {
            return "This is not a side session.";
        }

        return side.IntegrateWhenFinished ? "Noted. The session is integrated into the main session once everything it runs has stopped; finish your reply now." : "Noted. The user did not set this session to integrate when finished, so it stays as it is.";
    }

    /// <inheritdoc />
    public async Task Reopen(Guid id) => await workspaces.UpdateSide(id, current => current with { MarkedComplete = false, Error = null, UnsentTask = null });

    /// <inheritdoc />
    public async Task SetWhenFinished(Guid id, bool enabled)
    {
        if (await workspaces.UpdateSide(id, current => current with { IntegrateWhenFinished = enabled }) is null)
        {
            throw new RequestException("The side session no longer exists.");
        }

        _ = Task.Run(Pump, CancellationToken.None);
    }

    /// <inheritdoc />
    public bool IsDelivering(Guid mainId) => workspaces.Sides(mainId).Any(entry => entry.Side is { State: SideSessionState.Integrating, Integration: { InWork: false, WaitsForWork: false } });

    /// <inheritdoc />
    public async Task Forget(Guid mainId)
    {
        foreach ((Guid id, StoredSideSession side) in workspaces.Sides(mainId).Where(entry => entry.Side.State == SideSessionState.Integrating))
        {
            try
            {
                await Cancel(id);
            }
            catch (RequestException exception)
            {
                log.Write($"The integration of '{side.Name}' was not cancelled: {exception.Message}");
            }
        }
    }

    private StoredSideSession Find(Guid id) => workspaces.GetSide(id) ?? throw new RequestException("The side session no longer exists.");

    private string? Busy(Guid id)
    {
        if (activity.Get(id) != WorkspaceState.Ready || prompts.IsBusy(id))
        {
            return "The side session is still working.";
        }

        if (loops.Get(id) is not null)
        {
            return "A loop runs in the side session. Cancel it first.";
        }

        if (queue.Get(id) is not null)
        {
            return "Work of the side session waits for the local AI. Cancel it or let it run first.";
        }

        if (guard.IsPaused(id))
        {
            return "The side session is paused at the usage limit and resumes by itself. Let it, or cancel the automatic resume, first.";
        }

        return scripts.IsRunning(id) ? "A script runs in the side session. Stop it first." : null;
    }

    private void Share(StoredSideSession side, StoredIntegration integration)
    {
        if (integration.Folder is { } files)
        {
            prompts.Share(side.ParentId, integration.Id, [side.Folder, files]);
        }
    }

    private async Task Withdraw(Guid id, StoredSideSession side, StoredIntegration integration)
    {
        if (preparing.TryRemove(id, out CancellationTokenSource? running))
        {
            await running.CancelAsync();
        }

        prompts.Unshare(side.ParentId, integration.Id);

        if (integration.Request is { } request)
        {
            prompts.Withdraw(side.ParentId, request);
            await queue.Withdraw(side.ParentId, request);
        }

        try
        {
            repository.RemoveIntegrationFiles(id, side, await sessions.Protected(id));
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
            log.Write($"The integration files of '{side.Name}' were left: {exception.Message}");
        }
    }

    // Changes the integration only while it is still the same one, so nothing of a cancelled one comes back.
    private async Task<StoredIntegration?> Change(Guid id, string integrationId, Func<StoredIntegration, StoredIntegration> change)
        => (await workspaces.UpdateSide(id, current => current.Integration?.Id == integrationId ? current with { Integration = change(current.Integration) } : current))?.Integration is { } updated && updated.Id == integrationId ? updated : null;

    private async Task Prepare(Guid id, string integrationId)
    {
        using CancellationTokenSource running = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        preparing[id] = running;

        try
        {
            if (workspaces.GetSide(id) is not { Integration: { } integration } side || integration.Id != integrationId)
            {
                return;
            }

            string handoff = await Handoff(id, running.Token);
            string files = repository.IntegrationFolder(side.Folder);
            string changes;

            using (await sessions.Lock(id))
            {
                if (workspaces.GetSide(id)?.Integration?.Id != integrationId)
                {
                    return;
                }

                changes = await repository.Export(side.Folder, side.BaseTree, files, handoff, running.Token);
                WorkspaceInfo main = await workspaces.Get(side.ParentId, running.Token);
                string request = Describe(side, integrationId, files, handoff, changes, main);
                StoredIntegration? ready = await Change(id, integrationId, current => current with { Folder = files, Request = request });

                if (ready is not null)
                {
                    Share(side, ready);
                }
            }

            await Pump();
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
            await Fail(id, integrationId, exception.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            preparing.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, running));
        }
    }

    // What could not even be prepared gives the session back, with the reason in main's chat.
    private async Task Fail(Guid id, string integrationId, string reason)
    {
        StoredSideSession? side;

        using (await sessions.Lock(id))
        {
            side = workspaces.GetSide(id);

            if (side?.Integration?.Id != integrationId)
            {
                return;
            }

            await workspaces.UpdateSide(id, current => current with { State = SideSessionState.Ready, Integration = null, Error = $"The integration could not start: {reason}" });
        }

        prompts.Unshare(side.ParentId, integrationId);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = side.ParentId, Time = DateTimeOffset.UtcNow, Error = $"The integration of the side session '{side.Name}' could not start: {reason}", IsNotice = true });
    }

    private async Task<string> Handoff(Guid id, CancellationToken cancellation)
    {
        try
        {
            await using (await queue.Reserve(id, cancellation))
            {
                return await prompts.Ask(id, handoffQuestion, cancellation);
            }
        }
        catch (RequestException exception)
        {
            return $"No handoff could be made ({exception.Message}). Go by the changes.";
        }
    }

    private string Describe(StoredSideSession side, string integrationId, string files, string handoff, string changes, WorkspaceInfo main)
    {
        string[] lines = changes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string listed = lines.Length == 0 ? "None that git tracks or would track." : string.Join('\n', lines.Take(listedChanges)) + (lines.Length > listedChanges ? $"\n(and {lines.Length - listedChanges} more in changes.txt)" : string.Empty);
        return $"""
            Integrate the side session "{side.Name}" into this workspace (integration {integrationId}).

            Its files are in {side.Folder}. Its change list and diff against the files it started with are in {files} (changes.txt, changes.diff and handoff.md). Your file tools can read both folders but not change them; do not change them with the shell either. Change only this workspace, {main.Folder}.

            Handoff from the side session:
            {handoff.Trim()}

            Files the session changed:
            {listed}

            Bring over all of its changes and its intent, adapted to this workspace as it is now:
            - Go by the change list. Other differences between the two folders may be newer work in this workspace, which must be kept.
            - Review the code you copy against this workspace, which may have changed what it depends on.
            - Copy files git ignores that matter; they are not in the change list.
            - Decide conflicts yourself without asking, and run the tests that fit when you can.
            - Do not commit or merge as part of this.
            This joins any work you are doing now. When it is done, call finish_integration with integration_id "{integrationId}" and then say what you did, including anything you adapted or left out.
            """.ReplaceLineEndings("\n").Trim();
    }

    private async Task Watch(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(every, cancellation);
                await Pump();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Gives out the requests whose workspace can take them, and starts the integrations of finished side sessions.
    private async Task Pump()
    {
        await pumping.WaitAsync();

        try
        {
            foreach ((Guid id, StoredSideSession side) in workspaces.Sides().ToList())
            {
                if (side is { State: SideSessionState.Ready, MarkedComplete: true } && Busy(id) is null)
                {
                    // Looked at only once the session is idle, which it is after what stopped or failed was heard.
                    if (abandoned.TryRemove(id, out _))
                    {
                        await workspaces.UpdateSide(id, current => current with { MarkedComplete = false });
                        continue;
                    }

                    if (!side.IntegrateWhenFinished)
                    {
                        continue;
                    }

                    try
                    {
                        await Integrate(id);
                    }
                    catch (RequestException)
                    {
                    }
                }
                else if (side is { State: SideSessionState.Integrating, Integration: { Request: not null, InWork: false } integration } && !sending.ContainsKey(id))
                {
                    await Deliver(id, side, integration);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            log.Write($"Integrations could not be moved on: {exception.Message}");
        }
        finally
        {
            pumping.Release();
        }
    }

    private async Task Deliver(Guid id, StoredSideSession side, StoredIntegration integration)
    {
        WorkspaceState state = activity.Get(side.ParentId);

        if (state is not (WorkspaceState.Ready or WorkspaceState.Working) || integration.WaitsForWork && state == WorkspaceState.Ready || guard.IsPaused(side.ParentId) || notBefore.TryGetValue(id, out DateTimeOffset soonest) && DateTimeOffset.UtcNow < soonest)
        {
            return;
        }

        // Starting new work is held to the usage limit, as a loop's prompt is; joining work in progress is not new work.
        if (state == WorkspaceState.Ready && await guard.Hold(side.ParentId, side.Service, stopping))
        {
            return;
        }

        sending[id] = 0;

        if (await Change(id, integration.Id, current => current with { InWork = true, WaitsForWork = false, Deliveries = current.Deliveries + 1 }) is null)
        {
            sending.TryRemove(id, out _);
            return;
        }

        // Sent apart from the pump: starting an AI can take long, and other workspaces must not wait for it.
        _ = Task.Run(() => Send(id, side.ParentId, integration.Id, integration.Request!), CancellationToken.None);
    }

    private async Task Send(Guid id, Guid mainId, string integrationId, string request)
    {
        string name = workspaces.GetSide(id)?.Name ?? string.Empty;
        bool started = false;
        bool dropped;

        try
        {
            if (workspaces.GetSide(id)?.Integration?.Id != integrationId)
            {
                return;
            }

            WorkTicket ticket = await queue.Submit(mainId, request, WorkSource.Integration, stopping);

            // A cancellation while it was on its way takes it out of the queue again.
            if (IsCancelled(integrationId))
            {
                prompts.Withdraw(mainId, request);
                await queue.Withdraw(mainId, request);
            }

            started = !ticket.Queued || await ticket.Started.WaitAsync(stopping);

            if (!started)
            {
                // The queued work was dropped, which the user did; it goes along with what main does next.
                await Change(id, integrationId, current => current with { InWork = false, WaitsForWork = true });
            }
        }
        catch (RequestException exception)
        {
            log.Write($"A request to integrate could not be sent: {exception.Message}");
            notBefore[id] = DateTimeOffset.UtcNow + retryAfter;
            await Change(id, integrationId, current => current with { InWork = false, WaitsForWork = current.Deliveries >= 2 });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (handing)
            {
                sending.TryRemove(id, out _);
                dropped = cancelled.Contains(integrationId);
            }

            if (dropped && started)
            {
                await TellToStop(mainId, name, integrationId);
            }
        }

        // Work can end before this notices that the request is part of it.
        if (started && !dropped && !prompts.IsBusy(mainId) && activity.Get(mainId) == WorkspaceState.Ready)
        {
            await Ended(mainId, halted.ContainsKey(mainId));
        }
    }

    private bool IsCancelled(string integrationId)
    {
        lock (handing)
        {
            return cancelled.Contains(integrationId);
        }
    }

    // Only work under way is told; an AI that already ended its turn is not started again for this.
    private async Task TellToStop(Guid mainId, string name, string integrationId)
    {
        if (!prompts.IsBusy(mainId))
        {
            return;
        }

        try
        {
            await prompts.Prompt(mainId, $"The user cancelled the integration of the side session \"{name}\" (integration {integrationId}). Stop integrating it, leave what you already changed as it is without undoing it, and go on with your other work.", stopping);
        }
        catch (Exception exception) when (exception is RequestException or OperationCanceledException)
        {
            log.Write($"The main AI could not be told that an integration was cancelled: {exception.Message}");
        }
    }

    // Main's work ended: a request that was part of it and was not finished is given once more, unless the work was stopped.
    private async Task Ended(Guid mainId, bool stopped)
    {
        foreach ((Guid id, StoredSideSession side) in workspaces.Sides(mainId).Where(entry => entry.Side is { State: SideSessionState.Integrating, Integration.InWork: true } && !sending.ContainsKey(entry.Id)))
        {
            notBefore[id] = DateTimeOffset.UtcNow + retryAfter;
            await Change(id, side.Integration!.Id, current => current with { InWork = false, WaitsForWork = stopped || current.Deliveries >= 2 });
        }
    }

    private void Follow(RemoteEvent remoteEvent)
    {
        // Noted as it happens, before the session can be seen as idle again.
        if ((remoteEvent.Kind == RemoteEventKind.Failed || remoteEvent.Kind == RemoteEventKind.StateChanged && remoteEvent.State is WorkspaceState.Stopping or WorkspaceState.Canceling) && workspaces.GetSide(remoteEvent.WorkspaceId) is not null)
        {
            abandoned[remoteEvent.WorkspaceId] = 0;
        }

        if (remoteEvent.Kind == RemoteEventKind.StateChanged && remoteEvent.State is { } state)
        {
            Guid mainId = remoteEvent.WorkspaceId;

            if (state is WorkspaceState.Stopping or WorkspaceState.Canceling)
            {
                halted[mainId] = 0;
            }
            else if (state == WorkspaceState.Ready)
            {
                bool stopped = halted.TryRemove(mainId, out _);
                _ = Task.Run(() => Ended(mainId, stopped), CancellationToken.None);
            }

            _ = Task.Run(Pump, CancellationToken.None);
        }
        else if (remoteEvent.Kind is RemoteEventKind.LoopChanged or RemoteEventKind.QueueChanged or RemoteEventKind.ScriptChanged or RemoteEventKind.LimitPauseChanged)
        {
            if (workspaces.GetSide(remoteEvent.WorkspaceId) is { MarkedComplete: true } || remoteEvent.Kind == RemoteEventKind.LimitPauseChanged)
            {
                _ = Task.Run(Pump, CancellationToken.None);
            }
        }
    }
}
