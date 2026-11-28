namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Runs loops of the AI: a prompt sent over and over on an interval until a condition ends the loop or it is cancelled. A run is an ordinary prompt, sent once the workspace is free, so it takes part in everything a prompt does.
/// </summary>
internal interface IWorkspaceLoops
{
    /// <summary>
    /// Restores the loops that ran when the server stopped, which go on where they were.
    /// </summary>
    /// <param name="dataFolder">The data folder of the server, where the loops are kept from now on.</param>
    /// <param name="cancellation">Cancels the read; cancelling it later keeps what is kept for the next start.</param>
    /// <returns>A task that completes once they are restored.</returns>
    Task Open(string dataFolder, CancellationToken cancellation);

    /// <summary>
    /// Starts a loop in a workspace. The first run starts as soon as the workspace and the AI are free.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="settings">The prompt, the interval and the condition.</param>
    /// <param name="cancellation">Cancels the start.</param>
    /// <exception cref="RequestException">The settings are not valid, a loop already runs, the workspace is busy with something other than the AI, or the service reports no such usage window.</exception>
    Task Start(Guid id, LoopSettings settings, CancellationToken cancellation);

    /// <summary>
    /// Cancels a workspace's loop at once without stopping the run in progress, which finishes. Does nothing when no loop runs.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the loop no longer counts as running.</returns>
    Task Cancel(Guid id);

    /// <summary>
    /// Reads the loop that runs in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>The loop, or <see langword="null" /> when none runs.</returns>
    LoopInfo? Get(Guid id);

    /// <summary>
    /// Checks whether a run of a loop is under way in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if one is.</returns>
    bool IsRunning(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceLoops(IWorkspaceManager workspaces, IPromptService prompts, IWorkspaceActivity activity, IUsageService usage, IEventHub events, TimeSpan? pollInterval = null, TimeSpan? windowInterval = null, TimeSpan? limitInterval = null, IWindowGuard? guard = null, IWorkQueue? queue = null, Func<Guid, bool>? delivering = null, Func<IFollowUps?>? following = null) : IWorkspaceLoops
{
    private readonly ConcurrentDictionary<Guid, LoopRun> loops = new();

    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan windowCheck = windowInterval ?? TimeSpan.FromMinutes(1);

    private readonly IFailureKinds kinds = new FailureKinds(limitInterval is { } interval ? [interval] : null);

    private readonly IStoredFile file = new StoredFile([], true);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SemaphoreSlim saving = new(1, 1);

    private string? path;

    private CancellationToken stopping;

    /// <inheritdoc />
    public async Task Open(string dataFolder, CancellationToken cancellation)
    {
        stopping = cancellation;
        path = Path.Combine(dataFolder, "loops.json");
        Dictionary<Guid, StoredLoop> stored = [];

        try
        {
            if (await file.Read(path, cancellation) is { } content)
            {
                stored = content.Deserialize<StoredLoops>(options)?.Loops ?? [];
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException and not NewerFileException or UnauthorizedAccessException)
        {
        }

        foreach ((Guid id, StoredLoop loop) in stored)
        {
            try
            {
                WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
                LoopRun run = new(loop.Settings, workspace.Service, loop.StartedAt, loop.Deadline) { Runs = loop.Runs };

                if (loop.NextRunAt is { } next)
                {
                    run.Schedule(next);
                }

                if (loops.TryAdd(id, run))
                {
                    prompts.Hold(id, true);
                    _ = Run(id, run);
                }
            }
            catch (RequestException)
            {
            }
        }
    }

    /// <inheritdoc />
    public LoopInfo? Get(Guid id) => loops.TryGetValue(id, out LoopRun? run) ? run.Info() : null;

    /// <inheritdoc />
    public bool IsRunning(Guid id) => loops.TryGetValue(id, out LoopRun? run) && run.Running;

    /// <inheritdoc />
    public async Task Start(Guid id, LoopSettings settings, CancellationToken cancellation)
    {
        string prompt = settings.Prompt?.Trim() ?? string.Empty;

        if (prompt.Length == 0)
        {
            throw new RequestException("The loop's prompt is empty.");
        }

        if (settings.IntervalSeconds is < 0 or > 86400)
        {
            throw new RequestException("The interval must be between 0 seconds and 24 hours.");
        }

        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline = Deadline(settings, now);

        if (settings.Condition == LoopCondition.Runs && settings.Runs is not > 0)
        {
            throw new RequestException("Choose how many runs the loop makes.");
        }

        if (settings.Condition == LoopCondition.UntilWindow)
        {
            await Validate(workspace.Service, settings, cancellation);
        }

        // A commit that follows work ends by itself, and the loop's first run waits for it like for any work.
        if (activity.Get(id) is not (WorkspaceState.Ready or WorkspaceState.Working) && following?.Invoke()?.IsCommitting(id) != true)
        {
            throw new RequestException(activity.Describe(id));
        }

        LoopRun run = new(settings with { Prompt = prompt }, workspace.Service, now, deadline);

        if (!loops.TryAdd(id, run))
        {
            throw new RequestException("A loop already runs in this workspace.");
        }

        prompts.Hold(id, true);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Loop = run.Info() });
        await Keep();
        _ = Run(id, run);
    }

    /// <inheritdoc />
    public Task Cancel(Guid id)
    {
        if (loops.TryGetValue(id, out LoopRun? run))
        {
            End(id, run, null);
        }

        return Task.CompletedTask;
    }

    private DateTimeOffset? Deadline(LoopSettings settings, DateTimeOffset now)
    {
        DateTimeOffset? deadline = settings.Condition switch
        {
            LoopCondition.UntilTime => settings.Until ?? throw new RequestException("Choose when the loop ends."),
            LoopCondition.ForDuration => now + TimeSpan.FromSeconds(settings.DurationSeconds ?? throw new RequestException("Choose how long the loop runs.")),
            _ => null
        };

        if (deadline is { } end && (end <= now || end - now > TimeSpan.FromDays(24)))
        {
            throw new RequestException(settings.Condition == LoopCondition.UntilTime ? "The loop must end within the next 24 days." : "The loop must run for a time between a second and 24 days.");
        }

        return deadline;
    }

    private async Task Validate(AiService service, LoopSettings settings, CancellationToken cancellation)
    {
        string name = settings.Window ?? throw new RequestException("Choose the usage window to watch.");

        if (settings.Percent is not (> 0 and <= 100))
        {
            throw new RequestException("The share of the usage limit must be between 1 and 100.");
        }

        UsageStatus status = await usage.Get(service, cancellation);

        if (status.Windows.All(window => window.Name != name))
        {
            throw new RequestException($"{service.Label()} reports no usage window named {name}.");
        }
    }

    private async Task Run(Guid id, LoopRun run)
    {
        // How often in a row the AI could not run, or a run failed, which makes the loop wait longer before the next.
        int unable = 0;
        int failing = 0;

        try
        {
            // A restored loop keeps its pace.
            if (run.Next is { } due && await Wait(run, () => DateTimeOffset.UtcNow >= due) is { } over)
            {
                End(id, run, over);
                return;
            }

            while (!run.Over)
            {
                run.LastWindowCheck = DateTimeOffset.MinValue;
                // A side session waiting to be brought in goes first, and so do a commit, work to try again and queued inputs. A stopped chat and one waiting for delegated tasks wait.
                string? reason = await Wait(run, () => activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id) && queue?.Get(id) is null && guard?.IsPaused(id) != true && delivering?.Invoke(id) != true && !MustWait(id));

                if (run.Over || reason is not null)
                {
                    End(id, run, reason);
                    return;
                }

                if (run.Settings.Condition == LoopCondition.Runs && run.Runs >= run.Settings.Runs)
                {
                    End(id, run, "The loop made all its runs.");
                    return;
                }

                if (guard is not null && await guard.Hold(id, run.Service, CancellationToken.None))
                {
                    await Task.Delay(poll);
                    continue;
                }

                // Said before the prompt is sent, so nobody takes the work it starts for other work.
                Turn(id, run, true);
                WorkStep step = new() { Source = ActivitySource.Loop, Text = run.Settings.Prompt, Commit = run.Settings.AutoCommit ? true : null };
                TaskCompletionSource<WorkEnd> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
                IFollowUps? followed = following?.Invoke();

                // Work that is still on its way to the workspace would be left unfollowed if the run were sent over it.
                if (followed?.TryExpect(id, step, end => ended.TrySetResult(end)) == false)
                {
                    Turn(id, run, false);
                    await Task.Delay(poll);
                    continue;
                }

                WorkTicket? ticket = null;
                bool yielded = false;
                bool began = true;

                try
                {
                    if (queue is null)
                    {
                        await prompts.Prompt(id, run.Settings.Prompt, true, CancellationToken.None);
                    }
                    else
                    {
                        ticket = await queue.Submit(id, run.Settings.Prompt, WorkSource.Loop, CancellationToken.None);
                    }

                    followed?.Sent(id, step.Id);

                    if (ticket is { Queued: true })
                    {
                        reason = await Wait(run, () => ticket.Started.IsCompleted || GoesFirst(id) || delivering?.Invoke(id) == true);

                        if (run.Over || reason is not null)
                        {
                            End(id, run, reason);
                            return;
                        }

                        if (!ticket.Started.IsCompleted)
                        {
                            // The run gives up its place in the line, and takes a new one once what goes first is done.
                            await queue!.YieldLoop(id);
                            yielded = ticket.Started.IsCompleted;

                            // A user's message that joined the waiting run keeps its place, and the run goes with it.
                            if (!yielded)
                            {
                                reason = await Wait(run, () => ticket.Started.IsCompleted);

                                if (run.Over || reason is not null)
                                {
                                    End(id, run, reason);
                                    return;
                                }
                            }
                        }

                        began = await ticket.Started;

                        // It may have been cancelled meanwhile, and one that was is not said to run.
                        if (run.Over)
                        {
                            return;
                        }
                    }
                }
                catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
                {
                    followed?.NotSent(id, step.Id);

                    if (activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id))
                    {
                        // The loop does not end over a run that could not start: it tries again later.
                        reason = await Rest(id, run, unable++, exception.Message);

                        if (reason is not null)
                        {
                            End(id, run, reason);
                            return;
                        }

                        continue;
                    }

                    Turn(id, run, false);
                    await Task.Delay(poll);
                    continue;
                }

                if (!began)
                {
                    followed?.NotSent(id, step.Id);

                    if (yielded)
                    {
                        Turn(id, run, false);
                        continue;
                    }

                    End(id, run, "The loop stopped: its waiting run was cancelled.");
                    return;
                }

                DateTimeOffset started = DateTimeOffset.UtcNow;
                run.Schedule(started + TimeSpan.FromSeconds(run.Settings.IntervalSeconds));
                Interlocked.Increment(ref run.Runs);
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Loop = run.Info() });
                await Keep();
                int gone = 0;

                // The run is over once what follows the work says so, which is after its commit.
                reason = await Wait(run, () => !prompts.IsBusy(id) && activity.Get(id) == WorkspaceState.Ready && (followed is null || ended.Task.IsCompleted || (gone = IsFollowed(followed, id, step.Id) ? 0 : gone + 1) > 3));

                if (run.Over)
                {
                    return;
                }

                if (reason is not null)
                {
                    End(id, run, reason);
                    return;
                }

                if (ended.Task.IsCompleted && (await ended.Task).CouldNotRun)
                {
                    Interlocked.Decrement(ref run.Runs);
                    reason = await Rest(id, run, unable++, (await ended.Task).Error);

                    if (reason is not null)
                    {
                        End(id, run, reason);
                        return;
                    }

                    continue;
                }

                unable = 0;
                failing = ended.Task.IsCompleted && (await ended.Task).Outcome == ActivityOutcome.Failed ? failing + 1 : 0;

                if (run.Settings.Condition == LoopCondition.Runs && run.Runs >= run.Settings.Runs)
                {
                    End(id, run, "The loop made all its runs.");
                    return;
                }

                Turn(id, run, false);
                TimeSpan between = TimeSpan.FromSeconds(run.Settings.IntervalSeconds);
                // A loop whose runs keep failing slows down, so it does not spend usage and fill the chat as fast as it can.
                reason = failing > 0 && kinds.Delay(failing - 1) is { } pause && DateTimeOffset.UtcNow + pause > started + between
                    ? await Rest(id, run, failing - 1, null)
                    : await Wait(run, () => DateTimeOffset.UtcNow - started >= between);

                if (reason is not null)
                {
                    End(id, run, reason);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            End(id, run, $"The loop stopped: {exception.Message}");
        }
    }

    private bool GoesFirst(Guid id) => following?.Invoke()?.GoesFirst(id) == true;

    private bool MustWait(Guid id) => following?.Invoke() is { } followed && (followed.GoesFirst(id) || followed.IsWaiting(id));

    private bool IsFollowed(IFollowUps followed, Guid id, Guid stepId)
    {
        FollowState state = followed.Describe(id);
        return state.IsCommitting || state.Step?.Id == stepId || state.Retry?.Id == stepId;
    }

    private void Turn(Guid id, LoopRun run, bool running)
    {
        run.Running = running;

        if (!run.Over)
        {
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Loop = run.Info() });
        }
    }

    // Waits before a run that could not be made is tried again, which the loop shows.
    private async Task<string?> Rest(Guid id, LoopRun run, int attempt, string? why)
    {
        DateTimeOffset next = DateTimeOffset.UtcNow + kinds.Delay(attempt);
        run.Running = false;
        run.Await(next);
        if (why is not null)
        {
            following?.Invoke()?.Tell(id, $"A run of the loop could not be made: {why}");
        }

        events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Loop = run.Info() });
        string? reason = await Wait(run, () => DateTimeOffset.UtcNow >= next);
        run.Await(null);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Loop = run.Info() });
        return reason;
    }

    private async Task Keep()
    {
        // Once the server is stopping, what is kept stays as it is for the next start.
        if (path is null || stopping.IsCancellationRequested)
        {
            return;
        }

        await saving.WaitAsync();

        try
        {
            StoredLoops stored = new() { Loops = loops.ToDictionary(pair => pair.Key, pair => pair.Value.Store()) };
            await file.Write(path, JsonSerializer.SerializeToNode(stored, options)!.AsObject(), CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            saving.Release();
        }
    }

    private async Task<string?> Wait(LoopRun run, Func<bool> done)
    {
        while (!run.Over)
        {
            stopping.ThrowIfCancellationRequested();

            if (await Ended(run) is { } reason)
            {
                return reason;
            }

            if (done())
            {
                return null;
            }

            await Task.Delay(poll);
        }

        return null;
    }

    private async Task<string?> Ended(LoopRun run)
    {
        if (run.Deadline is { } deadline && DateTimeOffset.UtcNow >= deadline)
        {
            return run.Settings.Condition == LoopCondition.UntilTime ? "The loop reached its end time." : "The loop ran for the time it was given.";
        }

        if (run.Settings.Condition != LoopCondition.UntilWindow || DateTimeOffset.UtcNow - run.LastWindowCheck < windowCheck)
        {
            return null;
        }

        run.LastWindowCheck = DateTimeOffset.UtcNow;

        try
        {
            UsageStatus status = await usage.Get(run.Service, CancellationToken.None);
            return status.Windows.Any(window => window.Name == run.Settings.Window && window.PercentUsed >= run.Settings.Percent) ? $"{run.Settings.Window} reached {run.Settings.Percent:0.#}% of its limit, so the loop ended." : null;
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException)
        {
            return null;
        }
    }

    private void End(Guid id, LoopRun run, string? reason)
    {
        if (!loops.TryRemove(new KeyValuePair<Guid, LoopRun>(id, run)))
        {
            return;
        }

        run.Over = true;
        _ = Keep();
        _ = queue?.CancelLoop(id);
        prompts.Hold(id, false);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Text = reason });
    }

    private sealed class LoopRun(LoopSettings settings, AiService service, DateTimeOffset startedAt, DateTimeOffset? deadline)
    {
        private DateTimeOffset? nextRunAt;

        private DateTimeOffset? limitCheckAt;

        public int Runs;

        public volatile bool Over;

        public volatile bool Running;

        public LoopSettings Settings { get; } = settings;

        public AiService Service { get; } = service;

        public DateTimeOffset? Deadline { get; } = deadline;

        public DateTimeOffset LastWindowCheck { get; set; } = DateTimeOffset.MinValue;

        public void Await(DateTimeOffset? next)
        {
            lock (this)
            {
                limitCheckAt = next;
            }
        }

        public void Schedule(DateTimeOffset next)
        {
            lock (this)
            {
                nextRunAt = next;
            }
        }

        public DateTimeOffset? Next
        {
            get
            {
                lock (this)
                {
                    return nextRunAt;
                }
            }
        }

        public StoredLoop Store()
        {
            lock (this)
            {
                return new() { Settings = Settings, StartedAt = startedAt, Deadline = Deadline, Runs = Volatile.Read(ref Runs), NextRunAt = nextRunAt };
            }
        }

        public LoopInfo Info()
        {
            lock (this)
            {
                return new() { Settings = Settings, StartedAt = startedAt, Runs = Volatile.Read(ref Runs), NextRunAt = nextRunAt, LimitCheckAt = limitCheckAt, IsRunning = Running };
            }
        }
    }

    private sealed record StoredLoops
    {
        public Dictionary<Guid, StoredLoop> Loops { get; init; } = [];
    }

    private sealed record StoredLoop
    {
        public required LoopSettings Settings { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public DateTimeOffset? Deadline { get; init; }

        public int Runs { get; init; }

        public DateTimeOffset? NextRunAt { get; init; }
    }
}
