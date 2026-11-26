namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Holds the AI back while a usage window of its provider is at or past the maximum of the global settings: it tells the AI of a workspace to pause when that happens, reads the windows again every 15 minutes, and tells the AI to resume once no window is at or past the maximum.
/// </summary>
internal interface IWindowGuard
{
    /// <summary>
    /// Starts following the work of every workspace and checking the windows of the providers it uses.
    /// </summary>
    /// <param name="cancellation">Stops the checks.</param>
    void Open(CancellationToken cancellation);

    /// <summary>
    /// Holds a workspace whose AI is idle when a usage window of its provider is at or past the maximum, which a loop asks before it sends its next prompt: the workspace is paused like one whose AI was told to pause, except that nothing is sent, and the pause ends by itself, without a resume, once no window is at or past the maximum.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="service">The AI provider of the workspace.</param>
    /// <param name="cancellation">Cancels the reading of the windows.</param>
    /// <returns><see langword="true" /> if the workspace is paused, whether it already was or is now, so nothing should give its AI work.</returns>
    Task<bool> Hold(Guid id, AiService service, CancellationToken cancellation);

    /// <summary>
    /// Checks whether a prompt of a user may go ahead, which it may not while a usage window of the provider is at or past the maximum, unless the workspace ignores the maximum for the work in progress or the prompt is the one that pauses the work.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="service">The AI provider of the workspace.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="cancellation">Cancels the reading of the windows.</param>
    /// <returns>The window that is at or past the maximum, or <see langword="null" /> when the prompt may go ahead.</returns>
    Task<LimitPause?> Over(Guid id, AiService service, string text, CancellationToken cancellation);

    /// <summary>
    /// Lets the workspace ignore the maximum until the work it does now, or the work its next prompt starts, is done, which the user confirmed: the automatic pause ends, with nothing resumed, and the guard does not pause the work again.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Exempt(Guid id);

    /// <summary>
    /// Takes back <see cref="Exempt" />, for a prompt that failed to start work.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Unexempt(Guid id);

    /// <summary>
    /// Ends the automatic pause of a workspace without resuming anything, which the user asked for, and does not pause the AI again while it finishes what it is doing.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Cancel(Guid id);

    /// <summary>
    /// Gets the automatic pause of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>The pause, or <see langword="null" /> when the workspace is not paused.</returns>
    LimitPause? Get(Guid id);

    /// <summary>
    /// Checks whether a workspace is paused automatically, in which case nothing should give its AI new work.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if the workspace is paused.</returns>
    bool IsPaused(Guid id);
}

/// <inheritdoc />
internal sealed class WindowGuard(IWorkspaceManager workspaces, IPromptService prompts, IUsageService usage, IPrompterHost host, IEventHub events, TimeProvider? time = null) : IWindowGuard
{
    private readonly ConcurrentDictionary<Guid, Held> held = new();

    private readonly ConcurrentDictionary<Guid, byte> working = new();

    private readonly ConcurrentDictionary<Guid, bool> exempt = new();

    private readonly ConcurrentDictionary<Guid, byte> cancelled = new();

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private readonly string pausePrompt = "pause current work";

    private readonly string resumePrompt = "resume unfinished work";

    private IDisposable? subscription;

    /// <summary>
    /// Gets how often the windows are read while the AI works somewhere.
    /// </summary>
    internal TimeSpan CheckEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long a paused workspace waits before the windows are read again to see whether its AI can resume.
    /// </summary>
    internal TimeSpan RecheckEvery { get; init; } = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public void Open(CancellationToken cancellation)
    {
        subscription?.Dispose();
        subscription = events.Subscribe(Follow);
        _ = Repeat(cancellation);
    }

    /// <inheritdoc />
    public async Task<bool> Hold(Guid id, AiService service, CancellationToken cancellation)
    {
        if (held.ContainsKey(id))
        {
            return true;
        }

        if (host.Get().MaxWindowPercentOf(service) is not { } max || exempt.ContainsKey(id) || Past(await Read(async () => await usage.Get(service, cancellation)), max) is not { } window)
        {
            return false;
        }

        DateTimeOffset now = clock.GetUtcNow();
        LimitPause info = new() { Window = window.Name, PercentUsed = window.PercentUsed, MaxPercent = max, PausedAt = now, NextCheckAt = now + RecheckEvery };

        if (held.TryAdd(id, new Held(service, info, info.NextCheckAt, false, true)))
        {
            Announce(id, info);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<LimitPause?> Over(Guid id, AiService service, string text, CancellationToken cancellation)
    {
        if (text == pausePrompt || exempt.ContainsKey(id))
        {
            return null;
        }

        if (Get(id) is { } pause)
        {
            return pause;
        }

        if (host.Get().MaxWindowPercentOf(service) is not { } max || Past(await Read(async () => await usage.Get(service, cancellation)), max) is not { } window)
        {
            return null;
        }

        DateTimeOffset now = clock.GetUtcNow();
        return new LimitPause { Window = window.Name, PercentUsed = window.PercentUsed, MaxPercent = max, PausedAt = now, NextCheckAt = now + RecheckEvery };
    }

    /// <inheritdoc />
    public void Exempt(Guid id)
    {
        exempt[id] = working.ContainsKey(id);
        cancelled.TryRemove(id, out _);
        Release(id);
    }

    /// <inheritdoc />
    public void Unexempt(Guid id) => exempt.TryRemove(id, out _);

    /// <inheritdoc />
    public void Cancel(Guid id)
    {
        if (working.ContainsKey(id))
        {
            cancelled[id] = 0;
        }

        Release(id);
    }

    /// <inheritdoc />
    public LimitPause? Get(Guid id) => held.TryGetValue(id, out Held? pause) ? pause.Info : null;

    /// <inheritdoc />
    public bool IsPaused(Guid id) => held.ContainsKey(id);

    /// <summary>
    /// Reads the windows of the providers of the workspaces whose AI works, pauses the ones past the maximum, and does the check that is due of every paused workspace.
    /// </summary>
    /// <param name="cancellation">Cancels the checks.</param>
    /// <returns>A task that completes when the checks are done.</returns>
    internal async Task Sweep(CancellationToken cancellation)
    {
        AiEnvironment environment = host.Get();
        Dictionary<AiService, UsageStatus?> readings = [];

        foreach (Guid id in working.Keys.Where(id => !held.ContainsKey(id) && !exempt.ContainsKey(id) && !cancelled.ContainsKey(id)))
        {
            if (await Service(id, cancellation) is not { } service || environment.MaxWindowPercentOf(service) is not { } max)
            {
                continue;
            }

            if (!readings.TryGetValue(service, out UsageStatus? status))
            {
                status = await Read(async () => await usage.Get(service, cancellation));
                readings[service] = status;
            }

            if (Past(status, max) is { } window)
            {
                await Pause(id, service, window, max);
            }
        }

        foreach ((Guid id, Held pause) in held.ToArray())
        {
            if (await Service(id, cancellation) is not null && pause.Due <= clock.GetUtcNow())
            {
                await Recheck(id, pause, environment.MaxWindowPercentOf(pause.Service) ?? 100, cancellation);
            }
        }
    }

    private async Task Repeat(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(CheckEvery, clock, cancellation);
                await Sweep(cancellation);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Follow(RemoteEvent remoteEvent)
    {
        switch (remoteEvent.Kind)
        {
            case RemoteEventKind.WorkingChanged when remoteEvent.Working == true:
                working[remoteEvent.WorkspaceId] = 0;

                if (exempt.ContainsKey(remoteEvent.WorkspaceId))
                {
                    exempt[remoteEvent.WorkspaceId] = true;
                }

                break;
            case RemoteEventKind.WorkingChanged:
                working.TryRemove(remoteEvent.WorkspaceId, out _);
                cancelled.TryRemove(remoteEvent.WorkspaceId, out _);

                if (exempt.TryGetValue(remoteEvent.WorkspaceId, out bool started) && started)
                {
                    exempt.TryRemove(remoteEvent.WorkspaceId, out _);
                }

                break;
            case RemoteEventKind.StateChanged when remoteEvent.State == WorkspaceState.Stopping:
                Release(remoteEvent.WorkspaceId);
                break;
        }
    }

    private async Task<AiService?> Service(Guid id, CancellationToken cancellation)
    {
        try
        {
            return (await workspaces.Get(id, cancellation)).Service;
        }
        catch (RequestException)
        {
            working.TryRemove(id, out _);
            exempt.TryRemove(id, out _);
            cancelled.TryRemove(id, out _);
            Release(id);
            return null;
        }
    }

    private async Task<UsageStatus?> Read(Func<Task<UsageStatus?>> reading)
    {
        try
        {
            return await reading();
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException or RequestException)
        {
            return null;
        }
    }

    private UsagePeriod? Past(UsageStatus? status, double max) => status?.Windows.Where(window => window.PercentUsed >= max).MaxBy(window => window.PercentUsed);

    private async Task Pause(Guid id, AiService service, UsagePeriod window, double max)
    {
        try
        {
            await prompts.Prompt(id, pausePrompt, false, CancellationToken.None);
        }
        catch (RequestException)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        LimitPause info = new() { Window = window.Name, PercentUsed = window.PercentUsed, MaxPercent = max, PausedAt = now, NextCheckAt = now + RecheckEvery };

        if (held.TryAdd(id, new Held(service, info, info.NextCheckAt)))
        {
            Announce(id, info);
        }
    }

    private async Task Recheck(Guid id, Held pause, double max, CancellationToken cancellation)
    {
        DateTimeOffset now = clock.GetUtcNow();

        if (!pause.Below)
        {
            UsageStatus? status = await Read(async () => await usage.Refresh(pause.Service, cancellation));
            UsagePeriod? past = Past(status, max);

            if (status is null || past is not null)
            {
                Renew(id, pause, past is null ? pause.Info with { MaxPercent = max, NextCheckAt = now + RecheckEvery } : pause.Info with { Window = past.Name, PercentUsed = past.PercentUsed, MaxPercent = max, NextCheckAt = now + RecheckEvery });
                return;
            }
        }

        if (pause.Idle)
        {
            Release(id);
            return;
        }

        if (prompts.IsBusy(id))
        {
            Retry(id, pause, now);
            return;
        }

        try
        {
            await prompts.Prompt(id, resumePrompt, true, CancellationToken.None);
        }
        catch (RequestException)
        {
            Retry(id, pause, now);
            return;
        }

        Release(id);
    }

    private void Renew(Guid id, Held pause, LimitPause info)
    {
        if (held.TryUpdate(id, pause with { Info = info, Due = info.NextCheckAt, Below = false }, pause))
        {
            Announce(id, info);
        }
    }

    private void Retry(Guid id, Held pause, DateTimeOffset now) => held.TryUpdate(id, pause with { Due = now + CheckEvery, Below = true }, pause);

    private void Release(Guid id)
    {
        if (held.TryRemove(id, out _))
        {
            Announce(id, null);
        }
    }

    private void Announce(Guid id, LimitPause? info) => events.Publish(new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = id, LimitPause = info });

    private sealed record Held(AiService Service, LimitPause Info, DateTimeOffset Due, bool Below = false, bool Idle = false);
}
