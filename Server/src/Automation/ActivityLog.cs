namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps the activity of each workspace, one file per workspace in the data folder, and tells clients what changes. Changes are made in the order they are asked for without holding up the caller.
/// </summary>
internal interface IActivityLog
{
    /// <summary>
    /// Sets where the activity is kept.
    /// </summary>
    /// <param name="dataFolder">The data folder of the server.</param>
    void Open(string dataFolder);

    /// <summary>
    /// Adds an entry.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="entry">The entry.</param>
    void Add(Guid id, ActivityEntry entry);

    /// <summary>
    /// Changes an entry; does nothing when there is no such entry.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="entryId">The entry.</param>
    /// <param name="change">Makes the new entry from the old one.</param>
    void Change(Guid id, Guid entryId, Func<ActivityEntry, ActivityEntry> change);

    /// <summary>
    /// Adds a line about something that happened to an entry.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="entryId">The entry.</param>
    /// <param name="note">What happened.</param>
    void Note(Guid id, Guid entryId, string note);

    /// <summary>
    /// Reads a page of the activity of a workspace, with every change asked for before.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="before">The cursor of the page, or <see langword="null" /> for the newest entries.</param>
    /// <param name="limit">The most entries to read.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The page.</returns>
    Task<ActivityPage> Read(Guid id, string? before, int? limit, CancellationToken cancellation);

    /// <summary>
    /// Removes the activity of a workspace that is gone.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Delete(Guid id);

    /// <summary>
    /// Waits until every change asked for so far is made.
    /// </summary>
    /// <returns>A task that completes then.</returns>
    Task Flush();
}

/// <inheritdoc />
internal sealed class ActivityLog(IEventHub events, ILog? log = null, int maxEntries = 500) : IActivityLog
{
    private readonly Lock gate = new();

    private readonly Dictionary<Guid, List<ActivityEntry>> entries = [];

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly int noteLimit = 100;

    private readonly int pageLimit = 100;

    private Task last = Task.CompletedTask;

    private string? folder;

    /// <inheritdoc />
    public void Open(string dataFolder) => folder = Path.Combine(dataFolder, "activity");

    /// <inheritdoc />
    public void Add(Guid id, ActivityEntry entry) => Queue(async () =>
    {
        List<ActivityEntry> list = await Load(id);
        list.Add(entry);

        if (list.Count > maxEntries)
        {
            list.RemoveRange(0, list.Count - maxEntries);
        }

        await Save(id, list);
        Publish(id, entry);
    });

    /// <inheritdoc />
    public void Change(Guid id, Guid entryId, Func<ActivityEntry, ActivityEntry> change) => Queue(async () =>
    {
        List<ActivityEntry> list = await Load(id);
        int index = list.FindLastIndex(entry => entry.Id == entryId);

        if (index < 0)
        {
            return;
        }

        ActivityEntry changed = change(list[index]);

        if (changed == list[index])
        {
            return;
        }

        list[index] = changed;
        await Save(id, list);
        Publish(id, changed);
    });

    /// <inheritdoc />
    public void Note(Guid id, Guid entryId, string note) => Change(id, entryId, entry => entry with { Notes = [.. entry.Notes.TakeLast(noteLimit - 1), $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {note}"] });

    /// <inheritdoc />
    public async Task<ActivityPage> Read(Guid id, string? before, int? limit, CancellationToken cancellation)
    {
        TaskCompletionSource<ActivityPage> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue(async () =>
        {
            try
            {
                List<ActivityEntry> list = await Load(id);
                int end = before is not null && int.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out int cursor) ? Math.Min(cursor, list.Count) : list.Count;
                int start = Math.Max(0, end - Math.Clamp(limit ?? 30, 1, pageLimit));
                List<ActivityEntry> page = list.GetRange(start, end - start);
                page.Reverse();
                answer.SetResult(new ActivityPage { Entries = page, Older = start > 0 ? start.ToString(CultureInfo.InvariantCulture) : null });
            }
            catch (Exception exception)
            {
                answer.SetException(exception);
            }
        });
        return await answer.Task.WaitAsync(cancellation);
    }

    /// <inheritdoc />
    public void Delete(Guid id) => Queue(() =>
    {
        entries.Remove(id);

        if (folder is not null && File.Exists(PathOf(id)))
        {
            File.Delete(PathOf(id));
        }

        return Task.CompletedTask;
    });

    /// <inheritdoc />
    public Task Flush()
    {
        lock (gate)
        {
            return last;
        }
    }

    private void Queue(Func<Task> work)
    {
        lock (gate)
        {
            last = Next(last, work);
        }
    }

    // One at a time and in order, so the list and its file are only ever touched by one change.
    private async Task Next(Task previous, Func<Task> work)
    {
        // A write that failed must not keep every later one from happening.
        await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);

        try
        {
            await work();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log?.Write($"The activity could not be written: {exception.Message}");
        }
    }

    private async Task<List<ActivityEntry>> Load(Guid id)
    {
        if (entries.TryGetValue(id, out List<ActivityEntry>? known))
        {
            return known;
        }

        List<ActivityEntry> list = [];

        if (folder is not null && File.Exists(PathOf(id)))
        {
            try
            {
                list = JsonSerializer.Deserialize<List<ActivityEntry>>(await File.ReadAllTextAsync(PathOf(id)), options) ?? [];
            }
            catch (JsonException exception)
            {
                log?.Write($"The activity of a workspace could not be read and starts over: {exception.Message}");
            }
        }

        entries[id] = list;
        return list;
    }

    private async Task Save(Guid id, List<ActivityEntry> list)
    {
        if (folder is null)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        string temporary = PathOf(id) + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(list, options));
        File.Move(temporary, PathOf(id), true);
    }

    private void Publish(Guid id, ActivityEntry entry) => events.Publish(new RemoteEvent { Kind = RemoteEventKind.ActivityChanged, WorkspaceId = id, ActivityEntry = entry });

    private string PathOf(Guid id) => Path.Combine(folder!, $"{id:N}.json");
}
