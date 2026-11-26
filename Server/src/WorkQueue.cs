namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Decides when the work of workspaces that run on the local AI starts. In the yielding mode only one local AI session works at a time and other work waits here; the work of this server's user comes before delegated work. Work of the other providers always starts at once.
/// </summary>
internal interface IWorkQueue
{
    /// <summary>
    /// Gets the identifiers of the workspaces with work waiting.
    /// </summary>
    IReadOnlyList<Guid> Waiting { get; }

    /// <summary>
    /// Gets how many more delegated tasks this server would take now: in the yielding mode one when nothing runs or waits, otherwise what is left of the limit.
    /// </summary>
    int FreeDelegatedSlots { get; }

    /// <summary>
    /// Restores the work that waited when the server stopped and starts what may start.
    /// </summary>
    /// <param name="dataFolder">The data folder of the server.</param>
    /// <param name="cancellation">Cancels the read; once the queue is open, cancelling it keeps waiting work from starting, which leaves it for the next start of the server.</param>
    /// <returns>A task that completes once the queue is restored.</returns>
    Task Open(string dataFolder, CancellationToken cancellation);

    /// <summary>
    /// Starts a prompt, steers it into the work the workspace already does, or lets it wait. A workspace has at most one waiting prompt, which later ones join.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="source">Where the prompt comes from.</param>
    /// <param name="cancellation">Cancels the wait for a start that happens at once.</param>
    /// <returns>Whether the prompt waits, and a task that tells when it starts.</returns>
    /// <exception cref="RequestException">The prompt could not be sent.</exception>
    Task<WorkTicket> Submit(Guid id, string text, WorkSource source, CancellationToken cancellation);

    /// <summary>
    /// Waits until the local AI may be used for work that is not a prompt of its own, such as writing a commit message, and holds it until the result is disposed.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>What gives the AI back when disposed.</returns>
    /// <exception cref="RequestException">The waiting work was removed from the queue.</exception>
    Task<IAsyncDisposable> Reserve(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Removes everything of a workspace that waits; work that runs is not touched.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>What was removed, the waiting prompt if there was one, or <see langword="null" /> when nothing waited.</returns>
    Task<QueueStatus?> Cancel(Guid id);

    /// <summary>
    /// Removes the waiting run of a loop, as ending the loop does; a message the user added to it keeps it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the run is removed.</returns>
    Task CancelLoop(Guid id);

    /// <summary>
    /// Gets what of a workspace waits.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>The waiting work, or <see langword="null" />.</returns>
    QueueStatus? Get(Guid id);

    /// <summary>
    /// Starts whatever may start now, as a change of the mode or of the limit can allow.
    /// </summary>
    void Pump();
}

/// <inheritdoc />
internal sealed class WorkQueue : IWorkQueue
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkQueue" /> class.
    /// </summary>
    /// <param name="workspaces">Knows the workspaces and their providers.</param>
    /// <param name="prompts">Runs the work.</param>
    /// <param name="host">Holds the mode and the limit.</param>
    /// <param name="activity">Tells whether a workspace is busy with something else.</param>
    /// <param name="savedInputs">Keeps the text of a waiting prompt that could not start.</param>
    /// <param name="events">Tells clients about the queue and the queue about work ending.</param>
    public WorkQueue(IWorkspaceManager workspaces, IPromptService prompts, IPrompterHost host, IWorkspaceActivity activity, ISavedInputs savedInputs, IEventHub events)
    {
        this.workspaces = workspaces;
        this.prompts = prompts;
        this.host = host;
        this.activity = activity;
        this.savedInputs = savedInputs;
        this.events = events;
        events.Subscribe(Follow);
    }

    private readonly IWorkspaceManager workspaces;

    private readonly IPromptService prompts;

    private readonly IPrompterHost host;

    private readonly IWorkspaceActivity activity;

    private readonly ISavedInputs savedInputs;

    private readonly IEventHub events;

    private readonly Lock gate = new();

    private readonly Lock publishing = new();

    private readonly SemaphoreSlim saving = new(1, 1);

    private readonly List<Entry> entries = [];

    private readonly List<Entry> launching = [];

    private readonly Dictionary<Guid, Slot> slots = [];

    private readonly HashSet<Guid> announced = [];

    private readonly IStoredFile stored = new StoredFile([], true);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string attachedPrefix = "Attached file: ";

    private string? file;

    private CancellationToken stopping;

    private long sequence;

    /// <inheritdoc />
    public IReadOnlyList<Guid> Waiting
    {
        get
        {
            lock (gate)
            {
                return [.. entries.Select(entry => entry.Workspace).Distinct()];
            }
        }
    }

    /// <inheritdoc />
    public int FreeDelegatedSlots
    {
        get
        {
            AiEnvironment environment = host.Get();

            lock (gate)
            {
                return environment.LocalWorkMode == LocalWorkMode.Yield
                    ? entries.Count == 0 && slots.Count == 0 ? 1 : 0
                    : Math.Max(0, environment.DelegatedTaskLimit - slots.Values.Count(slot => slot.Delegated) - entries.Count(entry => entry.Delegated));
            }
        }
    }

    /// <inheritdoc />
    public async Task Open(string dataFolder, CancellationToken cancellation)
    {
        stopping = cancellation;
        file = Path.Combine(dataFolder, "queue.json");
        List<StoredEntry> restored = [];

        try
        {
            if (await stored.Read(file, cancellation) is { } content)
            {
                restored = content.Deserialize<StoredQueue>(options)?.Entries.ToList() ?? [];
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException and not NewerFileException or UnauthorizedAccessException)
        {
        }

        // Loops do not survive a restart, and the swarm submits the delegated tasks it still has to do itself.
        foreach (StoredEntry waiting in restored.Where(entry => entry.Source == WorkSource.Direct))
        {
            try
            {
                WorkspaceInfo workspace = await workspaces.Get(waiting.WorkspaceId, cancellation);

                lock (gate)
                {
                    entries.Add(new Entry(workspace.Id, workspace.Name, false) { Text = waiting.Text, Source = waiting.Source, Sequence = ++sequence });
                }
            }
            catch (RequestException)
            {
            }
        }

        Changed();
        Pump();
    }

    /// <inheritdoc />
    public async Task<WorkTicket> Submit(Guid id, string text, WorkSource source, CancellationToken cancellation)
    {
        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);

        if (workspace.Service != AiService.Local)
        {
            await prompts.Prompt(id, text, source == WorkSource.Loop, cancellation);
            return new WorkTicket(false, Task.FromResult(true));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RequestException("The prompt is empty.");
        }

        Entry? entry = null;
        Entry? joined = null;
        bool starting = false;
        bool steering = false;

        lock (gate)
        {
            if (prompts.IsBusy(id) || slots.TryGetValue(id, out Slot? occupied) && occupied.Starting)
            {
                steering = true;
            }
            else if (entries.FirstOrDefault(waiting => waiting.Workspace == id && !waiting.IsReservation) is { } waiting)
            {
                if (source is WorkSource.Loop or WorkSource.Report)
                {
                    throw new RequestException("The workspace already has queued work.");
                }

                if (waiting.Source == WorkSource.Report)
                {
                    // The report goes along with the user's prompt, in the place it had.
                    joined = waiting;
                    entry = new Entry(id, waiting.Name, false) { Text = Merge(waiting.Text, text), Source = source, Sequence = waiting.Sequence };
                    entries[entries.IndexOf(waiting)] = entry;
                }
                else
                {
                    waiting.Text = Merge(waiting.Text, text);
                    waiting.Source = source;
                    entry = waiting;
                }
            }
            else
            {
                entry = new Entry(id, workspace.Name, false) { Text = text, Source = source, Sequence = ++sequence };

                if (CanStart(entry))
                {
                    Occupy(entry);
                    starting = true;
                }
                else
                {
                    entries.Add(entry);
                }
            }
        }

        if (steering)
        {
            await prompts.Prompt(id, text, source == WorkSource.Loop, cancellation);
            return new WorkTicket(false, Task.FromResult(true));
        }

        if (starting)
        {
            try
            {
                await prompts.Prompt(id, text, source == WorkSource.Loop, cancellation);
            }
            catch
            {
                Settle(id, true);
                entry!.Started.TrySetResult(false);
                throw;
            }

            Settle(id, true);
            entry!.Started.TrySetResult(true);
            return new WorkTicket(false, entry.Started.Task);
        }

        Changed();
        await Save();
        joined?.Started.TrySetResult(true);
        Pump();
        return new WorkTicket(true, entry!.Started.Task);
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> Reserve(Guid id, CancellationToken cancellation)
    {
        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);

        if (workspace.Service != AiService.Local)
        {
            return new Reservation(() => { });
        }

        Entry entry;
        bool now;

        lock (gate)
        {
            entry = new Entry(id, workspace.Name, true) { Sequence = ++sequence };
            now = CanStart(entry);

            if (now)
            {
                Occupy(entry);
            }
            else
            {
                entries.Add(entry);
            }
        }

        if (!now)
        {
            Changed();
            Pump();

            using CancellationTokenRegistration registration = cancellation.Register(() =>
            {
                bool removed;

                lock (gate)
                {
                    removed = entries.Remove(entry);
                }

                if (removed)
                {
                    entry.Started.TrySetCanceled(cancellation);
                    Changed();
                    Pump();
                }
            });

            if (!await entry.Started.Task)
            {
                throw new RequestException("The queued work was cancelled.");
            }
        }

        return new Reservation(() =>
        {
            lock (gate)
            {
                if (slots.TryGetValue(id, out Slot? slot))
                {
                    slot.Holds--;
                }
            }

            Settle(id, false);
        });
    }

    /// <inheritdoc />
    public async Task<QueueStatus?> Cancel(Guid id)
    {
        List<Entry> removed;
        QueueStatus? status;

        lock (gate)
        {
            status = Describe(id);
            removed = [.. entries.Where(entry => entry.Workspace == id)];
            entries.RemoveAll(entry => entry.Workspace == id);
        }

        await Dropped(removed);
        return status;
    }

    /// <inheritdoc />
    public async Task CancelLoop(Guid id)
    {
        List<Entry> removed;

        lock (gate)
        {
            removed = [.. entries.Where(entry => entry.Workspace == id && !entry.IsReservation && entry.Source == WorkSource.Loop)];
            entries.RemoveAll(removed.Contains);

            foreach (Entry run in launching.Where(entry => entry.Workspace == id && entry.Source == WorkSource.Loop))
            {
                run.IsAbandoned = true;
            }
        }

        await Dropped(removed);
    }

    /// <inheritdoc />
    public QueueStatus? Get(Guid id)
    {
        lock (gate)
        {
            return Describe(id);
        }
    }

    /// <inheritdoc />
    public void Pump()
    {
        if (stopping.IsCancellationRequested)
        {
            return;
        }

        List<Entry> starting = [];

        lock (gate)
        {
            foreach (Entry entry in Ordered().ToList())
            {
                if (CanStart(entry))
                {
                    entries.Remove(entry);
                    Occupy(entry);
                    starting.Add(entry);

                    if (!entry.IsReservation)
                    {
                        launching.Add(entry);
                    }
                }
            }
        }

        if (starting.Count == 0)
        {
            return;
        }

        Changed();

        foreach (Entry entry in starting)
        {
            if (entry.IsReservation)
            {
                entry.Started.TrySetResult(true);
            }
            else
            {
                _ = Launch(entry);
            }
        }
    }

    private async Task Launch(Entry entry)
    {
        try
        {
            await Start(entry);
        }
        finally
        {
            lock (gate)
            {
                launching.Remove(entry);
            }
        }

        Pump();
    }

    private async Task Start(Entry entry)
    {
        await Save();

        try
        {
            await prompts.Prompt(entry.Workspace, entry.Text, true, CancellationToken.None);
        }
        catch (RequestException exception) when (exception.IsHalted)
        {
            // A stopped report counts as told, so its text has to stay with the user.
            await Keep(entry, entry.Source == WorkSource.Report);
            Settle(entry.Workspace, true);
            entry.Started.TrySetResult(false);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (activity.Get(entry.Workspace) != WorkspaceState.Ready || prompts.IsBusy(entry.Workspace))
            {
                bool ended;

                lock (gate)
                {
                    ended = entry.IsAbandoned;

                    if (!ended)
                    {
                        entries.Add(entry);
                    }
                }

                Settle(entry.Workspace, true);
                await Save();

                if (ended)
                {
                    entry.Started.TrySetResult(false);
                }

                return;
            }

            events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = entry.Workspace, Time = DateTimeOffset.UtcNow, Error = $"The queued work could not start: {exception.Message}" });
            await Keep(entry);
            Settle(entry.Workspace, true);
            entry.Started.TrySetException(new RequestException(exception.Message));
            return;
        }

        Settle(entry.Workspace, true);
        entry.Started.TrySetResult(true);
    }

    // What the user wrote is not lost with a prompt that will not start.
    private async Task Keep(Entry entry, bool alsoReport = false)
    {
        Changed([entry.Workspace]);

        if (entry.Source != WorkSource.Direct && !alsoReport)
        {
            return;
        }

        try
        {
            await savedInputs.Keep(entry.Workspace, entry.Text, CancellationToken.None);
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task Dropped(List<Entry> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        foreach (Entry entry in removed)
        {
            entry.Started.TrySetResult(false);
        }

        Changed([.. removed.Where(entry => !entry.IsReservation).Select(entry => entry.Workspace)]);
        await Save();
        Pump();
    }

    private void Follow(RemoteEvent remoteEvent)
    {
        if (remoteEvent.Kind is RemoteEventKind.WorkingChanged or RemoteEventKind.StateChanged)
        {
            _ = Task.Run(() => Observe(remoteEvent.WorkspaceId));
        }
    }

    private async Task Observe(Guid id)
    {
        bool known;

        lock (gate)
        {
            known = slots.ContainsKey(id);
        }

        if (!known && prompts.IsBusy(id))
        {
            try
            {
                WorkspaceInfo workspace = await workspaces.Get(id, CancellationToken.None);

                if (workspace.Service == AiService.Local)
                {
                    lock (gate)
                    {
                        slots.TryAdd(id, new Slot(workspace.Name));
                    }
                }
            }
            catch (RequestException)
            {
            }
        }

        Settle(id, false);
    }

    private void Settle(Guid id, bool started)
    {
        lock (gate)
        {
            if (slots.TryGetValue(id, out Slot? slot))
            {
                if (started)
                {
                    slot.Starting = false;
                }

                if (!slot.Starting && slot.Holds <= 0 && !prompts.IsBusy(id))
                {
                    slots.Remove(id);
                }
            }
        }

        Changed();
        Pump();
    }

    private bool CanStart(Entry entry)
    {
        if (!Startable(entry))
        {
            return false;
        }

        AiEnvironment environment = host.Get();

        if (environment.LocalWorkMode == LocalWorkMode.Simultaneous)
        {
            return !entry.Delegated || slots.Values.Count(slot => slot.Delegated) < environment.DelegatedTaskLimit && !entries.Any(other => other != entry && other.Delegated && other.Sequence < entry.Sequence && Startable(other));
        }

        return (slots.Count == 0 || entry.IsReservation && slots.ContainsKey(entry.Workspace)) && !entries.Any(other => other != entry && Precedes(other, entry) && Startable(other));
    }

    private bool Startable(Entry entry) => entry.IsReservation || !slots.ContainsKey(entry.Workspace) && activity.Get(entry.Workspace) == WorkspaceState.Ready;

    private bool Precedes(Entry first, Entry second) => first.Delegated == second.Delegated ? first.Sequence < second.Sequence : second.Delegated;

    private IEnumerable<Entry> Ordered() => entries.OrderBy(entry => entry.Delegated).ThenBy(entry => entry.Sequence);

    private void Occupy(Entry entry)
    {
        if (!slots.TryGetValue(entry.Workspace, out Slot? slot))
        {
            slot = new Slot(entry.Name);
            slots[entry.Workspace] = slot;
        }

        slot.Delegated |= entry.Delegated;

        if (entry.IsReservation)
        {
            slot.Holds++;
        }
        else
        {
            slot.Starting = true;
        }
    }

    private QueueStatus? Describe(Guid id)
    {
        List<Entry> ordered = [.. Ordered()];

        if ((ordered.FirstOrDefault(entry => entry.Workspace == id && !entry.IsReservation) ?? ordered.FirstOrDefault(entry => entry.Workspace == id)) is not { } found)
        {
            return null;
        }

        return new QueueStatus { Position = ordered.IndexOf(found) + 1, Text = found.Text, Running = [.. slots.Where(slot => slot.Key != id).Select(slot => slot.Value.Name).Order(StringComparer.OrdinalIgnoreCase)], Delegated = found.Delegated };
    }

    // Clients cannot tell from an emptied queue whether the work starts, so a prompt that never will is marked.
    private void Changed(IReadOnlyList<Guid>? dropped = null)
    {
        lock (publishing)
        {
            List<(Guid Id, QueueStatus? Status)> changes = [];

            lock (gate)
            {
                HashSet<Guid> waiting = [.. entries.Select(entry => entry.Workspace)];
                changes.AddRange(announced.Union(dropped ?? []).Where(id => !waiting.Contains(id)).Select(id => (id, (QueueStatus?)null)));
                changes.AddRange(waiting.Select(id => (id, Describe(id))));
                announced.Clear();
                announced.UnionWith(waiting);
            }

            foreach ((Guid id, QueueStatus? status) in changes)
            {
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = id, Queue = status, Text = status is null && dropped?.Contains(id) == true ? "dropped" : null });
            }
        }
    }

    private async Task Save()
    {
        if (file is null)
        {
            return;
        }

        await saving.WaitAsync();

        try
        {
            StoredQueue queue;

            lock (gate)
            {
                queue = new StoredQueue { Entries = [.. Ordered().Where(entry => !entry.IsReservation).Select(entry => new StoredEntry { WorkspaceId = entry.Workspace, Text = entry.Text, Source = entry.Source })] };
            }

            await stored.Write(file, JsonSerializer.SerializeToNode(queue, options)!.AsObject(), CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            saving.Release();
        }
    }

    private string Merge(string first, string second)
    {
        (string firstBody, IReadOnlyList<string> firstFiles) = Split(first);
        (string secondBody, IReadOnlyList<string> secondFiles) = Split(second);
        string body = string.Join("\n\n", new[] { firstBody, secondBody }.Where(part => part.Length > 0));
        return string.Join("\n", new[] { body }.Where(part => part.Length > 0).Concat(firstFiles).Concat(secondFiles));
    }

    private (string Body, IReadOnlyList<string> Files) Split(string text)
    {
        string[] lines = text.Split('\n');
        int files = lines.Reverse().TakeWhile(line => line.StartsWith(attachedPrefix, StringComparison.Ordinal)).Count();
        return (string.Join("\n", lines[..^files]).TrimEnd(), lines[^files..]);
    }

    private sealed class Entry(Guid workspace, string name, bool isReservation)
    {
        public Guid Workspace { get; } = workspace;

        public string Name { get; } = name;

        public bool IsReservation { get; } = isReservation;

        public string Text { get; set; } = string.Empty;

        public WorkSource Source { get; set; }

        public bool Delegated => Source == WorkSource.Delegated;

        public long Sequence { get; init; }

        public bool IsAbandoned { get; set; }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Slot(string name)
    {
        public string Name { get; } = name;

        public bool Starting { get; set; }

        public int Holds { get; set; }

        public bool Delegated { get; set; }
    }

    private sealed class Reservation(Action release) : IAsyncDisposable
    {
        private Action? pending = release;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref pending, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record StoredQueue
    {
        public IReadOnlyList<StoredEntry> Entries { get; init; } = [];
    }

    private sealed record StoredEntry
    {
        public required Guid WorkspaceId { get; init; }

        public required string Text { get; init; }

        public WorkSource Source { get; init; }
    }
}
