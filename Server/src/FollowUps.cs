namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What the AI's work tells as it ends.
/// </summary>
internal interface IWorkCompletion
{
    /// <summary>
    /// Takes note that the AI stopped working, which has to be told before the workspace is ready again.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="succeeded">Whether the work went well: the AI answered, and was neither stopped nor failed.</param>
    /// <returns><see langword="true" /> if the workspace was taken straight on into <see cref="WorkspaceState.Managing" /> to commit its changes, so it must not be made ready.</returns>
    bool Complete(Guid id, bool succeeded);

    /// <summary>
    /// Checks whether the changes of a workspace are being committed after its work, which no other work starts before.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if they are.</returns>
    bool IsCommitting(Guid id);

    /// <summary>
    /// Takes note that work which took a ready workspace never started, so what was turned on for it goes off again. Work that is still to be committed stays so.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Abort(Guid id);

    /// <summary>
    /// Gives up on what was to follow the work, which was cancelled or will not end by itself: nothing is committed, the queued inputs are paused, and work still under way does not resume them as it ends.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    Task Abandon(Guid id);
}

/// <summary>
/// What follows the AI's work in a workspace: committing its changes automatically, and then sending the queued inputs one at a time, each committed in turn.
/// </summary>
internal interface IFollowUps : IWorkCompletion
{
    /// <summary>
    /// Pauses the queued inputs that were left, since the work they waited for did not end, and starts following the workspaces.
    /// </summary>
    /// <param name="cancellation">Ends following them when cancelled.</param>
    Task Open(CancellationToken cancellation);

    /// <summary>
    /// Gets whether the changes of a workspace are committed once its work is over.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>The state.</returns>
    AutoCommitState Get(Guid id);

    /// <summary>
    /// Turns auto commit on for work that is about to be given to the workspace or joins the work under way, unless it is being committed already or a run of a loop is under way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if this turned it on, so it is to be turned off again if the work is not taken.</returns>
    bool Arm(Guid id);

    /// <summary>
    /// Turns auto commit off, unless the commit is under way already.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Disarm(Guid id);

    /// <summary>
    /// Checks whether something that follows the workspace's work goes before any other new work: a commit under way or due, or a queued input that is to be sent or being sent.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if there is.</returns>
    bool GoesFirst(Guid id);

    /// <summary>
    /// Sets whether the changes are committed once the work that is under way is over, which holds for that work only.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="enabled">Whether they are.</param>
    /// <exception cref="RequestException">It is turned on while no work is under way, or while a run of a loop is under way.</exception>
    void SetAutoCommit(Guid id, bool enabled);

    /// <summary>
    /// Queues an input, which is sent at once when no work is under way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The input.</param>
    /// <param name="autoCommit">Whether the changes are committed after its work.</param>
    /// <param name="cancellation">Cancels the save.</param>
    /// <returns>The saved inputs now.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the text is empty or too long.</exception>
    Task<IReadOnlyList<SavedInput>> Queue(Guid id, string text, bool autoCommit, CancellationToken cancellation);

    /// <summary>
    /// Resumes the paused queued inputs, which are sent once nothing is in their way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the change.</param>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task ResumeQueue(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Checks whether a commit is under way or still to come once the work is over, which other new work must not go ahead of.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    bool IsPending(Guid id);

    /// <summary>
    /// Checks whether anything is still to follow the work of a workspace: a commit or queued inputs, paused ones included.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if there is.</returns>
    bool IsHolding(Guid id);
}

/// <inheritdoc />
internal sealed class FollowUps(IWorkspaceManager workspaces, ISavedInputs savedInputs, IPromptService prompts, IWorkQueue queue, IWorkspaceActivity activity, IWorkspaceVersioning versioning, IGitService git, IWorkspaceLoops loops, IWindowGuard guard, IEventHub events, Func<Guid, Task<bool>> awaited, Func<Guid, bool>? held = null, TimeSpan? interval = null, Func<Guid, Task>? reopen = null) : IFollowUps
{
    private readonly Lock gate = new();

    private readonly Dictionary<Guid, Following> following = [];

    private readonly SemaphoreSlim signal = new(0);

    private readonly TimeSpan pace = interval ?? TimeSpan.FromMilliseconds(300);

    private CancellationToken stopping;

    private IDisposable? subscription;

    /// <inheritdoc />
    public async Task Open(CancellationToken cancellation)
    {
        stopping = cancellation;

        foreach (WorkspaceInfo workspace in await workspaces.All(cancellation))
        {
            await Pause(workspace.Id, true);
        }

        subscription?.Dispose();
        subscription = events.Subscribe(Follow);

        // Work that was waiting for the local AI is restored and still runs, so it is committed as it was to be.
        foreach (Guid id in queue.RestoredCommits)
        {
            Arm(id);
        }

        _ = Pump(cancellation);
    }

    /// <inheritdoc />
    public AutoCommitState Get(Guid id)
    {
        lock (gate)
        {
            return State(id);
        }
    }

    /// <inheritdoc />
    public void SetAutoCommit(Guid id, bool enabled)
    {
        if (enabled && loops.IsRunning(id))
        {
            throw new RequestException("A run of the loop is working, which is not committed by itself.");
        }

        bool suspended = guard.IsSuspended(id);

        activity.Together(() =>
        {
            lock (gate)
            {
                Following entry = Entry(id);

                if (entry.IsCommitting)
                {
                    if (!enabled)
                    {
                        throw new RequestException("The changes are being committed already. Stop it to keep them from being committed.");
                    }

                    return false;
                }

                if (enabled && !entry.IsArmed)
                {
                    WorkspaceState state = activity.Get(id);

                    if (state != WorkspaceState.Working && !suspended)
                    {
                        throw new RequestException("The AI is not working.");
                    }

                    // Work that waits to be resumed has ended its turn, and is committed once nothing of it is left.
                    entry.IsDue = state == WorkspaceState.Ready;
                }

                if (!enabled)
                {
                    entry.IsDue = false;
                }

                Arm(id, entry, enabled);
                return true;
            }
        });

        Wake();
    }

    /// <inheritdoc />
    public bool Arm(Guid id)
    {
        if (loops.IsRunning(id))
        {
            return false;
        }

        lock (gate)
        {
            Following entry = Entry(id);

            if (entry.IsArmed || entry.IsCommitting)
            {
                return false;
            }

            Arm(id, entry, true);
            return true;
        }
    }

    /// <inheritdoc />
    public void Disarm(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && !entry.IsCommitting)
            {
                entry.IsDue = false;
                Arm(id, entry, false);
            }
        }

        Wake();
    }

    /// <inheritdoc />
    public bool GoesFirst(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && (entry.IsCommitting || entry.IsDue || entry.IsStarting))
            {
                return true;
            }
        }

        return savedInputs.Waiting.Contains(id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> Queue(Guid id, string text, bool autoCommit, CancellationToken cancellation)
    {
        IReadOnlyList<SavedInput> saved = await savedInputs.Queue(id, text, autoCommit, cancellation);
        Wake();
        return saved;
    }

    /// <inheritdoc />
    public async Task ResumeQueue(Guid id, CancellationToken cancellation)
    {
        await savedInputs.SetPaused(id, false, cancellation);
        Wake();
    }

    /// <inheritdoc />
    public async Task Abandon(Guid id)
    {
        if (prompts.IsBusy(id))
        {
            lock (gate)
            {
                Entry(id).IsGivenUp = true;
            }
        }

        Disarm(id);
        await Hold(id);
    }

    /// <inheritdoc />
    public void Abort(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && !entry.IsCommitting && !entry.IsDue)
            {
                Arm(id, entry, false);
            }
        }

        Wake();
    }

    /// <inheritdoc />
    public bool IsCommitting(Guid id)
    {
        lock (gate)
        {
            return following.TryGetValue(id, out Following? entry) && entry.IsCommitting;
        }
    }

    /// <inheritdoc />
    public bool IsPending(Guid id)
    {
        lock (gate)
        {
            return following.TryGetValue(id, out Following? entry) && (entry.IsCommitting || entry.IsDue);
        }
    }

    /// <inheritdoc />
    public bool IsHolding(Guid id) => IsPending(id) || savedInputs.HasQueued(id);

    /// <inheritdoc />
    public bool Complete(Guid id, bool succeeded)
    {
        bool givenUp;
        int halts;

        lock (gate)
        {
            Following entry = Entry(id);
            givenUp = entry.IsGivenUp;
            entry.IsGivenUp = false;
            halts = entry.Halts;
        }

        WorkspaceState state = activity.Get(id);

        // A stop can come as the AI ends by itself, before the AI hears of it, and what it stopped is not committed either.
        if (!succeeded || state is WorkspaceState.Stopping or WorkspaceState.Canceling)
        {
            Disarm(id);
            _ = Hold(id);
            return false;
        }

        // An AI that works as part of a git action, resolving its conflicts, ends no work of the user's.
        if (state != WorkspaceState.Working)
        {
            return false;
        }

        bool suspended = guard.IsSuspended(id);
        bool looped = loops.IsRunning(id);

        bool? decided = activity.Together<bool?>(() =>
        {
            lock (gate)
            {
                Following entry = Entry(id);

                if (entry.IsArmed)
                {
                    if (suspended)
                    {
                        entry.IsDue = true;
                        return false;
                    }

                    if (activity.Pass(id, WorkspaceState.Working, WorkspaceState.Managing))
                    {
                        Claim(id, entry);
                        return true;
                    }
                }

                return null;
            }
        });

        if (decided is { } taken)
        {
            return taken;
        }

        // The runs of a loop go on by themselves, so their going well resumes nothing, and neither does work that was given up or has been stopped since.
        if (!suspended && !looped && !givenUp && savedInputs.HasQueued(id) && !HasHalted(id, halts))
        {
            _ = Resume(id);
        }

        Wake();
        return false;
    }

    private Following Entry(Guid id)
    {
        if (!following.TryGetValue(id, out Following? entry))
        {
            entry = new Following();
            following[id] = entry;
        }

        return entry;
    }

    private AutoCommitState State(Guid id)
        => !following.TryGetValue(id, out Following? entry) ? AutoCommitState.Off : entry.IsCommitting ? AutoCommitState.Committing : entry.IsArmed ? AutoCommitState.On : AutoCommitState.Off;

    // Published while the lock is held so the changes leave in the order they were made.
    private void Announce(Guid id) => events.Publish(new RemoteEvent { Kind = RemoteEventKind.AutoCommitChanged, WorkspaceId = id, AutoCommit = State(id) });

    private void Arm(Guid id, Following entry, bool armed)
    {
        if (entry.IsArmed != armed)
        {
            entry.IsArmed = armed;
            Announce(id);
        }
    }

    // The workspace was just put into the state of git actions for the commit.
    private void Claim(Guid id, Following entry)
    {
        entry.IsCommitting = true;
        entry.IsStopped = false;
        entry.IsArmed = false;
        entry.IsDue = false;
        Announce(id);
        _ = Task.Run(() => Commit(id), CancellationToken.None);
    }

    private async Task Commit(Guid id)
    {
        bool claimed = true;
        bool postponed = false;

        try
        {
            if (await awaited(id))
            {
                postponed = true;
                return;
            }

            WorkspaceInfo workspace = await workspaces.Get(id, stopping);
            GitStatus status = await git.Status(workspace.Folder, stopping);

            if (status is { IsRepo: true, HasChanges: true })
            {
                if (!status.HasIdentity)
                {
                    throw new RequestException("Git needs your name and email first, which committing once from the version menu asks for.");
                }

                RequireNotStopped(id);
                claimed = false;
                await versioning.PerformClaimed(workspace, async token =>
                {
                    // A stop that came before there was an action to cancel is honored here.
                    RequireNotStopped(id);
                    await git.Commit(workspace.Folder, await versioning.CommitMessage(workspace, token), token);
                    // While git still has the workspace, so no client takes it for ready with its queued inputs paused.
                    await ResumeUnlessStopped(id);
                }, stopping);
            }
            else
            {
                await ResumeUnlessStopped(id);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whatever is queued would be mixed with the changes that were not committed.
            await Pause(id, true);
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Time = DateTimeOffset.UtcNow, Error = $"Auto commit failed: {exception.Message}" });
        }
        finally
        {
            bool stopped;

            lock (gate)
            {
                Following entry = Entry(id);
                entry.IsCommitting = false;
                stopped = entry.IsStopped;

                if (postponed && !stopped)
                {
                    entry.IsArmed = true;
                    entry.IsDue = true;
                }

                Announce(id);
            }

            // A stop takes the workspace over, and what holds it by now is not this commit's.
            if (claimed && !stopped)
            {
                activity.Leave(id, WorkspaceState.Managing);
            }

            Wake();
        }
    }

    private bool IsStopped(Guid id)
    {
        lock (gate)
        {
            return Entry(id).IsStopped;
        }
    }

    private void RequireNotStopped(Guid id)
    {
        if (IsStopped(id) || activity.Get(id) != WorkspaceState.Managing)
        {
            throw new RequestException("It was stopped.");
        }
    }

    private Task ResumeUnlessStopped(Guid id) => IsStopped(id) ? Task.CompletedTask : Resume(id);

    private bool HasHalted(Guid id, int since)
    {
        lock (gate)
        {
            return Entry(id).Halts != since;
        }
    }

    // A workspace that was deleted has nothing left to follow.
    private async Task<WorkspaceInfo?> Find(Guid id)
    {
        try
        {
            return await workspaces.Get(id, stopping);
        }
        catch (RequestException)
        {
            savedInputs.Forget(id);

            lock (gate)
            {
                following.Remove(id);
            }

            return null;
        }
    }

    // Holds the queued inputs back from this moment on, though writing it down takes a while.
    private async Task Hold(Guid id)
    {
        bool queued = savedInputs.HasQueued(id);

        lock (gate)
        {
            Entry(id).Halts++;

            if (queued)
            {
                Entry(id).Pausing++;
            }
        }

        if (!queued)
        {
            return;
        }

        try
        {
            await Pause(id, true);
        }
        finally
        {
            lock (gate)
            {
                Entry(id).Pausing--;
            }
        }
    }

    private Task Resume(Guid id) => Pause(id, false);

    private async Task Pause(Guid id, bool paused)
    {
        try
        {
            await savedInputs.SetPaused(id, paused, CancellationToken.None);
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Follow(RemoteEvent remoteEvent)
    {
        // What a stop will make of the work is known as soon as it is asked for.
        if (remoteEvent is { Kind: RemoteEventKind.StateChanged, State: WorkspaceState.Stopping or WorkspaceState.Canceling })
        {
            lock (gate)
            {
                if (following.TryGetValue(remoteEvent.WorkspaceId, out Following? entry) && entry.IsCommitting)
                {
                    entry.IsStopped = true;
                }
            }

            Disarm(remoteEvent.WorkspaceId);
            _ = Hold(remoteEvent.WorkspaceId);
        }

        // Waiting work that was taken out of the line never ends.
        if (remoteEvent is { Kind: RemoteEventKind.QueueChanged, Queue: null, Text: "dropped" } && !prompts.IsBusy(remoteEvent.WorkspaceId))
        {
            Disarm(remoteEvent.WorkspaceId);
        }
    }

    private void Wake()
    {
        if (signal.CurrentCount == 0)
        {
            signal.Release();
        }
    }

    private async Task Pump(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await signal.WaitAsync(pace, cancellation);
                List<Guid> due;

                lock (gate)
                {
                    due = [.. following.Where(entry => entry.Value.IsDue).Select(entry => entry.Key)];
                }

                // A commit that is due goes before any queued input.
                foreach (Guid id in due)
                {
                    await Attend(id, () => CommitWhenOver(id));
                }

                foreach (Guid id in savedInputs.Waiting)
                {
                    await Attend(id, () => StartWhenFree(id));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // What goes wrong with one workspace must not end the following of all of them.
    private async Task Attend(Guid id, Func<Task> attend)
    {
        try
        {
            await attend();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Time = DateTimeOffset.UtcNow, Error = $"What was to follow the work could not be done: {exception.Message}" });
            await Abandon(id);
        }
    }

    private async Task CommitWhenOver(Guid id)
    {
        if (await Find(id) is null)
        {
            return;
        }

        if (prompts.IsBusy(id))
        {
            // The work goes on, and its end decides again.
            lock (gate)
            {
                Entry(id).IsDue = false;
            }

            return;
        }

        if (guard.IsSuspended(id) || loops.IsRunning(id) || activity.Get(id) != WorkspaceState.Ready || await awaited(id))
        {
            return;
        }

        activity.Together(() =>
        {
            lock (gate)
            {
                Following entry = Entry(id);

                if (entry is { IsDue: true, IsArmed: true } && activity.TryEnter(id, WorkspaceState.Managing))
                {
                    Claim(id, entry);
                }

                return true;
            }
        });
    }

    private bool IsFree(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && (entry.IsArmed || entry.IsCommitting || entry.IsDue || entry.IsStarting || entry.Pausing > 0))
            {
                return false;
            }
        }

        return activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id) && !loops.IsRunning(id) && queue.Get(id) is null && !queue.IsStarting(id) && held?.Invoke(id) != true;
    }

    private async Task StartWhenFree(Guid id)
    {
        if (!IsFree(id))
        {
            return;
        }

        if (await Find(id) is not { } workspace)
        {
            return;
        }

        if (workspace.Side is { State: not SideSessionState.Ready } || guard.IsSuspended(id) || await awaited(id) || await guard.Hold(id, workspace.Service, stopping) || !IsFree(id))
        {
            return;
        }

        lock (gate)
        {
            Entry(id).IsStarting = true;
        }

        // Sent apart from the pump, since starting an AI can take long and other workspaces must not wait for it.
        _ = Task.Run(() => Start(workspace), CancellationToken.None);
    }

    private async Task Start(WorkspaceInfo workspace)
    {
        Guid id = workspace.Id;
        bool armed = false;

        try
        {
            // Turned on only once the input has the workspace, and before its work can end, so work that got there first keeps its own setting.
            await savedInputs.SendNext(id, input => Send(workspace, input, () => armed = input.AutoCommit && Arm(id)), stopping);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            if (armed)
            {
                Disarm(id);
            }

            // Work that got there first is not a failure, and the input is sent once it is over.
            if (activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id) && queue.Get(id) is null && !queue.IsStarting(id))
            {
                await Pause(id, true);
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Time = DateTimeOffset.UtcNow, Error = $"A queued input could not be sent: {exception.Message}" });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (gate)
            {
                Entry(id).IsStarting = false;
            }

            Wake();
        }
    }

    private async Task Send(WorkspaceInfo workspace, SavedInput input, Action taken)
    {
        // A side session that was marked complete is given new work, as one that is sent a message is.
        if (workspace.Side is not null && reopen is not null)
        {
            await reopen(workspace.Id);
        }

        if (workspace.Service == AiService.Local)
        {
            await queue.SubmitWhenFree(workspace.Id, input.Text, taken, stopping);
        }
        else
        {
            await prompts.PromptWhenIdle(workspace.Id, input.Text, taken, stopping);
        }
    }

    private sealed class Following
    {
        public bool IsArmed { get; set; }

        public bool IsCommitting { get; set; }

        public bool IsDue { get; set; }

        public bool IsGivenUp { get; set; }

        public bool IsStarting { get; set; }

        public bool IsStopped { get; set; }

        public int Halts { get; set; }

        public int Pausing { get; set; }
    }
}
