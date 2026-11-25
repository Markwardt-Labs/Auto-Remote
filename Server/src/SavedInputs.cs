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

    /// <summary>
    /// Adds an entry that is a recording being turned into text, and returns at once; the conversion goes on in the background and every client is told as the entry changes. When the text is ready the entry becomes a saved input like any other, and when it fails it stays with the reason until it is discarded.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="data">The base64 WAV recording.</param>
    /// <param name="cancellation">Cancels adding the entry, not the conversion.</param>
    /// <returns>The saved inputs now, the last saved first, with the new entry.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the recording is empty.</exception>
    Task<IReadOnlyList<SavedInput>> Transcribe(Guid id, string data, CancellationToken cancellation);

    /// <summary>
    /// Removes a saved input without taking it, which also stops a conversion that is still running, and tells every client.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="inputId">The saved input.</param>
    /// <param name="cancellation">Cancels the removal.</param>
    /// <returns>The saved inputs now, the last saved first.</returns>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task<IReadOnlyList<SavedInput>> Discard(Guid id, Guid inputId, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class SavedInputs(IWorkspaceStore store, IWorkspaceManager workspaces, IEventHub events, ISpeechService speech) : ISavedInputs
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    private readonly ConcurrentDictionary<Guid, Dictionary<Guid, Conversion>> conversions = new();

    private readonly int maxEntries = 200;

    private readonly int maxLength = 100000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedInput>> List(Guid id, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            return Current(id, await store.LoadInputs(id, cancellation));
        }
        finally
        {
            gate.Release();
        }
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
            return Publish(id, Current(id, saved));
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

            if (Conversions(id).TryGetValue(inputId, out Conversion? conversion))
            {
                throw new RequestException(conversion.Entry.IsConverting ? "That input is still being turned into text." : "That recording could not be turned into text, discard it instead.");
            }

            SavedInput taken = saved.FirstOrDefault(input => input.Id == inputId) ?? throw new RequestException("That saved input is gone.");
            IReadOnlyList<SavedInput> left = [.. saved.Where(input => input.Id != inputId)];
            await store.SaveInputs(id, left, cancellation);
            Publish(id, Current(id, left));
            return taken;
        }
        finally
        {
            gate.Release();
        }
    }

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
            IReadOnlyList<SavedInput> saved = Publish(id, Current(id, await store.LoadInputs(id, cancellation)));
            _ = Convert(id, conversion, data);
            return saved;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SavedInput>> Discard(Guid id, Guid inputId, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellation);

        try
        {
            IReadOnlyList<SavedInput> stored = await store.LoadInputs(id, cancellation);

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

            return Publish(id, Current(id, stored));
        }
        finally
        {
            gate.Release();
        }
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

            IReadOnlyList<SavedInput> stored = await store.LoadInputs(id, CancellationToken.None);

            if (error is not null)
            {
                conversion.Entry = conversion.Entry with { IsConverting = false, Error = error };
            }
            else
            {
                running.Remove(conversion.Entry.Id);
                stored = [.. Newest([.. stored, conversion.Entry with { Text = text, IsConverting = false }]).Take(maxEntries)];
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

    private IReadOnlyList<SavedInput> Newest(IEnumerable<SavedInput> inputs) => [.. inputs.OrderByDescending(input => input.Time)];

    private sealed class Conversion(SavedInput entry)
    {
        public SavedInput Entry { get; set; } = entry;

        public CancellationTokenSource Cancel { get; } = new();
    }
}
