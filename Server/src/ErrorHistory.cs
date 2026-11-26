namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps the errors the AI of each workspace ended with, so that a failure that scrolled out of the chat can still be read; entries older than 30 days are removed.
/// </summary>
internal interface IErrorHistory
{
    /// <summary>
    /// Starts recording every failure the server publishes, removes the entries that are too old from every workspace and goes on doing that every few hours until stopped.
    /// </summary>
    /// <param name="cancellation">Stops the periodic removal.</param>
    /// <returns>A task that completes once the first removal is done.</returns>
    Task Open(CancellationToken cancellation);

    /// <summary>
    /// Lists the errors of a workspace, removing the ones that are too old.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The errors, the newest first.</returns>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task<IReadOnlyList<ErrorEntry>> List(Guid id, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class ErrorHistory(IWorkspaceStore store, IWorkspaceManager workspaces, IEventHub events, TimeProvider? time = null) : IErrorHistory
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private readonly TimeSpan retention = TimeSpan.FromDays(30);

    private IDisposable? subscription;

    /// <summary>
    /// Gets how often the entries that are too old are removed from every workspace.
    /// </summary>
    internal TimeSpan SweepEvery { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Gets the most entries a workspace keeps, so a failing loop cannot fill the file; the oldest are dropped beyond it.
    /// </summary>
    internal int MaxEntries { get; init; } = 500;

    /// <inheritdoc />
    public async Task Open(CancellationToken cancellation)
    {
        subscription?.Dispose();
        subscription = events.Subscribe(Receive);
        await Sweep(cancellation);
        _ = Repeat(cancellation);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ErrorEntry>> List(Guid id, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        return await Prune(id, cancellation);
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.Kind == RemoteEventKind.Failed && remoteEvent.Error is { Length: > 0 } message)
        {
            _ = Record(remoteEvent.WorkspaceId, message, remoteEvent.Time);
        }
    }

    private async Task Record(Guid id, string message, DateTimeOffset? time)
    {
        try
        {
            await workspaces.Get(id, CancellationToken.None);
            SemaphoreSlim gate = gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();

            try
            {
                IReadOnlyList<ErrorEntry> recorded = Newest([.. Fresh(await store.LoadErrors(id, CancellationToken.None)), new ErrorEntry { Time = time ?? clock.GetUtcNow(), Message = message }]);
                recorded = [.. recorded.Take(MaxEntries)];
                await store.SaveErrors(id, recorded, CancellationToken.None);
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.ErrorsChanged, WorkspaceId = id, Errors = recorded });
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<IReadOnlyList<ErrorEntry>> Prune(Guid id, CancellationToken cancellation)
    {
        SemaphoreSlim gate = gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<ErrorEntry> stored = await store.LoadErrors(id, cancellation);
            IReadOnlyList<ErrorEntry> kept = Newest(Fresh(stored));

            if (kept.Count != stored.Count)
            {
                await store.SaveErrors(id, kept, cancellation);
            }

            return kept;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Sweep(CancellationToken cancellation)
    {
        try
        {
            foreach (WorkspaceInfo workspace in (await workspaces.List(cancellation)).Workspaces)
            {
                await Prune(workspace.Id, cancellation);
            }
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task Repeat(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(SweepEvery, clock, cancellation);
                await Sweep(cancellation);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private IEnumerable<ErrorEntry> Fresh(IEnumerable<ErrorEntry> errors) => errors.Where(entry => entry.Time >= clock.GetUtcNow() - retention);

    private IReadOnlyList<ErrorEntry> Newest(IEnumerable<ErrorEntry> errors) => [.. errors.OrderByDescending(entry => entry.Time)];
}
