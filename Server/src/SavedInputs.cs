namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps the chat inputs users saved per workspace, so every client of the server sees the same ones.
/// </summary>
internal interface ISavedInputs
{
    /// <summary>
    /// Gets the workspaces with queued inputs that are to be sent, as far as their inputs have been read.
    /// </summary>
    IReadOnlyList<Guid> Waiting { get; }

    /// <summary>
    /// Checks whether a workspace has any queued input, paused ones included.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if it has.</returns>
    bool HasQueued(Guid id);

    /// <summary>
    /// Checks whether any queued input of a workspace is to be committed, as far as its inputs have been read.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if one is.</returns>
    bool WantsCommit(Guid id);

    /// <summary>
    /// Lists the saved inputs of a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The queued inputs in the order they are sent, followed by the others, the last saved first.</returns>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task<IReadOnlyList<SavedInput>> List(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Says once whether the inputs read of a workspace held queued ones that an older release had paused.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> the first time after they were read.</returns>
    bool TakeLegacyPause(Guid id);

    /// <summary>
    /// Stops counting the queued inputs of a workspace that is gone.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Forget(Guid id);

    /// <summary>
    /// Adds a queued input behind those already queued, resumes them all and tells every client.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The input.</param>
    /// <param name="autoCommit">Whether the changes are committed after its work.</param>
    /// <param name="cancellation">Cancels the save.</param>
    /// <returns>The saved inputs now.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the text is empty or too long.</exception>
    Task<IReadOnlyList<SavedInput>> Queue(Guid id, string text, bool autoCommit, CancellationToken cancellation);

    /// <summary>
    /// Takes a queued input out of the queue and leaves it among the drafts, as one that could not be sent.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="inputId">The input.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task Unqueue(Guid id, Guid inputId, CancellationToken cancellation);

    /// <summary>
    /// Sends the next queued input that is not paused and removes it once it was sent; nothing else can take or delete it meanwhile.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="send">Sends the input. When it throws, the input stays queued. It must not use the saved inputs itself.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns><see langword="true" /> if there was one.</returns>
    /// <exception cref="RequestException">No such workspace exists, or what <paramref name="send" /> throws.</exception>
    Task<bool> SendNext(Guid id, Func<SavedInput, Task> send, CancellationToken cancellation);

    /// <summary>
    /// Saves a chat input as the newest saved input and tells every client; the oldest entries are dropped beyond the limit.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The chat input.</param>
    /// <param name="autoCommit">Whether the changes are committed after the work it starts.</param>
    /// <param name="cancellation">Cancels the save.</param>
    /// <returns>The saved inputs now.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the text is empty or too long.</exception>
    Task<IReadOnlyList<SavedInput>> Save(Guid id, string text, bool autoCommit, CancellationToken cancellation);

    /// <summary>
    /// Sets whether the changes are committed after the work a saved input starts, and tells every client.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="inputId">The saved input.</param>
    /// <param name="autoCommit">Whether they are.</param>
    /// <param name="cancellation">Cancels the change.</param>
    /// <returns>The saved inputs now.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the saved input is gone.</exception>
    Task<IReadOnlyList<SavedInput>> SetAutoCommit(Guid id, Guid inputId, bool autoCommit, CancellationToken cancellation);

    /// <summary>
    /// Saves a prompt that will not run, whatever its length, since waiting prompts joined together can be longer than a saved input a client may send.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="cancellation">Cancels the save.</param>
    /// <returns>The saved inputs now.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the text is empty.</exception>
    Task<IReadOnlyList<SavedInput>> Keep(Guid id, string text, CancellationToken cancellation);

    /// <summary>
    /// Removes a saved input and tells every client, so only one caller gets it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="inputId">The saved input.</param>
    /// <param name="cancellation">Cancels the removal.</param>
    /// <returns>The saved input that was removed.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the saved input is gone.</exception>
    Task<SavedInput> Take(Guid id, Guid inputId, CancellationToken cancellation);

    /// <summary>
    /// Adds an entry that is a recording being turned into text, and returns at once; the conversion goes on in the background and every client is told as the entry changes. When the text is ready the entry becomes a saved input like any other, and when it fails it stays with the reason until it is discarded.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="data">The base64 WAV recording.</param>
    /// <param name="cancellation">Cancels adding the entry, not the conversion.</param>
    /// <returns>The saved inputs now, with the new entry.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the recording is empty.</exception>
    Task<IReadOnlyList<SavedInput>> Transcribe(Guid id, string data, CancellationToken cancellation);

    /// <summary>
    /// Removes a saved input without taking it, which also stops a conversion that is still running, and tells every client.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="inputId">The saved input.</param>
    /// <param name="cancellation">Cancels the removal.</param>
    /// <returns>The saved inputs now.</returns>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task<IReadOnlyList<SavedInput>> Discard(Guid id, Guid inputId, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class SavedInputs(IWorkspaceStore store, IWorkspaceManager workspaces, IEventHub events, ISpeechService speech) : ISavedInputs
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    private readonly ConcurrentDictionary<Guid, Dictionary<Guid, Conversion>> conversions = new();

    // Whether each workspace that has queued inputs has any that is to be committed.
    private readonly ConcurrentDictionary<Guid, bool> queued = new();

    private readonly ConcurrentDictionary<Guid, byte> sent = new();

    private readonly ConcurrentDictionary<Guid, byte> paused = new();

    private readonly int maxEntries = 200;

    private readonly int maxLength = 100000;

    /// <inheritdoc />
    public IReadOnlyList<Guid> Waiting => [.. queued.Keys];

    /// <inheritdoc />
    public bool HasQueued(Guid id) => queued.ContainsKey(id);

    /// <inheritdoc />
    public bool WantsCommit(Guid id) => queued.TryGetValue(id, out bool commits) && commits;

    /// <inheritdoc />
    public bool TakeLegacyPause(Guid id) => paused.TryRemove(id, out _);

    /// <inheritdoc />
    public void Forget(Guid id) => queued.TryRemove(id, out _);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> List(Guid id, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            return Current(id, Track(id, await Load(id, cancellation)));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> Queue(Guid id, string text, bool autoCommit, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RequestException("There is nothing to queue.");
        }

        if (text.Length > maxLength)
        {
            throw new RequestException($"The input is longer than {maxLength} characters.");
        }

        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> stored = await Load(id, cancellation);

            if (stored.Count(input => input.IsQueued) >= maxEntries)
            {
                throw new RequestException($"{maxEntries} inputs are queued already.");
            }

            SavedInput added = new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, IsQueued = true, AutoCommit = autoCommit };
            IReadOnlyList<SavedInput> saved = Trim(Newest([.. stored, added]), added.Id);
            await store.SaveInputs(id, saved, cancellation);
            return Publish(id, Current(id, Track(id, saved)));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Unqueue(Guid id, Guid inputId, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> stored = Track(id, await Load(id, cancellation));

            if (stored.Any(input => input.Id == inputId && input.IsQueued))
            {
                IReadOnlyList<SavedInput> saved = [.. stored.Select(input => input.Id == inputId ? input with { IsQueued = false } : input)];
                await store.SaveInputs(id, saved, cancellation);
                Publish(id, Current(id, Track(id, saved)));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendNext(Guid id, Func<SavedInput, Task> send, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> stored = Track(id, await Load(id, cancellation));

            if (Newest(stored).FirstOrDefault(input => input.IsQueued) is not { } next)
            {
                return false;
            }

            await send(next);
            IReadOnlyList<SavedInput> left = [.. stored.Where(input => input.Id != next.Id)];
            // It was sent, so it goes however the wait for it ends, also when that cannot be written down.
            try
            {
                await store.SaveInputs(id, left, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                sent[next.Id] = 0;
            }

            Publish(id, Current(id, Track(id, left)));
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SavedInput>> Save(Guid id, string text, bool autoCommit, CancellationToken cancellation) => text.Length > maxLength
        ? Task.FromException<IReadOnlyList<SavedInput>>(new RequestException($"The input is longer than {maxLength} characters."))
        : Add(id, text, autoCommit, cancellation);

    /// <inheritdoc />
    public Task<IReadOnlyList<SavedInput>> Keep(Guid id, string text, CancellationToken cancellation) => Add(id, text, true, cancellation);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> SetAutoCommit(Guid id, Guid inputId, bool autoCommit, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> stored = await Load(id, cancellation);

            if (Conversions(id).TryGetValue(inputId, out Conversion? conversion))
            {
                conversion.Entry = conversion.Entry with { AutoCommit = autoCommit };
            }
            else if (stored.Any(input => input.Id == inputId))
            {
                stored = [.. stored.Select(input => input.Id == inputId ? input with { AutoCommit = autoCommit } : input)];
                await store.SaveInputs(id, stored, cancellation);
            }
            else
            {
                throw new RequestException("That saved input is gone.");
            }

            return Publish(id, Current(id, Track(id, stored)));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<SavedInput>> Add(Guid id, string text, bool autoCommit, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RequestException("There is nothing to save.");
        }

        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);

        try
        {
            SavedInput added = new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, AutoCommit = autoCommit };
            IReadOnlyList<SavedInput> saved = Trim(Newest([.. await Load(id, cancellation), added]), added.Id);
            await store.SaveInputs(id, saved, cancellation);
            return Publish(id, Current(id, Track(id, saved)));
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
            IReadOnlyList<SavedInput> saved = Newest(await Load(id, cancellation));

            if (Conversions(id).TryGetValue(inputId, out Conversion? conversion))
            {
                throw new RequestException(conversion.Entry.IsConverting ? "That input is still being turned into text." : "That recording could not be turned into text, discard it instead.");
            }

            SavedInput taken = saved.FirstOrDefault(input => input.Id == inputId) ?? throw new RequestException("That saved input is gone.");
            IReadOnlyList<SavedInput> left = [.. saved.Where(input => input.Id != inputId)];
            await store.SaveInputs(id, left, cancellation);
            Publish(id, Current(id, Track(id, left)));
            // One that is taken back is the user's to edit and send like any other.
            return taken with { IsQueued = false };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> Transcribe(Guid id, string data, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            throw new RequestException("The recording is empty.");
        }

        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            Conversion conversion = new(new SavedInput { Id = Guid.NewGuid(), Text = string.Empty, Time = DateTimeOffset.UtcNow, IsConverting = true });
            Conversions(id)[conversion.Entry.Id] = conversion;
            IReadOnlyList<SavedInput> saved = Publish(id, Current(id, await Load(id, cancellation)));
            _ = Convert(id, conversion, data);
            return saved;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> Discard(Guid id, Guid inputId, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> stored = await Load(id, cancellation);

            if (Conversions(id).Remove(inputId, out Conversion? conversion))
            {
                if (conversion.Entry.IsConverting)
                {
                    conversion.Cancel.Cancel();
                }
            }
            else if (stored.Any(input => input.Id == inputId))
            {
                stored = [.. stored.Where(input => input.Id != inputId)];
                await store.SaveInputs(id, stored, cancellation);
            }
            else
            {
                return Current(id, stored);
            }

            return Publish(id, Current(id, Track(id, stored)));
        }
        finally
        {
            gate.Release();
        }
    }

    // A queued input that was sent but could not be taken off the disk is gone all the same, so it is never sent twice.
    private async Task<IReadOnlyList<SavedInput>> Load(Guid id, CancellationToken cancellation)
    {
        IReadOnlyList<SavedInput> stored = await store.LoadInputs(id, cancellation);

        if (stored.Any(input => input is { IsQueued: true, WasPaused: true }))
        {
            paused[id] = 0;
        }

        return [.. stored.Where(input => !sent.ContainsKey(input.Id)).Select(input => input.WasPaused is null ? input : input with { WasPaused = null })];
    }

    // The oldest of the saved ones go first. Queued inputs and the one just added are never dropped, since they would be lost without a word.
    private IReadOnlyList<SavedInput> Trim(IEnumerable<SavedInput> inputs, Guid added)
    {
        List<SavedInput> kept = [.. inputs];

        while (kept.Count > maxEntries && kept.FindLastIndex(input => !input.IsQueued && input.Id != added) is int oldest and >= 0)
        {
            kept.RemoveAt(oldest);
        }

        return kept;
    }

    private IReadOnlyList<SavedInput> Track(Guid id, IReadOnlyList<SavedInput> stored)
    {
        if (stored.Any(input => input.IsQueued))
        {
            queued[id] = stored.Any(input => input.IsQueued && input.AutoCommit);
        }
        else
        {
            queued.TryRemove(id, out _);
        }

        return stored;
    }

    private SemaphoreSlim Gate(Guid id) => gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private Dictionary<Guid, Conversion> Conversions(Guid id) => conversions.GetOrAdd(id, _ => []);

    private IReadOnlyList<SavedInput> Current(Guid id, IEnumerable<SavedInput> stored) => Newest([.. stored, .. Conversions(id).Values.Select(conversion => conversion.Entry)]);

    private IReadOnlyList<SavedInput> Publish(Guid id, IReadOnlyList<SavedInput> saved)
    {
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = id, SavedInputs = saved });
        return saved;
    }

    private async Task Convert(Guid id, Conversion conversion, string data)
    {
        string text = string.Empty;
        string? error = null;

        try
        {
            text = (await speech.Transcribe(data, conversion.Cancel.Token)).Trim();
            error = text.Length == 0 ? "Nothing was heard." : text.Length > maxLength ? $"The text is longer than {maxLength} characters." : null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (RequestException exception)
        {
            error = exception.Message;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = "The speech could not be turned into text.";
        }

        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync();

        try
        {
            Dictionary<Guid, Conversion> running = Conversions(id);

            if (!running.TryGetValue(conversion.Entry.Id, out Conversion? current) || current != conversion)
            {
                return;
            }

            // A workspace removed meanwhile keeps nothing, so nothing of it is written again.
            await workspaces.Get(id, CancellationToken.None);
            IReadOnlyList<SavedInput> stored = await Load(id, CancellationToken.None);

            if (error is not null)
            {
                conversion.Entry = conversion.Entry with { IsConverting = false, Error = error };
            }
            else
            {
                running.Remove(conversion.Entry.Id);
                stored = Trim(Newest([.. stored, conversion.Entry with { Text = text, IsConverting = false }]), conversion.Entry.Id);
                await store.SaveInputs(id, stored, CancellationToken.None);
            }

            Publish(id, Current(id, stored));
        }
        catch (Exception exception) when (exception is RequestException or IOException)
        {
            Conversions(id).Remove(conversion.Entry.Id);
        }
        finally
        {
            gate.Release();

            if (!conversion.Cancel.IsCancellationRequested)
            {
                conversion.Cancel.Dispose();
            }
        }
    }

    // The queued ones come first, the next to be sent at the top.
    private IReadOnlyList<SavedInput> Newest(IEnumerable<SavedInput> inputs) => [.. inputs.OrderByDescending(input => input.IsQueued).ThenBy(input => input.IsQueued ? input.Time.UtcTicks : -input.Time.UtcTicks)];

    private sealed class Conversion(SavedInput entry)
    {
        public SavedInput Entry { get; set; } = entry;

        public CancellationTokenSource Cancel { get; } = new();
    }
}
