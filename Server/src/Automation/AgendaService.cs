namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Works out what is up next in each workspace and why it waits, so every client says the same, and tells them as it changes.
/// </summary>
internal interface IAgendaService
{
    /// <summary>
    /// Starts telling clients about changes.
    /// </summary>
    /// <param name="cancellation">Ends it when cancelled.</param>
    void Open(CancellationToken cancellation);

    /// <summary>
    /// Gets what is up next in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>It.</returns>
    Task<Agenda> Get(Guid id, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class AgendaService(IWorkspaceManager workspaces, IFollowUps followUps, ISavedInputs savedInputs, IWorkspaceLoops loops, IWorkQueue queue, IWindowGuard guard, IWorkspaceActivity activity, IEventHub events, Func<Guid, IReadOnlyList<BringInItem>> bringIns, TimeSpan? interval = null) : IAgendaService
{
    private readonly TimeSpan every = interval ?? TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<Guid, string> published = [];

    private readonly JsonSerializerOptions options = new();

    private readonly int titleLimit = 120;

    private IReadOnlyList<WorkspaceInfo> known = [];

    /// <inheritdoc />
    public void Open(CancellationToken cancellation) => _ = Watch(cancellation);

    /// <inheritdoc />
    public async Task<Agenda> Get(Guid id, CancellationToken cancellation)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FollowState state = followUps.Describe(id);
        LoopInfo? loop = loops.Get(id);
        QueueStatus? local = queue.Get(id);
        List<AgendaItem> items = [];

        if (state.IsCommitting || state.IsCommitDue)
        {
            items.Add(new AgendaItem { Kind = AgendaKind.Commit, Title = "Commit the changes", Waiting = state.IsCommitting ? null : "Once the work is over" });
        }

        if (state.Retry is { } retry)
        {
            items.Add(new AgendaItem { Kind = AgendaKind.Retry, Id = retry.Id, Title = retry.Title, Waiting = state.RetryReason, At = state.RetryAt });
        }

        items.AddRange(bringIns(id).Select(item => new AgendaItem { Kind = AgendaKind.BringIn, Id = item.Id, Title = item.Title, AutoCommit = item.Commit, Waiting = item.Waiting }));

        if (state.IsAwaited)
        {
            items.Add(new AgendaItem { Kind = AgendaKind.DelegatedResults, Title = "Results of delegated tasks", Waiting = "The tasks are still under way" });
        }

        if (savedInputs.HasQueued(id))
        {
            bool first = true;

            foreach (SavedInput input in (await savedInputs.List(id, cancellation)).Where(input => input.IsQueued))
            {
                items.Add(new AgendaItem { Kind = AgendaKind.Queued, Id = input.Id, Title = Title(input.Text), AutoCommit = input.AutoCommit, Waiting = first ? state.HoldReason : null, At = first ? state.HoldUntil : null });
                first = false;
            }
        }

        if (loop is not null)
        {
            items.Add(new AgendaItem { Kind = AgendaKind.Loop, Title = $"Loop: {Title(loop.Settings.Prompt)}", Waiting = loop.LimitCheckAt is null ? null : "The AI could not run", At = loop.LimitCheckAt ?? loop.NextRunAt });
        }

        if (local is not null)
        {
            items.Add(new AgendaItem { Kind = AgendaKind.LocalAi, Title = Title(local.Text), Waiting = $"Waiting for the local AI, position {local.Position}" });
        }

        return new Agenda { Items = items, IsStopped = state.IsStopped, Status = Status(id, state, items, loop, local, now) };
    }

    private string? Status(Guid id, FollowState state, IReadOnlyList<AgendaItem> items, LoopInfo? loop, QueueStatus? local, DateTimeOffset now)
    {
        int next = items.Count(item => item.Kind is AgendaKind.Retry or AgendaKind.BringIn or AgendaKind.Queued);
        string more = next > 0 ? $" · {next} up next" : string.Empty;

        if (state.IsStopped && items.Count > 0)
        {
            return $"Stopped{more}";
        }

        if (guard.Get(id) is { } limit)
        {
            return $"Paused at the usage limit: {limit.Window} is at {limit.PercentUsed:0.#}%{more}";
        }

        if (local is not null)
        {
            return $"Waiting for Local AI, position {local.Position}{more}";
        }

        if (state.Retry is not null && state.RetryAt is { } retryAt && retryAt > now)
        {
            return $"Retrying {In(retryAt - now)}: {Title(state.RetryReason ?? "it failed")}{more}";
        }

        if (state.HoldUntil is { } holdUntil && holdUntil > now && items.Any(item => item.Kind == AgendaKind.Queued))
        {
            return $"Queue tries again {In(holdUntil - now)}: {Title(state.HoldReason ?? "it could not be sent")}{more}";
        }

        if (loop?.LimitCheckAt is { } check && check > now)
        {
            return $"Loop tries again {In(check - now)}: the AI could not run{more}";
        }

        if (state.IsAwaited && (next > 0 || loop is not null))
        {
            return $"Waiting for delegated tasks{more}";
        }

        if (next > 0)
        {
            return $"{next} up next";
        }

        return loop is { IsRunning: false } && (loop.NextRunAt is not { } due || due <= now) && activity.Get(id) != WorkspaceState.Ready ? "Loop waiting for the current work" : null;
    }

    private string In(TimeSpan wait) => wait < TimeSpan.FromMinutes(1) ? "shortly" : $"in {(int)Math.Ceiling(wait.TotalMinutes)} min";

    private string Title(string text)
    {
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length > titleLimit ? $"{line[..titleLimit]}..." : line;
    }

    private async Task Watch(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(every, cancellation);
                await Look(cancellation);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Look(CancellationToken cancellation)
    {
        try
        {
            known = await workspaces.All(cancellation);
        }
        catch (Exception exception) when (exception is RequestException or IOException or ObjectDisposedException)
        {
            return;
        }

        foreach (WorkspaceInfo workspace in known)
        {
            try
            {
                Agenda agenda = await Get(workspace.Id, cancellation);
                string now = JsonSerializer.Serialize(agenda, options);
                bool empty = agenda is { Items.Count: 0, Status: null, IsStopped: false };

                // A workspace that never had anything up next has nothing to tell.
                if (published.TryGetValue(workspace.Id, out string? before) ? before != now : !empty)
                {
                    published[workspace.Id] = now;
                    events.Publish(new RemoteEvent { Kind = RemoteEventKind.AgendaChanged, WorkspaceId = workspace.Id, Agenda = agenda });
                }
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
            }
        }
    }
}
