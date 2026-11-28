namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Brings side sessions into their main workspace: the main AI gets a turn of its own for it, with read access to the files and changes of every session that is ready to come in, combines them as it sees fit and confirms each with a tool, which removes that session. Until then a session stays locked; it can be cancelled, which gives it back. One whose bringing in is not confirmed is asked for once more, and then given back as it is.
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
    /// Forgets that a side session's work was done, since it was given more work.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>A task that completes once done.</returns>
    Task Reopen(Guid id);

    /// <summary>
    /// Sets whether the changes are committed after a side session was brought in.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <param name="commit">Whether they are.</param>
    /// <returns>A task that completes once set.</returns>
    Task SetCommit(Guid id, bool commit);

    /// <summary>
    /// Gets what waits to be brought into a workspace.
    /// </summary>
    /// <param name="mainId">The main workspace.</param>
    /// <returns>The side sessions that are being brought in and do not have their turn yet.</returns>
    IReadOnlyList<BringInItem> Pending(Guid mainId);

    /// <summary>
    /// Sets whether a side session is brought into its main workspace by itself once its work is done.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <param name="enabled">Whether it is.</param>
    /// <returns>A task that completes once set.</returns>
    Task SetWhenFinished(Guid id, bool enabled);

    /// <summary>
    /// Tells whether a request to bring work in waits for its turn, which goes before the queued inputs and a loop's next prompt.
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
internal sealed class SideIntegrations(IWorkspaceManager workspaces, ISideSessions sessions, ISideSessionRepository repository, IPromptService prompts, IWorkQueue queue, IWorkspaceActivity activity, IWorkspaceLoops loops, IScriptRunner scripts, IWindowGuard guard, IWorkspaceSnapshots snapshots, IEventHub events, ILog log, ISwarmService? swarm = null, TimeSpan? interval = null, TimeSpan? retryDelay = null, Func<IFollowUps?>? following = null, IActivityLog? journal = null, Func<Guid, string, Task<string?>>? remote = null, IFailureKinds? failures = null) : ISideIntegrations
{
    private readonly TimeSpan every = interval ?? TimeSpan.FromSeconds(5);

    private readonly TimeSpan retryAfter = retryDelay ?? TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> preparing = new();

    private readonly ConcurrentDictionary<Guid, byte> sending = new();

    private readonly HashSet<string> cancelled = [];

    private readonly Lock handing = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> notBefore = new();

    private readonly SemaphoreSlim pumping = new(1, 1);

    private readonly int listedChanges = 200;

    private readonly int deliveryLimit = 2;

    private readonly IFailureKinds kinds = failures ?? new FailureKinds();

    private readonly string restarted = "The server restarted while you were bringing this in. Check what is already here before you change anything, so nothing is brought in twice.";

    private readonly string unconfirmed = "Bringing it in was not confirmed.";

    private readonly string handoffQuestion = "Another AI is about to bring this side session's work into the main session. Describe only what was done in this side session since it was made, not what was done in the main session before it: the goal, the decisions that matter, how it was checked and anything to watch out for. Answer with that description alone, in at most 300 words.";

    private CancellationToken stopping;

    /// <inheritdoc />
    public async Task Open(CancellationToken cancellation)
    {
        stopping = cancellation;
        events.Subscribe(Follow);

        following?.Invoke()?.Observe(Worked);

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
                // A turn the server ended counts, so one that stops the server cannot go on for good; it gets one more than others.
                if (integration.Deliveries > deliveryLimit)
                {
                    await GiveBack(id, side.ParentId, side.Name, integration, "The server stopped too often while the AI was bringing it in.");
                }
                else
                {
                    await Change(id, integration.Id, current => current with { InWork = false, Interrupted = true });
                }
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
                throw new RequestException(side.State == SideSessionState.Integrating ? "The side session is already being brought into main." : "The side session is not ready.");
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
            integration = side is { State: SideSessionState.Integrating, Integration: { } current } ? current : throw new RequestException("The side session is not being brought into main.");
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
        string nothing = "There is nothing being brought in with that id here; it may have been cancelled. Nothing was removed.";

        if (workspaces.Sides(mainId).FirstOrDefault(entry => entry.Side.Integration?.Id == id) is not { Side: not null } found)
        {
            // It may name the result of a delegated task instead.
            return remote is not null && await remote(mainId, id) is { } answer ? answer : nothing;
        }

        using (await sessions.Lock(found.Id))
        {
            if (workspaces.GetSide(found.Id) is not { State: SideSessionState.Integrating, Integration: { } integration } side || integration.Id != id)
            {
                return nothing;
            }

            // Reverting main's work would now throw away the only copy of what the session did.
            snapshots.Protect(mainId, "This work brought a side session in, which cancelling it would lose. Stop it instead.");
            Record(mainId, side.Name, integration);
            await Withdraw(found.Id, side, integration);

            if (swarm is not null)
            {
                await swarm.Forget(found.Id);
            }

            await sessions.Remove(found.Id);
            return $"The side session \"{side.Name}\" is brought in and was removed.";
        }
    }

    /// <inheritdoc />
    public async Task Reopen(Guid id) => await workspaces.UpdateSide(id, current => current with { MarkedComplete = false, Error = null, UnsentTask = null });

    /// <inheritdoc />
    public async Task SetCommit(Guid id, bool commit)
    {
        if (await workspaces.UpdateSide(id, current => current with { CommitBringIn = commit }) is null)
        {
            throw new RequestException("The side session no longer exists.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BringInItem> Pending(Guid mainId)
    {
        // One that was given its turn but has not reached the AI yet is still to come, so nobody takes the workspace for done in between.
        bool handingOver = activity.Get(mainId) == WorkspaceState.Ready && !prompts.IsBusy(mainId);

        return
        [
            .. workspaces.Sides(mainId)
                .Where(entry => entry.Side is { State: SideSessionState.Integrating, Integration: not null } && (!entry.Side.Integration.InWork || handingOver))
                .Select(entry => new BringInItem(entry.Id, $"Bring in {entry.Side.Name}", entry.Side.CommitBringIn, entry.Side.Integration!.Request is null ? "Preparing its changes" : null))
        ];
    }

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
    public bool IsDelivering(Guid mainId) => workspaces.Sides(mainId).Any(entry => entry.Side is { State: SideSessionState.Integrating, Integration: { InWork: false, Request: not null } });

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
                log.Write($"Bringing in '{side.Name}' was not cancelled: {exception.Message}");
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

        if (following?.Invoke()?.IsHolding(id) == true)
        {
            return "The side session has queued inputs, work to try again or a commit still to come. Let them run, or remove the queued inputs, first.";
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
            log.Write($"The files made for bringing in '{side.Name}' were left: {exception.Message}");
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
                string[] changed = [.. changes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(listedChanges)];
                StoredIntegration? ready = await Change(id, integrationId, current => current with { Folder = files, Request = request, Handoff = handoff.Trim(), Files = changed });

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

            await workspaces.UpdateSide(id, current => current with { State = SideSessionState.Ready, Integration = null, Error = $"Bringing it in could not start: {reason}" });
        }

        prompts.Unshare(side.ParentId, integrationId);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = side.ParentId, Time = DateTimeOffset.UtcNow, Error = $"Bringing the side session '{side.Name}' in could not start: {reason}", IsNotice = true });
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
            - How you combine it is up to you: use git or edit the files, whichever you see fit. Do not commit: Auto Remote commits when your turn ends, if the user set it to.
            When all of it is brought over, call finish_integration with integration_id "{integrationId}", which removes the session for good, and then say what you did, including anything you adapted or left out. If you cannot bring it over, do not call it.
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

    // Gives out the requests whose workspace can take them, and brings in the side sessions whose work is done.
    private async Task Pump()
    {
        await pumping.WaitAsync();

        try
        {
            foreach ((Guid id, StoredSideSession side) in workspaces.Sides().ToList())
            {
                if (side is { State: SideSessionState.Ready, MarkedComplete: true, IntegrateWhenFinished: true } && Busy(id) is null)
                {
                    try
                    {
                        await Integrate(id);
                    }
                    catch (RequestException)
                    {
                    }
                }
            }

            // Everything that is ready for a workspace goes into one turn, and its AI decides how to combine it.
            foreach (IGrouping<Guid, (Guid Id, StoredSideSession Side)> ready in workspaces.Sides().Where(entry => entry.Side is { State: SideSessionState.Integrating, Integration: { Request: not null, InWork: false } }).GroupBy(entry => entry.Side.ParentId).ToList())
            {
                await Deliver(ready.Key, [.. ready.Where(entry => !sending.ContainsKey(entry.Id) && (!notBefore.TryGetValue(entry.Id, out DateTimeOffset soonest) || DateTimeOffset.UtcNow >= soonest))]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            log.Write($"Side sessions could not be brought in: {exception.Message}");
        }
        finally
        {
            pumping.Release();
        }
    }

    private async Task Deliver(Guid mainId, IReadOnlyList<(Guid Id, StoredSideSession Side)> ready)
    {
        if (ready.Count == 0 || activity.Get(mainId) != WorkspaceState.Ready || prompts.IsBusy(mainId) || following?.Invoke()?.MayBringIn(mainId) == false || guard.IsPaused(mainId) || await guard.Hold(mainId, ready[0].Side.Service, stopping))
        {
            return;
        }

        List<(Guid Id, string Name, StoredIntegration Integration)> taken = [];

        foreach ((Guid id, StoredSideSession side) in ready)
        {
            sending[id] = 0;

            if (await Change(id, side.Integration!.Id, current => current with { InWork = true, Deliveries = current.Deliveries + 1, StepId = current.StepId == Guid.Empty ? Guid.NewGuid() : current.StepId }) is { } marked)
            {
                taken.Add((id, side.Name, marked));
            }
            else
            {
                sending.TryRemove(id, out _);
            }
        }

        if (taken.Count > 0)
        {
            bool commit = ready.Any(entry => entry.Side.CommitBringIn);
            // Sent apart from the pump: starting an AI can take long, and other workspaces must not wait for it.
            _ = Task.Run(() => Send(mainId, taken, commit), CancellationToken.None);
        }
    }

    private async Task Send(Guid mainId, IReadOnlyList<(Guid Id, string Name, StoredIntegration Integration)> sessionsToBring, bool commit)
    {
        bool started = false;
        IFollowUps? followed = following?.Invoke();
        string request = sessionsToBring.Count == 1
            ? sessionsToBring[0].Integration.Request!
            : $"Bring these {sessionsToBring.Count} side sessions into this workspace. Decide how to combine them, together or one after another, and confirm each one you finished.\n\n{string.Join("\n\n---\n\n", sessionsToBring.Select(entry => entry.Integration.Request))}";
        WorkStep step = new() { Id = sessionsToBring[0].Integration.StepId, Source = ActivitySource.BringIn, Text = request, Commit = commit, Preface = sessionsToBring.Any(entry => entry.Integration.Interrupted) ? restarted : null };

        try
        {
            if (sessionsToBring.All(entry => workspaces.GetSide(entry.Id)?.Integration?.Id != entry.Integration.Id))
            {
                return;
            }

            if (followed is not null && !followed.TryExpect(mainId, step, end => _ = Ended(mainId, sessionsToBring, end), () => Unfinished(sessionsToBring)))
            {
                // Something else is on its way to the workspace.
                await PutBack(sessionsToBring);
                return;
            }

            WorkspaceInfo main = await workspaces.Get(mainId, stopping);
            Task<bool> beginning = Task.FromResult(true);

            if (main.Service == AiService.Local)
            {
                beginning = (await queue.SubmitWhenFree(mainId, request, WorkSource.Integration, () => Commits(mainId, commit), stopping)).Started;
            }
            else
            {
                await prompts.PromptWhenIdle(mainId, request, () => Commits(mainId, commit), stopping);
            }

            followed?.Sent(mainId, step.Id);

            // A cancellation while it was on its way takes it out of the queue again.
            foreach ((Guid _, string _, StoredIntegration integration) in sessionsToBring.Where(entry => IsCancelled(entry.Integration.Id)))
            {
                prompts.Withdraw(mainId, integration.Request!);
                await queue.Withdraw(mainId, integration.Request!);
            }

            started = await beginning.WaitAsync(stopping);

            if (!started)
            {
                await Ended(mainId, sessionsToBring, new WorkEnd(ActivityOutcome.Stopped));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !stopping.IsCancellationRequested)
        {
            followed?.NotSent(mainId, step.Id);
            log.Write($"A request to bring side sessions in could not be sent: {exception.Message}");

            // Work that got there first is not a failure; an AI that cannot run is waited for; anything else counts against it.
            bool busy = activity.Get(mainId) != WorkspaceState.Ready || prompts.IsBusy(mainId);
            await Ended(mainId, sessionsToBring, new WorkEnd(busy || kinds.IsPassing(exception.Message) ? ActivityOutcome.Waiting : ActivityOutcome.Failed, !busy && kinds.IsPassing(exception.Message), exception.Message));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            List<(string Name, string Id)> dropped;

            lock (handing)
            {
                foreach ((Guid id, string _, StoredIntegration _) in sessionsToBring)
                {
                    sending.TryRemove(id, out _);
                }

                dropped = [.. sessionsToBring.Where(entry => cancelled.Contains(entry.Integration.Id)).Select(entry => (entry.Name, entry.Integration.Id))];
            }

            foreach ((string name, string integrationId) in started ? dropped : [])
            {
                await TellToStop(mainId, name, integrationId);
            }
        }
    }

    // Requests that did not reach the AI, which are given again.
    private async Task PutBack(IReadOnlyList<(Guid Id, string Name, StoredIntegration Integration)> asked)
    {
        foreach ((Guid id, string _, StoredIntegration integration) in asked)
        {
            notBefore[id] = DateTimeOffset.UtcNow + retryAfter;
            await Change(id, integration.Id, current => current with { InWork = false, Deliveries = Math.Max(0, current.Deliveries - 1) });
        }
    }

    private void Commits(Guid mainId, bool commit)
    {
        if (commit)
        {
            following?.Invoke()?.Arm(mainId);
        }
    }

    private string? Unfinished(IReadOnlyList<(Guid Id, string Name, StoredIntegration Integration)> asked)
        => asked.Any(entry => !IsCancelled(entry.Integration.Id) && workspaces.GetSide(entry.Id)?.Integration?.Id == entry.Integration.Id) ? unconfirmed : null;

    // The turn that was to bring the sessions in ended: those it did not confirm are asked for again, or given back.
    private async Task Ended(Guid mainId, IReadOnlyList<(Guid Id, string Name, StoredIntegration Integration)> asked, WorkEnd end)
    {
        try
        {
            foreach ((Guid id, string name, StoredIntegration integration) in asked.Where(entry => workspaces.GetSide(entry.Id)?.Integration?.Id == entry.Integration.Id).ToList())
            {
                // A turn the AI never really had, or that the user stopped, does not count against it.
                bool counts = !end.CouldNotRun && end.Outcome is not (ActivityOutcome.Stopped or ActivityOutcome.Waiting);
                bool gaveUp = end.Outcome == ActivityOutcome.Unfinished && end.Error != unconfirmed;
                // An AI that cannot run is waited for ever longer, so it is not asked again and again.
                notBefore[id] = DateTimeOffset.UtcNow + (end.CouldNotRun ? Longer(kinds.Delay(integration.Delays), retryAfter) : retryAfter);

                if (counts && (gaveUp || integration.Deliveries >= deliveryLimit))
                {
                    await GiveBack(id, mainId, name, integration, end.Error ?? unconfirmed);
                }
                else
                {
                    await Change(id, integration.Id, current => current with { InWork = false, Deliveries = counts ? current.Deliveries : Math.Max(0, current.Deliveries - 1), Delays = end.CouldNotRun ? current.Delays + 1 : 0 });
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or RequestException)
        {
            log.Write($"Side sessions could not be brought in: {exception.Message}");
        }
    }

    private async Task GiveBack(Guid id, Guid mainId, string name, StoredIntegration integration, string reason)
    {
        StoredSideSession side;

        using (await sessions.Lock(id))
        {
            if (workspaces.GetSide(id) is not { Integration: { } current } found || current.Id != integration.Id)
            {
                return;
            }

            side = found;
            // It stays as it is, and is not brought in again until the user asks.
            await workspaces.UpdateSide(id, entry => entry with { State = SideSessionState.Ready, Integration = null, IntegrateWhenFinished = false, MarkedComplete = false, Error = $"Needs attention: it was not brought in. {reason}" });
        }

        await Withdraw(id, side, integration);
        following?.Invoke()?.Tell(mainId, $"Bringing the side session \"{name}\" in was not confirmed, so it stays as it was. This workspace may hold part of its changes.");
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = mainId, Time = DateTimeOffset.UtcNow, Error = $"The side session '{name}' was not brought in and stays as it was: {reason}", IsNotice = true });
    }

    // What the session said about its work stays with the activity of the workspace it went into, since the session goes.
    private void Record(Guid mainId, string name, StoredIntegration integration)
    {
        if (journal is null || following?.Invoke()?.Describe(mainId).Step is not { } step)
        {
            return;
        }

        journal.Change(mainId, step.Id, entry => entry with
        {
            Handoff = string.Join("\n\n", new[] { entry.Handoff, $"{name}: {integration.Handoff}" }.Where(part => !string.IsNullOrEmpty(part))),
            Files = [.. entry.Files, .. integration.Files.Select(file => $"{name}: {file}")]
        });
        journal.Note(mainId, step.Id, $"Brought in the side session \"{name}\".");
    }

    // A side session whose work ended well is done; any other way it ends, or new work, takes that back.
    private void Worked(Guid id, WorkStep step, WorkEnd end)
    {
        if (workspaces.GetSide(id) is null || end.Outcome == ActivityOutcome.Waiting)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await workspaces.UpdateSide(id, current => current with { MarkedComplete = end.Outcome == ActivityOutcome.Done });
                await Pump();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or RequestException)
            {
                log.Write($"Side sessions could not be brought in: {exception.Message}");
            }
        }, CancellationToken.None);
    }

    private TimeSpan Longer(TimeSpan first, TimeSpan second) => first > second ? first : second;

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
            await prompts.Prompt(mainId, $"The user cancelled bringing in the side session \"{name}\" (integration {integrationId}). Stop bringing it in, leave what you already changed as it is without undoing it, and go on with your other work.", stopping);
        }
        catch (Exception exception) when (exception is RequestException or OperationCanceledException)
        {
            log.Write($"The main AI could not be told that an integration was cancelled: {exception.Message}");
        }
    }

    private void Follow(RemoteEvent remoteEvent)
    {
        if (remoteEvent.Kind == RemoteEventKind.StateChanged)
        {
            _ = Task.Run(Pump, CancellationToken.None);
        }
        else if (remoteEvent.Kind is RemoteEventKind.LoopChanged or RemoteEventKind.QueueChanged or RemoteEventKind.ScriptChanged or RemoteEventKind.LimitPauseChanged or RemoteEventKind.SavedInputsChanged)
        {
            if (workspaces.GetSide(remoteEvent.WorkspaceId) is { MarkedComplete: true } || remoteEvent.Kind == RemoteEventKind.LimitPauseChanged)
            {
                _ = Task.Run(Pump, CancellationToken.None);
            }
        }
    }
}
