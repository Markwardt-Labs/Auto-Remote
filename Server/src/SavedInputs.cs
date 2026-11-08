namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps the chat inputs users saved per workspace, so every client of the server sees the same ones.
/// </summary>
internal interface ISavedInputs
{
    /// <summary>
    /// Lists the saved inputs of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The saved inputs, the last saved first.</returns>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task<IReadOnlyList<SavedInput>> List(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Saves a chat input as the newest saved input and tells every client; the oldest entries are dropped beyond the limit.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The chat input.</param>
    /// <param name="cancellation">Cancels the save.</param>
    /// <returns>The saved inputs now, the last saved first.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the text is empty or too long.</exception>
    Task<IReadOnlyList<SavedInput>> Save(Guid id, string text, CancellationToken cancellation);

    /// <summary>
    /// Removes a saved input and tells every client, so only one caller gets it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="inputId">The saved input.</param>
    /// <param name="cancellation">Cancels the removal.</param>
    /// <returns>The saved input that was removed.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the saved input is gone.</exception>
    Task<SavedInput> Take(Guid id, Guid inputId, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class SavedInputs(IWorkspaceStore store, IWorkspaceManager workspaces, IEventHub events) : ISavedInputs
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    private readonly int maxEntries = 200;

    private readonly int maxLength = 100000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> List(Guid id, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        return Newest(await store.LoadInputs(id, cancellation));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> Save(Guid id, string text, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RequestException("There is nothing to save.");
        }

        if (text.Length > maxLength)
        {
            throw new RequestException($"The input is longer than {maxLength} characters.");
        }

        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> saved = [.. Newest([.. await store.LoadInputs(id, cancellation), new SavedInput { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow }]).Take(maxEntries)];
            await store.SaveInputs(id, saved, cancellation);
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = id, SavedInputs = saved });
            return saved;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SavedInput> Take(Guid id, Guid inputId, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> saved = Newest(await store.LoadInputs(id, cancellation));
            SavedInput taken = saved.FirstOrDefault(input => input.Id == inputId) ?? throw new RequestException("That saved input is gone.");
            IReadOnlyList<SavedInput> left = [.. saved.Where(input => input.Id != inputId)];
            await store.SaveInputs(id, left, cancellation);
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = id, SavedInputs = left });
            return taken;
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<SavedInput> Newest(IEnumerable<SavedInput> inputs) => [.. inputs.OrderByDescending(input => input.Time)];
}
