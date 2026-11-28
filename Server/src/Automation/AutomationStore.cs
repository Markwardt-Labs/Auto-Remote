namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps on disk what each workspace does by itself, so a restart goes on where the server was.
/// </summary>
internal interface IAutomationStore
{
    /// <summary>
    /// Reads what was kept.
    /// </summary>
    /// <param name="dataFolder">The data folder of the server, where it is kept from now on.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>What each workspace did.</returns>
    Task<IReadOnlyDictionary<Guid, StoredAutomation>> Open(string dataFolder, CancellationToken cancellation);

    /// <summary>
    /// Writes what every workspace does now, in the order asked for, without holding up the caller.
    /// </summary>
    /// <param name="states">Reads what every workspace does; called when it is written.</param>
    void Save(Func<IReadOnlyDictionary<Guid, StoredAutomation>> states);

    /// <summary>
    /// Waits until every write asked for so far is done.
    /// </summary>
    /// <returns>A task that completes then.</returns>
    Task Flush();
}

/// <inheritdoc />
internal sealed class AutomationStore(ILog? log = null) : IAutomationStore
{
    private readonly IStoredFile file = new StoredFile([], true);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock gate = new();

    private Task last = Task.CompletedTask;

    private bool pending;

    private string? path;

    private string? written;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, StoredAutomation>> Open(string dataFolder, CancellationToken cancellation)
    {
        path = Path.Combine(dataFolder, "automation.json");

        try
        {
            if (await file.Read(path, cancellation) is { } content && content.Deserialize<Content>(options) is { } stored)
            {
                return stored.Workspaces;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException and not NewerFileException or UnauthorizedAccessException)
        {
            log?.Write($"What the workspaces did before could not be read: {exception.Message}");
        }

        return new Dictionary<Guid, StoredAutomation>();
    }

    /// <inheritdoc />
    public void Save(Func<IReadOnlyDictionary<Guid, StoredAutomation>> states)
    {
        lock (gate)
        {
            // A write that has not started yet reads the latest state anyway.
            if (pending || path is null)
            {
                return;
            }

            pending = true;
            last = Write(last, states);
        }
    }

    /// <inheritdoc />
    public Task Flush()
    {
        lock (gate)
        {
            return last;
        }
    }

    private async Task Write(Task previous, Func<IReadOnlyDictionary<Guid, StoredAutomation>> states)
    {
        // A write that failed must not keep every later one from happening.
        await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);

        lock (gate)
        {
            pending = false;
        }

        try
        {
            JsonObject content = JsonSerializer.SerializeToNode(new Content { Workspaces = states().ToDictionary(pair => pair.Key, pair => pair.Value) }, options)!.AsObject();
            string next = content.ToJsonString();

            if (next != written)
            {
                await file.Write(path!, content, CancellationToken.None);
                written = next;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log?.Write($"What the workspaces do could not be written: {exception.Message}");
        }
    }

    private sealed record Content
    {
        public Dictionary<Guid, StoredAutomation> Workspaces { get; init; } = [];
    }
}
