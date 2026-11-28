namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What the AI's work tells as it goes, and what it asks to be told.
/// </summary>
internal interface IWorkCompletion
{
    /// <summary>
    /// Takes note that the AI took up work that was not under way, which is the work that was expected, work that waited to go on, or else a message of its own.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">What the AI was sent, or <see langword="null" /> for work it started by itself.</param>
    void Began(Guid id, string? text);

    /// <summary>
    /// Gets what is added to what the AI is sent as new work, which the chat does not show: what did not end well since its last turn, and whether the work is unattended.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">What it is sent.</param>
    /// <returns>The addition, or <see langword="null" />.</returns>
    string? Brief(Guid id, string text);

    /// <summary>
    /// Takes note that a message joined the work under way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The message.</param>
    void Joined(Guid id, string text);

    /// <summary>
    /// Takes note that the AI stopped working, which has to be told before the workspace is ready again.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="result">How the work ended.</param>
    /// <returns><see langword="true" /> if the workspace was taken straight on into <see cref="WorkspaceState.Managing" /> to commit its changes, so it must not be made ready.</returns>
    bool Complete(Guid id, WorkResult result);

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
    /// Gives up on the work under way or waiting, which the user cancelled: nothing is committed, and what runs by itself is stopped until the user resumes it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once that is done.</returns>
    Task Abandon(Guid id);

    /// <summary>
    /// Takes back waiting work that could not start, to try it again later.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The work that could not start.</param>
    /// <param name="error">Why it could not start.</param>
    /// <returns><see langword="true" /> if it is tried again, so its text does not need to be kept otherwise.</returns>
    bool TakeBack(Guid id, string text, string error);

    /// <summary>
    /// Takes the AI's word that it cannot finish the work under way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="reason">Why.</param>
    /// <returns>What the AI is told.</returns>
    string Unfinished(Guid id, string reason);
}

/// <summary>
/// Schedules what a workspace does by itself and follows its work. Each chat does one thing at a time, in this order: the commit of work that ended, work that is tried again, bringing in other work, the queued inputs and the runs of a loop. The AI is trusted: nothing waits for approval and work that does not end well holds nothing else up. Only the user's stop does, and an AI that cannot run, whose work waits and is tried again.
/// </summary>
internal interface IFollowUps : IWorkCompletion
{
    /// <summary>
    /// Restores what the workspaces did before the server stopped and starts following them. Work that was under way is taken up again, and a commit that was still to be made is made.
    /// </summary>
    /// <param name="dataFolder">The data folder of the server.</param>
    /// <param name="cancellation">Ends following them when cancelled.</param>
    /// <returns>A task that completes once everything is restored.</returns>
    Task Open(string dataFolder, CancellationToken cancellation);

    /// <summary>
    /// Gets whether the changes of a workspace are committed once its work is over.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>The state.</returns>
    AutoCommitState Get(Guid id);

    /// <summary>
    /// Turns the commit on for work that is about to be given to the workspace or joins the work under way, unless a commit is under way already.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if this turned it on.</returns>
    bool Arm(Guid id);

    /// <summary>
    /// Turns the commit off, unless it is under way already.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Disarm(Guid id);

    /// <summary>
    /// Checks whether something of a workspace goes before the next run of its loop: a commit, work to try again or a queued input.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if something does.</returns>
    bool GoesFirst(Guid id);

    /// <summary>
    /// Checks whether a workspace is to wait before anything new runs by itself: the user stopped it, or tasks its AI delegated are under way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    bool IsWaiting(Guid id);

    /// <summary>
    /// Turns the commit on or off for the work under way, as the user asks.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="enabled">Whether the changes are committed.</param>
    /// <exception cref="RequestException">Nothing is under way to turn it on for, or it is being turned off while the commit is under way.</exception>
    void SetAutoCommit(Guid id, bool enabled);

    /// <summary>
    /// Sends a message of the user whose work is to be committed. The commit is turned on before the message is on its way, so that its work cannot end ahead of it; a message that joins work has that work committed. A send that fails leaves the commit as it was.
    /// </summary>
    /// <typeparam name="T">What sending answers.</typeparam>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="send">Sends the message.</param>
    /// <returns>What <paramref name="send" /> answered.</returns>
    Task<T> SendToCommit<T>(Guid id, Func<Task<T>> send);

    /// <summary>
    /// Queues an input. One that is to be committed has the work under way committed before it too, so the two stay apart, unless the user turned that work's commit off, or it is a queued step that is kept uncommitted to go with the next.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The input.</param>
    /// <param name="autoCommit">Whether its changes are committed after it.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>The saved inputs now.</returns>
    Task<IReadOnlyList<SavedInput>> Queue(Guid id, string text, bool autoCommit, CancellationToken cancellation);

    /// <summary>
    /// Takes note that an input that is already queued is now to be committed: the work under way is then committed before it, as when it is queued that way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void CommitBefore(Guid id);

    /// <summary>
    /// Looks again after queued inputs were removed or changed: a commit turned on only for what was queued goes off once nothing queued is to be committed.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Reconsider(Guid id);

    /// <summary>
    /// Lets what runs by itself go on after the user stopped it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Resume(Guid id);

    /// <summary>
    /// Stops the work that waits to be tried again, which the user does while nothing else runs: it is not tried again, and what else runs by itself waits until resumed.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if any work waited.</returns>
    bool StopRetries(Guid id);

    /// <summary>
    /// Holds everything that runs by itself until the user resumes it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Pause(Guid id);

    /// <summary>
    /// Checks whether the user stopped what runs by itself.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if they did.</returns>
    bool IsStopped(Guid id);

    /// <summary>
    /// Checks whether a commit is under way or still to come once the work is over.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    bool IsPending(Guid id);

    /// <summary>
    /// Checks whether anything is still to follow the work of a workspace: a commit, work to try again or queued inputs.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if there is.</returns>
    bool IsHolding(Guid id);

    /// <summary>
    /// Checks whether a workspace can take a turn that brings other work into it now. Waiting for delegated tasks does not stand in the way.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if it can.</returns>
    bool MayBringIn(Guid id);

    /// <summary>
    /// Says which work is about to be sent to a workspace, so it is followed as that. <see cref="Sent" /> or <see cref="NotSent" /> is told how sending went.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="step">The work.</param>
    /// <param name="ended">Told how the work ended, if anyone is to be told.</param>
    /// <param name="shortfall">Asked as work ends that would otherwise count as done: what it left undone, or <see langword="null" />. Work with a shortfall is not committed.</param>
    void Expect(Guid id, WorkStep step, Action<WorkEnd>? ended = null, Func<string?>? shortfall = null);

    /// <summary>
    /// Does what <see cref="Expect" /> does unless other work is on its way to the workspace, so two parts of the server that bring work in never send at once.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="step">The work.</param>
    /// <param name="ended">Told how the work ended, if anyone is to be told.</param>
    /// <param name="shortfall">What it left undone, as for <see cref="Expect" />.</param>
    /// <returns><see langword="false" /> when other work is on its way, so nothing is expected.</returns>
    bool TryExpect(Guid id, WorkStep step, Action<WorkEnd>? ended = null, Func<string?>? shortfall = null);

    /// <summary>
    /// Sets again what is asked as restored work ends, which does not survive a restart by itself.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="stepId">The work.</param>
    /// <param name="shortfall">What it left undone, as for <see cref="Expect" />.</param>
    void Require(Guid id, Guid stepId, Func<string?> shortfall);

    /// <summary>
    /// Takes note that expected work was taken, to run now or wait for the local AI.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="stepId">The work.</param>
    void Sent(Guid id, Guid stepId);

    /// <summary>
    /// Takes note that expected work could not be sent, which whoever sends it deals with.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="stepId">The work.</param>
    void NotSent(Guid id, Guid stepId);

    /// <summary>
    /// Records something about the current work in the activity.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="note">What happened.</param>
    void Note(Guid id, string note);

    /// <summary>
    /// Hears of every piece of work that ends, after its commit if it has one.
    /// </summary>
    /// <param name="observer">Told the workspace, the work and how it ended.</param>
    /// <returns>Disposed to stop hearing.</returns>
    IDisposable Observe(Action<Guid, WorkStep, WorkEnd> observer);

    /// <summary>
    /// Tells the AI of a workspace something with its next turn.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="notice">What it is told.</param>
    void Tell(Guid id, string notice);

    /// <summary>
    /// Gets what a workspace does by itself right now.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>It.</returns>
    FollowState Describe(Guid id);

    /// <summary>
    /// Gets the workspaces that are followed.
    /// </summary>
    IReadOnlyList<Guid> Followed { get; }
}

/// <inheritdoc />
internal sealed class FollowUps(IWorkspaceManager workspaces, ISavedInputs savedInputs, IPromptService prompts, IWorkQueue queue, IWorkspaceActivity activity, IWorkspaceVersioning versioning, IGitService git, IWorkspaceLoops loops, IWindowGuard guard, IEventHub events, IActivityLog journal, IAutomationStore store, IFailureKinds kinds, Func<Guid, Task<bool>> awaited, Func<Guid, bool>? held = null, TimeSpan? interval = null, Func<Guid, Task>? reopen = null, Func<Guid, bool>? isolated = null) : IFollowUps
{
    private readonly Lock gate = new();

    private readonly Dictionary<Guid, Following> following = [];

    private readonly List<Action<Guid, WorkStep, WorkEnd>> observers = [];

    private readonly SemaphoreSlim signal = new(0);

    private readonly TimeSpan pace = interval ?? TimeSpan.FromMilliseconds(300);

    private readonly int attempts = 3;

    private readonly int noticeLimit = 10;

    private readonly int textLimit = 4000;

    private readonly int reportLimit = 8000;

    private readonly string unattended = "This work runs unattended: nobody may be there to answer, so make reasonable decisions yourself and say in your final reply what you assumed. If you cannot finish it, call report_unfinished with the reason.";

    private readonly string restarted = "The server restarted while you were working on this. Check what was already done, including commands you ran and tasks you delegated, so nothing is repeated, and then finish it.";

    private CancellationToken stopping;

    private IReadOnlyDictionary<Guid, StoredAutomation>? frozen;

    private IDisposable? subscription;

    /// <inheritdoc />
    public IReadOnlyList<Guid> Followed
    {
        get
        {
            lock (gate)
            {
                return [.. following.Keys];
            }
        }
    }

    /// <inheritdoc />
    public async Task Open(string dataFolder, CancellationToken cancellation)
    {
        stopping = cancellation;
        IReadOnlyDictionary<Guid, StoredAutomation> stored = await store.Open(dataFolder, cancellation);

        foreach (WorkspaceInfo workspace in await workspaces.All(cancellation))
        {
            try
            {
                // Reading them is what tells which workspaces have inputs queued.
                await savedInputs.List(workspace.Id, cancellation);

                // An older release paused queued inputs one by one; their chat is stopped now, so nothing runs that the user held back.
                if (savedInputs.TakeLegacyPause(workspace.Id))
                {
                    Pause(workspace.Id);
                }
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
            {
            }

            // The swarm takes up the work of a task another server delegated, or ends it.
            if (isolated?.Invoke(workspace.Id) != true && stored.TryGetValue(workspace.Id, out StoredAutomation? state))
            {
                Restore(workspace.Id, state);
            }
        }

        subscription?.Dispose();
        subscription = events.Subscribe(Follow);

        // Work that was waiting for the local AI is restored and still runs, so it is committed as it was to be.
        foreach (Guid id in queue.RestoredCommits.Where(id => queue.Get(id) is not null))
        {
            Arm(id);
        }

        // What the server does while it shuts down is not written, so the next start goes on from before it.
        cancellation.Register(() =>
        {
            Dictionary<Guid, StoredAutomation> last = Snapshot();

            lock (gate)
            {
                frozen = last;
            }

            store.Save(() => last);
        });

        Keep();
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
    public bool Arm(Guid id)
    {
        lock (gate)
        {
            Following entry = Entry(id);

            if (entry.IsArmed || entry.IsCommitting)
            {
                return false;
            }

            Arm(id, entry, true);
        }

        Keep();
        return true;
    }

    /// <inheritdoc />
    public void Disarm(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && !entry.IsCommitting)
            {
                entry.IsDue = false;
                entry.IsArmedByQueue = false;
                Arm(id, entry, false);
            }
        }

        Keep();
        Wake();
    }

    /// <inheritdoc />
    public void SetAutoCommit(Guid id, bool enabled)
    {
        bool suspended = guard.IsSuspended(id);
        bool waiting = queue.Get(id) is not null;

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

                WorkspaceState state = activity.Get(id);

                if (enabled && !entry.IsArmed)
                {
                    if (state != WorkspaceState.Working && !suspended && !(waiting && entry.Step is { Started: false }) && entry.Retry is null)
                    {
                        throw new RequestException("The AI is not working.");
                    }

                    // Work that waits to be resumed has ended its turn, and is committed once nothing of it is left.
                    entry.IsDue = state == WorkspaceState.Ready && suspended;
                }

                if (!enabled)
                {
                    entry.IsDue = false;
                }

                entry.UserChoice = enabled;
                entry.IsArmedByQueue = false;

                if (entry.Retry is { } retry && state == WorkspaceState.Ready && !suspended)
                {
                    // Nothing is under way, so the choice is about the work that is to be tried again.
                    entry.Retry = retry with { Commit = enabled, CommitDeclined = !enabled, CommitFromQueue = false };
                    entry.UserChoice = null;
                    entry.IsDue = false;
                    Arm(id, entry, false);
                }
                else
                {
                    Arm(id, entry, enabled);
                }

                return true;
            }
        });

        Keep();
        Wake();
    }

    /// <inheritdoc />
    public async Task<T> SendToCommit<T>(Guid id, Func<Task<T>> send)
    {
        bool wasArmed;
        bool wasByQueue;
        bool? choice;

        lock (gate)
        {
            Following entry = Entry(id);
            (wasArmed, wasByQueue, choice) = (entry.IsArmed, entry.IsArmedByQueue, entry.UserChoice);
            Choose(id, entry);
        }

        Keep();
        T sent;

        try
        {
            sent = await send();
        }
        catch
        {
            lock (gate)
            {
                if (following.TryGetValue(id, out Following? entry) && !entry.IsCommitting)
                {
                    (entry.UserChoice, entry.IsArmedByQueue) = (choice, wasByQueue);

                    if (!wasArmed)
                    {
                        entry.IsDue = false;
                        Arm(id, entry, false);
                    }
                }
            }

            Keep();
            Wake();
            throw;
        }

        // Earlier work that ended while the message was on its way took the choice along, and the message started work of its own.
        activity.Together(() =>
        {
            lock (gate)
            {
                Following entry = Entry(id);

                if (entry.UserChoice != false && activity.Get(id) == WorkspaceState.Working)
                {
                    Choose(id, entry);
                }

                return true;
            }
        });

        Keep();
        return sent;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> Queue(Guid id, string text, bool autoCommit, CancellationToken cancellation)
    {
        if (autoCommit && !string.IsNullOrWhiteSpace(text))
        {
            // Before the input is there to be sent, so the work under way cannot end in between.
            ArmForQueue(id);
        }

        IReadOnlyList<SavedInput> saved;

        try
        {
            saved = await savedInputs.Queue(id, text, autoCommit, cancellation);
        }
        catch
        {
            Reconsider(id);
            throw;
        }

        Wake();
        return saved;
    }

    /// <inheritdoc />
    public void CommitBefore(Guid id)
    {
        ArmForQueue(id);
        Wake();
    }

    /// <inheritdoc />
    public void Reconsider(Guid id)
    {
        bool wanted = savedInputs.WantsCommit(id);

        lock (gate)
        {
            if (!wanted && following.TryGetValue(id, out Following? waiting) && waiting.Retry is { CommitFromQueue: true } retry)
            {
                waiting.Retry = retry with { Commit = null, CommitFromQueue = false };
            }

            if (!wanted && following.TryGetValue(id, out Following? entry) && entry is { IsArmedByQueue: true, IsCommitting: false })
            {
                entry.IsArmedByQueue = false;
                entry.IsDue = false;
                Arm(id, entry, false);
            }
        }

        Keep();
        Wake();
    }

    /// <inheritdoc />
    public void Resume(Guid id)
    {
        lock (gate)
        {
            Following entry = Entry(id);
            entry.IsStopped = false;
            entry.HoldUntil = null;
            entry.HoldReason = null;

            if (entry.Retry is not null)
            {
                entry.RetryAt = DateTimeOffset.UtcNow;
            }
        }

        Keep();
        Wake();
    }

    /// <inheritdoc />
    public bool StopRetries(Guid id)
    {
        lock (gate)
        {
            if (!following.TryGetValue(id, out Following? entry) || entry.Retry is null)
            {
                return false;
            }

            foreach (WorkStep waiting in new[] { entry.Retry }.Concat(entry.Backlog).ToList())
            {
                Close(id, entry, waiting, new WorkEnd(ActivityOutcome.Stopped), "It was stopped.");
            }

            entry.Retry = null;
            entry.RetryAt = null;
            entry.RetryReason = null;
            entry.Backlog.Clear();
        }

        if (HasAutomation(id))
        {
            Pause(id);
        }

        Keep();
        Wake();
        return true;
    }

    /// <inheritdoc />
    public void Pause(Guid id)
    {
        lock (gate)
        {
            Entry(id).IsStopped = true;
        }

        Keep();
    }

    /// <inheritdoc />
    public bool IsStopped(Guid id)
    {
        lock (gate)
        {
            return following.TryGetValue(id, out Following? entry) && entry.IsStopped;
        }
    }

    /// <inheritdoc />
    public bool GoesFirst(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && (entry.IsCommitting || entry.IsDue || entry.IsStarting || entry.Retry is not null))
            {
                return true;
            }
        }

        return savedInputs.Waiting.Contains(id);
    }

    /// <inheritdoc />
    public bool IsWaiting(Guid id)
    {
        lock (gate)
        {
            return following.TryGetValue(id, out Following? entry) && (entry.IsStopped || entry.IsAwaited);
        }
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
    public bool IsHolding(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && entry.Retry is not null)
            {
                return true;
            }
        }

        return IsPending(id) || savedInputs.HasQueued(id);
    }

    /// <inheritdoc />
    public bool MayBringIn(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && (entry.IsStopped || entry.IsArmed || entry.IsCommitting || entry.IsDue || entry.IsStarting || entry.Retry is not null))
            {
                return false;
            }
        }

        return activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id) && !loops.IsRunning(id) && queue.Get(id) is null && !queue.IsStarting(id) && !guard.IsSuspended(id);
    }

    /// <inheritdoc />
    public void Expect(Guid id, WorkStep step, Action<WorkEnd>? ended = null, Func<string?>? shortfall = null)
    {
        lock (gate)
        {
            Following entry = Entry(id);
            entry.Expected = step;
            entry.Offered = step;
            entry.Sending = step.Id;
            entry.Early = null;

            if (ended is not null)
            {
                entry.Endings[step.Id] = ended;
            }

            if (shortfall is not null)
            {
                entry.Shortfalls[step.Id] = shortfall;
            }
        }
    }

    /// <inheritdoc />
    public bool TryExpect(Guid id, WorkStep step, Action<WorkEnd>? ended = null, Func<string?>? shortfall = null)
    {
        lock (gate)
        {
            Following entry = Entry(id);

            if (entry.IsStarting || entry.Sending is not null)
            {
                return false;
            }

            Expect(id, step, ended, shortfall);
            return true;
        }
    }

    /// <inheritdoc />
    public void Require(Guid id, Guid stepId, Func<string?> shortfall)
    {
        lock (gate)
        {
            Entry(id).Shortfalls[stepId] = shortfall;
        }
    }

    /// <inheritdoc />
    public void Sent(Guid id, Guid stepId)
    {
        WorkResult? early = null;

        lock (gate)
        {
            Following entry = Entry(id);

            if (entry.Sending != stepId)
            {
                return;
            }

            entry.Sending = null;
            (early, entry.Early) = (entry.Early, null);

            // Not taken up yet, so it waits for the local AI.
            if (entry.Expected is { } waiting && waiting.Id == stepId)
            {
                entry.Expected = null;
                entry.Step = waiting;
                Record(id, waiting, ActivityOutcome.Waiting, "Waiting for the local AI.");
            }

            // Only the very work that was sent: one that failed meanwhile and waits again is another.
            if (entry.Retry is not null && ReferenceEquals(entry.Retry, entry.Offered))
            {
                Advance(entry);
            }
        }

        if (early is not null)
        {
            Settle(id, early);
        }

        Keep();
    }

    /// <inheritdoc />
    public void NotSent(Guid id, Guid stepId)
    {
        lock (gate)
        {
            Following entry = Entry(id);

            if (entry.Sending == stepId)
            {
                entry.Sending = null;
                entry.Early = null;
            }

            if (entry.Expected?.Id == stepId)
            {
                entry.Expected = null;
            }

            if (entry.Step?.Id == stepId)
            {
                entry.Step = null;
                journal.Change(id, stepId, current => current with { Outcome = ActivityOutcome.Waiting, EndedAt = DateTimeOffset.UtcNow, Reason = "It could not be sent." });

                // A commit turned on for work that is not coming would hold everything else back for good.
                if (!entry.IsCommitting)
                {
                    entry.IsDue = false;
                    entry.IsArmedByQueue = false;
                    Arm(id, entry, false);
                }
            }

            entry.Endings.Remove(stepId);
        }

        Keep();
        Wake();
    }

    /// <inheritdoc />
    public void Began(Guid id, string? text)
    {
        // An AI that works as part of a git action, resolving its conflicts, does no work of the user's.
        if (activity.Get(id) == WorkspaceState.Managing)
        {
            return;
        }

        bool commit;

        lock (gate)
        {
            Following entry = Entry(id);
            WorkStep step = Upcoming(entry, text) ?? new WorkStep { Source = ActivitySource.Send, Text = text ?? "Work the AI started by itself" };

            // A commit still to be made for earlier work goes along with this work, which takes the workspace first.
            if (entry is { Committed: { } earlier, IsCommitting: false })
            {
                entry.Committed = null;
                entry.IsDue = false;
                journal.Note(id, earlier.Id, "Other work started before its commit, so its changes go along with that work.");
                Close(id, entry, earlier, new WorkEnd(ActivityOutcome.Done), null);
            }
            bool resumed = step.IsSuspended;

            if (entry.Expected?.Id == step.Id)
            {
                entry.Expected = null;
            }

            // Work still open that is not taken up now is over without having been told.
            if (entry.Step is { } left && left.Id != step.Id)
            {
                journal.Change(id, left.Id, current => current.EndedAt is null ? current with { EndedAt = DateTimeOffset.UtcNow, Outcome = ActivityOutcome.Stopped } : current);
            }

            entry.Step = step = step with { Started = true, IsSuspended = false, Unfinished = null };

            // Work still being sent stays to be tried again until the send went through.
            if (entry.Retry?.Id == step.Id && entry.Sending != step.Id)
            {
                Advance(entry);
            }

            Record(id, step, ActivityOutcome.Running, null);

            if (resumed)
            {
                journal.Note(id, step.Id, "Resumed after the usage limit.");
            }

            commit = step.Commit == true && entry.UserChoice != false;
        }

        if (commit)
        {
            Arm(id);
        }

        Keep();
    }

    /// <inheritdoc />
    public string? Brief(Guid id, string text)
    {
        lock (gate)
        {
            Following entry = Entry(id);
            WorkStep? step = Upcoming(entry, text);
            List<string> parts = [];
            entry.Told = entry.Notices.Count;

            if (entry.Notices.Count > 0)
            {
                parts.Add($"Since your last turn:\n- {string.Join("\n- ", entry.Notices)}");
            }

            if (step?.Preface is { } preface)
            {
                parts.Add(preface);
            }

            if (step?.IsUnattended == true)
            {
                parts.Add(unattended);
            }

            return parts.Count == 0 ? null : $"[Auto Remote]\n{string.Join("\n\n", parts)}";
        }
    }

    /// <inheritdoc />
    public void Joined(Guid id, string text)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && entry.Step is { } step)
            {
                journal.Note(id, step.Id, $"Also sent: {Cut(text, 300)}");
            }
        }
    }

    /// <inheritdoc />
    public bool Complete(Guid id, WorkResult result)
    {
        // The AI ends because the server does: the work stays as it was, to be taken up after the restart.
        if (stopping.IsCancellationRequested)
        {
            return false;
        }

        WorkspaceState state = activity.Get(id);
        bool halted = state is WorkspaceState.Stopping or WorkspaceState.Canceling;

        // An AI that works as part of a git action, resolving its conflicts, ends no work of the user's.
        if (state != WorkspaceState.Working && !halted && !result.Stopped && !result.Failed)
        {
            return false;
        }

        lock (gate)
        {
            Following entry = Entry(id);

            // A send that fails ends the work before it says so, and whoever sends deals with it then.
            if (!result.Answered && !result.Stopped && !halted && entry.Sending is { } sending && entry.Step?.Id == sending)
            {
                entry.Early = result;
                return false;
            }
        }

        // A stop can come as the AI ends by itself, before the AI hears of it, and what it stopped is not committed either.
        if (halted || result.Stopped)
        {
            Disarm(id);
            Stopped(id);
            return false;
        }

        return Settle(id, result);
    }

    /// <inheritdoc />
    public void Abort(Guid id)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && !entry.IsCommitting && !entry.IsDue)
            {
                entry.IsArmedByQueue = false;
                Arm(id, entry, false);
            }
        }

        Keep();
        Wake();
    }

    /// <inheritdoc />
    public Task Abandon(Guid id)
    {
        bool automated = HasAutomation(id);

        lock (gate)
        {
            Following entry = Entry(id);

            if (automated)
            {
                entry.IsStopped = true;
            }

            // Work that never started is over now; work under way ends by itself.
            if (entry.Step is { } waiting && (!waiting.Started || waiting.IsSuspended))
            {
                Close(id, entry, waiting, new WorkEnd(ActivityOutcome.Stopped), "It was cancelled.");
            }
        }

        Disarm(id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool TakeBack(Guid id, string text, string error)
    {
        lock (gate)
        {
            if (!following.TryGetValue(id, out Following? entry))
            {
                return false;
            }

            // Work that failed once it was taken up waits to be tried again already.
            if (new[] { entry.Retry }.Concat(entry.Backlog).Any(retry => retry?.Text == text))
            {
                return true;
            }

            if ((entry.Step is { Started: false } waiting ? waiting : entry.Expected) is not { } step || !text.Contains(step.Text, StringComparison.Ordinal))
            {
                return false;
            }

            entry.Step = null;
            entry.Expected = null;

            if (!Retryable(id, step))
            {
                Close(id, entry, step, new WorkEnd(ActivityOutcome.Failed, kinds.IsPassing(error), error), error);
                return false;
            }

            return PutOff(id, entry, step, error, kinds.IsPassing(error), false);
        }
    }

    /// <inheritdoc />
    public string Unfinished(Guid id, string reason)
    {
        string why = string.IsNullOrWhiteSpace(reason) ? "No reason was given." : Cut(reason.Trim(), 1000);

        lock (gate)
        {
            if (!following.TryGetValue(id, out Following? entry) || entry.Step is not { Started: true } step)
            {
                return "There is no work under way to report.";
            }

            entry.Step = step with { Unfinished = why };
            journal.Note(id, step.Id, $"The AI reported that it cannot finish: {why}");
        }

        Keep();
        return "Noted. The work is not taken for done and nothing is committed for it. Say what you did in your reply.";
    }

    /// <inheritdoc />
    public void Note(Guid id, string note)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && entry.Step is { } step)
            {
                journal.Note(id, step.Id, note);
            }
        }
    }

    /// <inheritdoc />
    public void Tell(Guid id, string notice)
    {
        lock (gate)
        {
            Remember(Entry(id), notice);
        }

        Keep();
    }

    /// <inheritdoc />
    public IDisposable Observe(Action<Guid, WorkStep, WorkEnd> observer)
    {
        lock (gate)
        {
            observers.Add(observer);
        }

        return new Observation(() =>
        {
            lock (gate)
            {
                observers.Remove(observer);
            }
        });
    }

    /// <inheritdoc />
    public FollowState Describe(Guid id)
    {
        lock (gate)
        {
            return following.TryGetValue(id, out Following? entry)
                ? new FollowState(entry.IsCommitting, entry.IsDue, entry.IsStopped, entry.IsAwaited, entry.Step, entry.Retry, entry.RetryAt, entry.RetryReason, entry.HoldUntil, entry.HoldReason)
                : new FollowState(false, false, false, false, null, null, null, null, null, null);
        }
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

    // The work that the AI takes up with what it is sent: what is on its way or waits for the local AI, when that is what is sent, or what waits for the usage limit, which goes on with any message. A message of a user that got there first is not the expected work.
    private WorkStep? Upcoming(Following entry, string? text)
    {
        if (entry.Expected is { } expected && (text is null || text.Contains(expected.Text, StringComparison.Ordinal)))
        {
            return expected;
        }

        return entry.Step is { } open && (open.IsSuspended || !open.Started && (text is null || text.Contains(open.Text, StringComparison.Ordinal))) ? open : null;
    }

    // Called with the lock held: the work that was tried again is on its way, so the next one that waits comes up.
    private void Advance(Following entry)
    {
        entry.Retry = entry.Backlog.Count > 0 ? entry.Backlog[0] : null;

        if (entry.Retry is not null)
        {
            entry.Backlog.RemoveAt(0);
        }

        entry.RetryAt = entry.Retry is null ? null : DateTimeOffset.UtcNow;
        entry.RetryReason = null;
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

    // Called with the lock held: the user chose a commit for the work, so nothing queued takes it back.
    private void Choose(Guid id, Following entry)
    {
        if (!entry.IsCommitting)
        {
            entry.UserChoice = true;
            entry.IsArmedByQueue = false;
            Arm(id, entry, true);
        }
    }

    private void ArmForQueue(Guid id)
    {
        bool suspended = guard.IsSuspended(id);
        bool waiting = queue.Get(id) is not null;

        activity.Together(() =>
        {
            lock (gate)
            {
                Following entry = Entry(id);

                // Work that is waiting to be tried again is current work too.
                if (entry.Retry is { Commit: not true, CommitDeclined: false } waits && waits is not { Source: ActivitySource.Queue, Commit: false })
                {
                    entry.Retry = waits with { Commit = true, CommitFromQueue = true };
                }

                WorkStep? step = entry.Step ?? entry.Expected;

                if (entry.IsCommitting || entry.IsArmed || entry.UserChoice == false || step is { Source: ActivitySource.Queue or ActivitySource.BringIn or ActivitySource.DelegatedResults, Commit: false })
                {
                    return false;
                }

                WorkspaceState state = activity.Get(id);

                if (state == WorkspaceState.Working || suspended || waiting && step is { Started: false })
                {
                    entry.IsDue = state == WorkspaceState.Ready && suspended;
                    entry.IsArmedByQueue = true;
                    Arm(id, entry, true);
                }

                return true;
            }
        });

        Keep();
    }

    // Whether a workspace has anything that runs by itself, going only by what can be asked while states are locked.
    private bool HasAutomation(Guid id)
    {
        bool queued = savedInputs.HasQueued(id);
        bool looping = loops.Get(id) is not null;

        lock (gate)
        {
            return queued || looping || following.TryGetValue(id, out Following? entry) && (entry.Retry is not null || entry.IsAwaited || entry.IsHeld || (entry.Step ?? entry.Expected)?.IsUnattended == true);
        }
    }

    private bool Retryable(WorkStep step) => step.Source == ActivitySource.Queue || step.Preface is not null;

    private bool Retryable(Guid id, WorkStep step) => Retryable(step) && isolated?.Invoke(id) != true;

    // Ends the work that was stopped, and stops what would run by itself after it.
    private void Stopped(Guid id)
    {
        bool automated = HasAutomation(id);

        lock (gate)
        {
            Following entry = Entry(id);

            if (automated)
            {
                entry.IsStopped = true;
            }

            entry.UserChoice = null;

            if (entry.Step is { } step)
            {
                Close(id, entry, step, new WorkEnd(ActivityOutcome.Stopped), "It was stopped.");
            }
        }

        Keep();
        Wake();
    }

    // Decides what becomes of work that ended without being stopped.
    private bool Settle(Guid id, WorkResult result)
    {
        bool suspended = guard.IsSuspended(id);
        WorkStep? step;

        lock (gate)
        {
            Following entry = Entry(id);
            step = entry.Step;

            // What the AI was told has reached it once it answered.
            if (result.Answered && entry.Told > 0)
            {
                entry.Notices.RemoveRange(0, Math.Min(entry.Told, entry.Notices.Count));
                entry.Told = 0;
            }
        }

        Func<string?>? shortfall = null;

        if (step is not null)
        {
            lock (gate)
            {
                shortfall = Entry(id).Shortfalls.GetValueOrDefault(step.Id);
            }
        }

        // Asked outside the lock, and only of work that would count as done.
        if ((step?.Unfinished ?? (result is { Failed: false, Answered: true } && !suspended ? shortfall?.Invoke() : null)) is { } why && step is not null)
        {
            Disarm(id);

            lock (gate)
            {
                Following entry = Entry(id);
                entry.UserChoice = null;
                Remember(entry, $"\"{step.Title}\" was not finished: {why} What it changed is in the files and was not committed.");
                journal.Change(id, step.Id, current => current with { Report = Report(result) });
                Close(id, entry, step, new WorkEnd(ActivityOutcome.Unfinished, false, why), why, true);
            }

            Keep();
            Wake();
            return false;
        }

        // A failure that passes while the work is paused at the usage limit leaves it paused, to go on when the AI is told to resume.
        if ((result.Failed || !result.Answered) && !(suspended && kinds.IsPassing(result.Error)))
        {
            Fail(id, step, result);
            return false;
        }

        bool? decided = activity.Together<bool?>(() =>
        {
            lock (gate)
            {
                Following entry = Entry(id);

                if (suspended)
                {
                    // The work is not over: it waits for the usage limit, and is committed once nothing of it is left.
                    entry.IsDue |= entry.IsArmed;

                    if (entry.Step is { } open)
                    {
                        entry.Step = open with { IsSuspended = true };
                        Record(id, entry.Step, ActivityOutcome.Waiting, "Paused at the usage limit.");
                    }

                    return false;
                }

                if (step is not null)
                {
                    journal.Change(id, step.Id, current => current with { Report = Report(result) });
                }

                entry.UserChoice = null;

                if (entry.IsArmed && activity.Pass(id, WorkspaceState.Working, WorkspaceState.Managing))
                {
                    entry.Committed = step;
                    entry.Step = null;
                    Claim(id, entry);
                    return true;
                }

                if (entry.Step is { } done)
                {
                    Close(id, entry, done, new WorkEnd(ActivityOutcome.Done), null);
                }

                return null;
            }
        });

        Keep();
        Wake();
        return decided == true;
    }

    private void Fail(Guid id, WorkStep? step, WorkResult result)
    {
        string reason = result.Error ?? "The AI ended without a reply.";
        bool passing = !result.Answered && kinds.IsPassing(result.Error);
        bool armed;

        lock (gate)
        {
            armed = Entry(id).IsArmed;
        }

        Disarm(id);

        lock (gate)
        {
            Following entry = Entry(id);
            bool? choice = entry.UserChoice;
            entry.UserChoice = null;

            if (step is null)
            {
                return;
            }

            journal.Change(id, step.Id, current => current with { Report = Report(result) });
            entry.Step = null;

            if (Retryable(id, step))
            {
                PutOff(id, entry, step with { Commit = armed || step.Commit == true && choice != false }, reason, passing, result.Answered);
            }
            else
            {
                if (!passing)
                {
                    Remember(entry, $"\"{step.Title}\" failed: {reason} What it changed is in the files and was not committed.");
                }

                Close(id, entry, step, new WorkEnd(passing ? ActivityOutcome.Waiting : ActivityOutcome.Failed, passing, reason), reason, !passing && step.IsUnattended);
            }
        }

        Keep();
        Wake();
    }

    // Called with the lock held. Work that could not run is tried again forever; work that failed only a few times.
    private bool PutOff(Guid id, Following entry, WorkStep step, string reason, bool passing, bool answered)
    {
        if (!passing && step.Failures + 1 >= attempts)
        {
            Remember(entry, $"\"{step.Title}\" failed {attempts} times and was given up: {reason} What it changed is in the files and was not committed.");
            Close(id, entry, step, new WorkEnd(ActivityOutcome.Failed, false, reason), reason, true);
            return false;
        }

        if (entry.Retry is { } other && other.Id != step.Id)
        {
            entry.Backlog.Insert(0, other);
        }

        string? preface = answered || !passing ? $"The previous attempt at this ended with an error: {reason} Check what was already done, and then finish it." : step.Preface;
        entry.Retry = step with { Started = false, IsSuspended = false, Preface = preface, Delays = passing ? step.Delays + 1 : step.Delays, Failures = passing ? step.Failures : step.Failures + 1 };
        TimeSpan wait = kinds.Delay(passing ? step.Delays : 0);
        entry.RetryAt = DateTimeOffset.UtcNow + wait;
        entry.RetryReason = reason;
        Record(id, entry.Retry, ActivityOutcome.Waiting, reason);
        journal.Note(id, step.Id, passing ? $"The AI could not run, so it is tried again later: {reason}" : $"It failed and is tried again: {reason}");
        return true;
    }

    // Called with the lock held. Adds the activity entry of work, or brings it up to date.
    private void Record(Guid id, WorkStep step, ActivityOutcome outcome, string? reason)
    {
        if (!following.TryGetValue(id, out Following? entry))
        {
            return;
        }

        if (entry.Logged.Add(step.Id))
        {
            journal.Add(id, new ActivityEntry { Id = step.Id, StartedAt = DateTimeOffset.UtcNow, Source = step.Source, Title = step.Title, Text = Cut(step.Text, textLimit), Outcome = outcome, Reason = reason });
        }

        journal.Change(id, step.Id, current => current with { Outcome = outcome, Reason = reason, EndedAt = null });
    }

    // Called with the lock held. The work is over: its activity says how it went and whoever waits for it hears of it.
    private void Close(Guid id, Following entry, WorkStep step, WorkEnd end, string? reason, bool needsAttention = false)
    {
        if (entry.Logged.Add(step.Id))
        {
            journal.Add(id, new ActivityEntry { Id = step.Id, StartedAt = DateTimeOffset.UtcNow, Source = step.Source, Title = step.Title, Text = Cut(step.Text, textLimit) });
        }

        journal.Change(id, step.Id, current => current with { Outcome = end.Outcome, Reason = reason, EndedAt = DateTimeOffset.UtcNow, NeedsAttention = current.NeedsAttention || needsAttention });

        if (entry.Step?.Id == step.Id)
        {
            entry.Step = null;
        }

        Tell(id, entry, step, end);
    }

    private void Tell(Guid id, Following entry, WorkStep step, WorkEnd end)
    {
        List<Action> told = [];
        entry.Shortfalls.Remove(step.Id);

        if (entry.Endings.Remove(step.Id, out Action<WorkEnd>? ended))
        {
            told.Add(() => ended(end));
        }

        told.AddRange(observers.Select(observer => (Action)(() => observer(id, step, end))));

        if (told.Count > 0)
        {
            // Apart from the caller, which holds locks that whoever hears of it may want.
            _ = Task.Run(() => told.ForEach(tell => tell()), CancellationToken.None);
        }
    }

    private void Remember(Following entry, string notice)
    {
        entry.Notices.Add(notice);

        if (entry.Notices.Count > noticeLimit)
        {
            entry.Notices.RemoveAt(0);
            entry.Told = Math.Max(0, entry.Told - 1);
        }
    }

    private string? Report(WorkResult result) => result.Reply is { } reply ? Cut(reply, reportLimit) : null;

    private string Cut(string text, int limit) => text.Length > limit ? $"{text[..limit]}..." : text;

    // The workspace was just put into the state of git actions for the commit.
    private void Claim(Guid id, Following entry)
    {
        entry.IsCommitting = true;
        entry.IsCommitStopped = false;
        entry.IsArmed = false;
        entry.IsArmedByQueue = false;
        entry.IsDue = false;
        Announce(id);
        _ = Task.Run(() => Commit(id), CancellationToken.None);
    }

    private async Task Commit(Guid id)
    {
        bool claimed = true;
        bool shutdown = false;
        string? subject = null;
        bool? pushed = null;
        string? error = null;

        try
        {
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
                    string message = await versioning.CommitMessage(workspace, token);
                    await git.Commit(workspace.Folder, message, token);
                    subject = message;
                    pushed = await git.IsPushed(workspace.Folder, token);
                }, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The commit is made again after the restart.
            shutdown = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = exception.Message;
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Time = DateTimeOffset.UtcNow, Error = $"Auto commit failed: {exception.Message}" });
        }
        finally
        {
            if (!shutdown)
            {
                Committed(id, claimed, subject, pushed, error);
            }
        }
    }

    private void Committed(Guid id, bool claimed, string? subject, bool? pushed, string? error)
    {
        bool stopped;

        lock (gate)
        {
            Following entry = Entry(id);
            entry.IsCommitting = false;
            stopped = entry.IsCommitStopped;

            if (entry.Committed is { } step)
            {
                entry.Committed = null;
                string? failure = error is not null ? error : pushed == false ? "The commit could not be pushed." : null;

                if (error is not null && !stopped)
                {
                    Remember(entry, $"The changes of \"{step.Title}\" could not be committed: {error}");
                }
                else if (pushed == false)
                {
                    Remember(entry, $"The commit of \"{step.Title}\" could not be pushed.");
                }

                journal.Change(id, step.Id, current => current with { Commit = subject, Pushed = pushed, CommitError = failure });
                Close(id, entry, step, new WorkEnd(stopped ? ActivityOutcome.Stopped : ActivityOutcome.Done), null, failure is not null && !stopped);
            }

            Announce(id);
        }

        // A stop takes the workspace over, and what holds it by now is not this commit's.
        if (claimed && !stopped)
        {
            activity.Leave(id, WorkspaceState.Managing);
        }

        Keep();
        Wake();
    }

    private void RequireNotStopped(Guid id)
    {
        bool stopped;

        lock (gate)
        {
            stopped = Entry(id).IsCommitStopped;
        }

        if (stopped || activity.Get(id) != WorkspaceState.Managing)
        {
            throw new RequestException("It was stopped.");
        }
    }

    private void Restore(Guid id, StoredAutomation state)
    {
        lock (gate)
        {
            Following entry = Entry(id);
            entry.IsStopped |= state.IsStopped;
            entry.Retry = state.Retry;
            entry.Backlog.AddRange(state.Backlog);
            entry.RetryAt = state.RetryAt;
            entry.RetryReason = state.RetryReason;
            entry.Notices.AddRange(state.Notices);

            foreach (WorkStep known in new[] { state.Step, state.Retry }.OfType<WorkStep>().Concat(state.Backlog))
            {
                entry.Logged.Add(known.Id);
            }

            if (state.IsCommitDue)
            {
                // The work ended well and only its commit is left, which is made again.
                entry.Committed = state.Step;
                entry.IsArmed = true;
                entry.IsDue = true;
                return;
            }

            if (state.Step is not { } step)
            {
                return;
            }

            if (!step.Started && queue.Get(id) is not null)
            {
                // It waited for the local AI, whose queue restores it.
                entry.Step = step;
                entry.IsArmed = state.IsArmed;
                entry.UserChoice = state.UserChoice;
                return;
            }

            if (step.Source == ActivitySource.BringIn)
            {
                // Whoever brings work in asks again by itself.
                journal.Change(id, step.Id, current => current with { Outcome = ActivityOutcome.Stopped, EndedAt = DateTimeOffset.UtcNow, Reason = "The server restarted, and it is asked for again." });
                return;
            }

            // One that was waiting and is not in the local queue any more never ran: queued and resumed work is sent again, and whoever else asked for it asks again.
            if (!step.Started && !Retryable(step))
            {
                journal.Change(id, step.Id, current => current with { Outcome = ActivityOutcome.Stopped, EndedAt = DateTimeOffset.UtcNow, Reason = "The server restarted before it started." });
                return;
            }

            // Work that waited for the usage limit was not under way, so the restart does not count against it.
            int interruptions = step.Started && !step.IsSuspended ? step.Interruptions + 1 : step.Interruptions;

            if (interruptions >= attempts)
            {
                string reason = "The server stopped three times while this was under way, so it was given up.";
                Remember(entry, $"\"{step.Title}\" was given up: {reason}");
                journal.Change(id, step.Id, current => current with { Outcome = ActivityOutcome.Failed, EndedAt = DateTimeOffset.UtcNow, Reason = reason, NeedsAttention = true });
                return;
            }

            bool commit = state.IsArmed || step.Commit == true && state.UserChoice != false;
            WorkStep again = step with { Started = false, IsSuspended = false, Interruptions = interruptions, Preface = step.Started ? restarted : step.Preface, Commit = commit };

            if (entry.Retry is null)
            {
                entry.Retry = again;
                entry.RetryAt = DateTimeOffset.UtcNow;
                entry.RetryReason = "The server restarted.";
            }
            else
            {
                // What already waited goes first.
                entry.Backlog.Add(again);
            }

            journal.Change(id, step.Id, current => current with { Outcome = ActivityOutcome.Waiting, Reason = "The server restarted." });
            journal.Note(id, step.Id, "The server restarted while this was under way.");
        }
    }

    private void Keep() => store.Save(() =>
    {
        IReadOnlyDictionary<Guid, StoredAutomation>? last;

        lock (gate)
        {
            last = frozen;
        }

        return last ?? Snapshot();
    });

    private Dictionary<Guid, StoredAutomation> Snapshot()
    {
        lock (gate)
        {
            return following.Select(pair => (pair.Key, State: new StoredAutomation
            {
                IsStopped = pair.Value.IsStopped,
                IsArmed = pair.Value.IsArmed,
                // Work paused at the usage limit is not over, so after a restart it is gone on with rather than committed.
                IsCommitDue = pair.Value.IsCommitting || pair.Value is { IsDue: true, IsArmed: true, Step: not { IsSuspended: true } },
                UserChoice = pair.Value.UserChoice,
                IsArmedByQueue = pair.Value.IsArmedByQueue,
                Step = pair.Value.Committed ?? pair.Value.Step,
                Retry = pair.Value.Retry,
                Backlog = [.. pair.Value.Backlog],
                RetryAt = pair.Value.RetryAt,
                RetryReason = pair.Value.RetryReason,
                Notices = [.. pair.Value.Notices]
            })).Where(pair => pair.State.IsStopped || pair.State.IsArmed || pair.State.IsCommitDue || pair.State.Step is not null || pair.State.Retry is not null || pair.State.Notices.Count > 0).ToDictionary(pair => pair.Key, pair => pair.State);
        }
    }

    private void Follow(RemoteEvent remoteEvent)
    {
        if (stopping.IsCancellationRequested)
        {
            return;
        }

        // What a stop will make of the work is known as soon as it is asked for.
        if (remoteEvent is { Kind: RemoteEventKind.StateChanged, State: WorkspaceState.Stopping or WorkspaceState.Canceling })
        {
            bool automated = HasAutomation(remoteEvent.WorkspaceId);

            lock (gate)
            {
                Following entry = Entry(remoteEvent.WorkspaceId);

                if (entry.IsCommitting)
                {
                    entry.IsCommitStopped = true;
                }

                if (automated)
                {
                    entry.IsStopped = true;
                }
            }

            Disarm(remoteEvent.WorkspaceId);
        }

        // Waiting work that was taken out of the line never ends.
        if (remoteEvent is { Kind: RemoteEventKind.QueueChanged, Queue: null, Text: "dropped" } && !prompts.IsBusy(remoteEvent.WorkspaceId))
        {
            lock (gate)
            {
                if (following.TryGetValue(remoteEvent.WorkspaceId, out Following? entry) && entry.Step is { Started: false } waiting)
                {
                    Close(remoteEvent.WorkspaceId, entry, waiting, new WorkEnd(ActivityOutcome.Stopped), "It was taken out of the line.");
                }
            }

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
                List<Guid> again;
                List<Guid> known;

                lock (gate)
                {
                    due = [.. following.Where(entry => entry.Value.IsDue).Select(entry => entry.Key)];
                    again = [.. following.Where(entry => entry.Value.Retry is not null && entry.Value.RetryAt <= DateTimeOffset.UtcNow).Select(entry => entry.Key)];
                    known = [.. following.Keys];
                }

                foreach (Guid id in known.Union(savedInputs.Waiting))
                {
                    await Attend(id, () => LookAround(id));
                }

                // A commit that is due goes first, then work that is tried again, then the queued inputs.
                foreach (Guid id in due)
                {
                    await Attend(id, () => CommitWhenOver(id));
                }

                foreach (Guid id in again)
                {
                    await Attend(id, () => RetryWhenFree(id));
                }

                foreach (Guid id in savedInputs.Waiting)
                {
                    await Attend(id, () => StartWhenFree(id));
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
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
        catch (Exception exception) when (exception is not OutOfMemoryException && !stopping.IsCancellationRequested)
        {
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Time = DateTimeOffset.UtcNow, Error = $"What was to follow the work could not be done: {exception.Message}" });

            lock (gate)
            {
                Following entry = Entry(id);
                entry.HoldUntil = DateTimeOffset.UtcNow + kinds.Delay(1);
                entry.HoldReason = exception.Message;
            }
        }
    }

    // Kept ready, since what a stop has to know is asked while states are locked.
    private async Task LookAround(Guid id)
    {
        bool waiting = await awaited(id);
        bool holds = held?.Invoke(id) == true;

        lock (gate)
        {
            Following entry = Entry(id);
            entry.IsAwaited = waiting;
            entry.IsHeld = holds;
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
            journal.Delete(id);

            lock (gate)
            {
                following.Remove(id);
            }

            Keep();
            return null;
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

        // Asked last: a pause that seems over because the server is shutting down starts no commit.
        if (guard.IsSuspended(id) || loops.IsRunning(id) || activity.Get(id) != WorkspaceState.Ready || stopping.IsCancellationRequested)
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
                    // Work that waited for the usage limit and was never resumed is over with its commit.
                    if (entry.Step is { IsSuspended: true } open)
                    {
                        entry.Committed = open;
                        entry.Step = null;
                    }

                    Claim(id, entry);
                }

                return true;
            }
        });
    }

    private bool IsFree(Guid id, bool isRetry)
    {
        lock (gate)
        {
            if (following.TryGetValue(id, out Following? entry) && (entry.IsStopped || entry.IsArmed || entry.IsCommitting || entry.IsDue || entry.IsStarting))
            {
                return false;
            }
        }

        // Work that is tried again goes before what waits to be brought in, which in turn waits for it, so only the queue is held back by that.
        return activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id) && !loops.IsRunning(id) && queue.Get(id) is null && !queue.IsStarting(id) && !IsSending(id) && (isRetry || held?.Invoke(id) != true);
    }

    private bool IsSending(Guid id)
    {
        lock (gate)
        {
            return following.TryGetValue(id, out Following? entry) && entry.Sending is not null;
        }
    }

    // Takes the turn to send something, once the workspace is free for it and the usage limit allows it.
    private async Task<WorkspaceInfo?> Turn(Guid id, Func<bool> wanted, bool isRetry)
    {
        if (!wanted() || !IsFree(id, isRetry) || await Find(id) is not { } workspace)
        {
            return null;
        }

        if (workspace.Side is { State: not SideSessionState.Ready } || guard.IsSuspended(id) || await guard.Hold(id, workspace.Service, stopping) || !IsFree(id, isRetry) || !wanted())
        {
            return null;
        }

        lock (gate)
        {
            Following entry = Entry(id);

            // Looked at again as the turn is taken: work sent over what is on its way would leave that work unfollowed.
            if (entry.Sending is not null)
            {
                return null;
            }

            entry.IsStarting = true;
        }

        return workspace;
    }

    private async Task RetryWhenFree(Guid id)
    {
        bool Wanted()
        {
            lock (gate)
            {
                return following.TryGetValue(id, out Following? entry) && entry.Retry is not null && entry.RetryAt <= DateTimeOffset.UtcNow;
            }
        }

        if (await Turn(id, Wanted, true) is { } workspace)
        {
            // Sent apart from the pump, since starting an AI can take long and other workspaces must not wait for it.
            _ = Task.Run(() => Again(workspace), CancellationToken.None);
        }
    }

    private async Task Again(WorkspaceInfo workspace)
    {
        Guid id = workspace.Id;
        WorkStep? step;

        lock (gate)
        {
            step = Entry(id).Retry;
        }

        try
        {
            if (step is not null)
            {
                Expect(id, step);
                await Send(workspace, step);
                Sent(id, step.Id);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !stopping.IsCancellationRequested)
        {
            bool armed = Get(id) == AutoCommitState.On;
            NotSent(id, step!.Id);

            if (armed)
            {
                Disarm(id);
            }

            // Work that got there first is not a failure, and this goes after it.
            if (activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id) && queue.Get(id) is null && !queue.IsStarting(id))
            {
                lock (gate)
                {
                    Following entry = Entry(id);

                    if (entry.Retry?.Id == step.Id)
                    {
                        entry.Retry = null;
                        PutOff(id, entry, step, exception.Message, kinds.IsPassing(exception.Message), false);
                    }
                }
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

            Keep();
            Wake();
        }
    }

    private async Task StartWhenFree(Guid id)
    {
        bool Wanted()
        {
            lock (gate)
            {
                return !following.TryGetValue(id, out Following? entry) || entry.Retry is null && !entry.IsAwaited && (entry.HoldUntil is not { } until || until <= DateTimeOffset.UtcNow);
            }
        }

        // Asked here too, as the pump may not have looked since the tasks were delegated.
        if (!Wanted() || await awaited(id))
        {
            return;
        }

        if (await Turn(id, Wanted, false) is { } workspace)
        {
            _ = Task.Run(() => Start(workspace), CancellationToken.None);
        }
    }

    private async Task Start(WorkspaceInfo workspace)
    {
        Guid id = workspace.Id;
        WorkStep? step = null;
        SavedInput? sending = null;

        try
        {
            await savedInputs.SendNext(id, async input =>
            {
                sending = input;
                step = new WorkStep { Source = ActivitySource.Queue, Text = input.Text, Commit = input.AutoCommit };
                Expect(id, step);
                await Send(workspace, step);
            }, stopping);

            if (step is not null)
            {
                Sent(id, step.Id);

                lock (gate)
                {
                    Following entry = Entry(id);
                    entry.HoldUntil = null;
                    entry.HoldReason = null;
                    entry.InputFailures.Remove(sending!.Id);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !stopping.IsCancellationRequested)
        {
            if (step is not null)
            {
                bool armed = Get(id) == AutoCommitState.On;
                NotSent(id, step.Id);

                if (armed && step.Commit == true)
                {
                    Disarm(id);
                }
            }

            // Work that got there first is not a failure, and the input is sent once it is over.
            if (activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id) && queue.Get(id) is null && !queue.IsStarting(id))
            {
                await KeepForLater(id, sending, step, exception.Message);
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

            Keep();
            Wake();
        }
    }

    // An input that cannot be sent stays queued while the AI cannot run; one that fails by itself leaves the queue for the drafts after a few tries.
    private async Task KeepForLater(Guid id, SavedInput? input, WorkStep? step, string reason)
    {
        bool passing = kinds.IsPassing(reason);
        bool given = false;

        lock (gate)
        {
            Following entry = Entry(id);
            int tries = input is null ? 0 : entry.InputFailures.GetValueOrDefault(input.Id);

            if (input is not null)
            {
                entry.InputFailures[input.Id] = tries + 1;
            }

            given = !passing && input is not null && tries + 1 >= attempts;
            entry.HoldUntil = given ? null : DateTimeOffset.UtcNow + kinds.Delay(passing ? tries : 0);
            entry.HoldReason = given ? null : reason;
        }

        events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Time = DateTimeOffset.UtcNow, Error = $"A queued input could not be sent{(given ? " and was moved to the drafts" : ", and is tried again later")}: {reason}" });

        if (given && step is not null)
        {
            await savedInputs.Unqueue(id, input!.Id, CancellationToken.None);

            lock (gate)
            {
                Following entry = Entry(id);
                Remember(entry, $"The queued input \"{step.Title}\" could not be sent and was left among the drafts: {reason}");
                Close(id, entry, step, new WorkEnd(ActivityOutcome.Failed, false, reason), reason, true);
            }

            Reconsider(id);
        }
    }

    private async Task Send(WorkspaceInfo workspace, WorkStep step)
    {
        Guid id = workspace.Id;

        // A side session that was done is given new work, as one that is sent a message is.
        if (workspace.Side is not null && reopen is not null)
        {
            await reopen(id);
        }

        // Turned on only once the work has the workspace, so work that got there first keeps its own setting.
        void Taken()
        {
            if (step.Commit == true)
            {
                Arm(id);
            }
        }

        if (workspace.Service == AiService.Local)
        {
            await queue.SubmitWhenFree(id, step.Text, Taken, stopping);
        }
        else
        {
            await prompts.PromptWhenIdle(id, step.Text, Taken, stopping);
        }
    }

    private sealed class Following
    {
        public bool IsArmed { get; set; }

        public bool IsCommitting { get; set; }

        public bool IsDue { get; set; }

        public bool IsStarting { get; set; }

        public bool IsCommitStopped { get; set; }

        public bool IsStopped { get; set; }

        public bool IsAwaited { get; set; }

        public bool IsHeld { get; set; }

        public bool IsArmedByQueue { get; set; }

        public bool? UserChoice { get; set; }

        public WorkStep? Step { get; set; }

        public WorkStep? Expected { get; set; }

        public WorkStep? Committed { get; set; }

        public Guid? Sending { get; set; }

        public WorkResult? Early { get; set; }

        public WorkStep? Retry { get; set; }

        public List<WorkStep> Backlog { get; } = [];

        public WorkStep? Offered { get; set; }

        public DateTimeOffset? RetryAt { get; set; }

        public string? RetryReason { get; set; }

        public DateTimeOffset? HoldUntil { get; set; }

        public string? HoldReason { get; set; }

        public int Told { get; set; }

        public List<string> Notices { get; } = [];

        public HashSet<Guid> Logged { get; } = [];

        public Dictionary<Guid, Action<WorkEnd>> Endings { get; } = [];

        public Dictionary<Guid, Func<string?>> Shortfalls { get; } = [];

        public Dictionary<Guid, int> InputFailures { get; } = [];
    }

    private sealed class Observation(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
