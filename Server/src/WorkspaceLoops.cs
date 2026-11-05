namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Runs loops of the AI: a prompt sent over and over on an interval until a condition ends the loop or it is cancelled. A run is an ordinary prompt, sent once the workspace is free, so it takes part in everything a prompt does.
/// </summary>
internal interface IWorkspaceLoops
{
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
}

/// <inheritdoc />
internal sealed class WorkspaceLoops(IWorkspaceManager workspaces, IPromptService prompts, IWorkspaceActivity activity, IUsageService usage, IEventHub events, TimeSpan? pollInterval = null, TimeSpan? windowInterval = null) : IWorkspaceLoops
{
    private readonly ConcurrentDictionary<Guid, LoopRun> loops = new();

    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan windowCheck = windowInterval ?? TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public LoopInfo? Get(Guid id) => loops.TryGetValue(id, out LoopRun? run) ? run.Info() : null;

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

        if (activity.Get(id) is not (WorkspaceState.Ready or WorkspaceState.Working))
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
        using IDisposable watch = events.Subscribe(remoteEvent =>
        {
            if (remoteEvent.WorkspaceId == id && remoteEvent.Kind == RemoteEventKind.Failed)
            {
                run.Failure = remoteEvent.Error ?? "The AI failed.";
            }
        });

        try
        {
            while (!run.Over)
            {
                run.LastWindowCheck = DateTimeOffset.MinValue;
                string? reason = await Wait(run, () => activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id));

                if (run.Over || reason is not null)
                {
                    End(id, run, reason);
                    return;
                }

                DateTimeOffset started = DateTimeOffset.UtcNow;
                run.Failure = null;

                try
                {
                    await prompts.Prompt(id, run.Settings.Prompt, true, CancellationToken.None);
                }
                catch (RequestException exception)
                {
                    if (activity.Get(id) == WorkspaceState.Ready && !prompts.IsBusy(id))
                    {
                        End(id, run, $"The loop stopped: {exception.Message}");
                        return;
                    }

                    await Task.Delay(poll);
                    continue;
                }

                run.Schedule(started + TimeSpan.FromSeconds(run.Settings.IntervalSeconds));
                Interlocked.Increment(ref run.Runs);
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Loop = run.Info() });
                reason = await Wait(run, () => !prompts.IsBusy(id) && activity.Get(id) == WorkspaceState.Ready);

                if (run.Over)
                {
                    return;
                }

                if (reason is not null)
                {
                    End(id, run, reason);
                    return;
                }

                if (run.Failure is { } failure)
                {
                    End(id, run, $"The loop stopped: {failure}");
                    return;
                }

                if (run.Settings.Condition == LoopCondition.Runs && run.Runs >= run.Settings.Runs)
                {
                    End(id, run, "The loop made all its runs.");
                    return;
                }

                TimeSpan interval = TimeSpan.FromSeconds(run.Settings.IntervalSeconds);
                reason = await Wait(run, () => DateTimeOffset.UtcNow - started >= interval);

                if (reason is not null)
                {
                    End(id, run, reason);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            End(id, run, $"The loop stopped: {exception.Message}");
        }
    }

    private async Task<string?> Wait(LoopRun run, Func<bool> done)
    {
        while (!run.Over)
        {
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
        prompts.Hold(id, false);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = id, Text = reason });
    }

    private sealed class LoopRun(LoopSettings settings, AiService service, DateTimeOffset startedAt, DateTimeOffset? deadline)
    {
        private DateTimeOffset? nextRunAt;

        public int Runs;

        public volatile bool Over;

        public LoopSettings Settings { get; } = settings;

        public AiService Service { get; } = service;

        public DateTimeOffset? Deadline { get; } = deadline;

        public DateTimeOffset LastWindowCheck { get; set; } = DateTimeOffset.MinValue;

        public string? Failure { get; set; }

        public void Schedule(DateTimeOffset next)
        {
            lock (this)
            {
                nextRunAt = next;
            }
        }

        public LoopInfo Info()
        {
            lock (this)
            {
                return new() { Settings = Settings, StartedAt = startedAt, Runs = Volatile.Read(ref Runs), NextRunAt = nextRunAt };
            }
        }
    }
}
