namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps a live AI session per workspace, gives it the prompts and publishes what is said in it. The conversation itself is stored by the AI service and read back through Live AI.
/// </summary>
internal interface IPromptService
{
    /// <summary>
    /// Supplies the settings used to run the AI services.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="stopping">Ends every session when cancelled.</param>
    void Configure(ServerConfig config, CancellationToken stopping);

    /// <summary>
    /// Checks whether the AI is currently working in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if the AI is working.</returns>
    bool IsBusy(Guid id);

    /// <summary>
    /// Reads a chunk of a workspace's conversation from the AI service's stored session.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="before">The cursor of <see cref="ChatHistory.Older" /> to read before, or <see langword="null" /> for the newest messages.</param>
    /// <param name="limit">The most messages to read, or <see langword="null" /> for the default.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The chunk in order and whether the AI is working.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the cursor is not valid.</exception>
    Task<ChatHistory> History(Guid id, string? before, int? limit, CancellationToken cancellation);

    /// <summary>
    /// Reads the model and the values of its settings that a workspace's AI session runs with, making the session when the workspace has none yet.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The model, or none when the session's stored model is no longer offered, and its settings.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the AI could not be asked.</exception>
    Task<WorkspaceAi> GetAi(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Chooses the model and the values of its settings for a workspace's AI session, which applies them from its next prompt and stores them once that prompt starts; until then the server keeps the choice even when it closes an idle session.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="model">The identifier of the model.</param>
    /// <param name="settings">The values of the model's settings; a setting left out gets the model's default.</param>
    /// <param name="cancellation">Cancels the change.</param>
    /// <exception cref="RequestException">No such workspace exists, or the service does not offer the model.</exception>
    Task SetAi(Guid id, string model, IReadOnlyDictionary<string, string> settings, CancellationToken cancellation);

    /// <summary>
    /// Keeps a workspace's AI session open, or lets the idle timer close it again. A loop holds it for as long as it runs, since the session closing between two runs would only cost the next run a start.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="held">Whether the session is held open.</param>
    void Hold(Guid id, bool held);

    /// <summary>
    /// Sends a prompt to the AI in a workspace. A prompt sent while the AI is working is steered into the work at once, and because the AI may have taken it for noise, it is followed up with a turn of its own after the work unless the conversation shows the AI got it. A prompt that arrives as the work ends is queued and sent right after, in order, so none is ever lost; the workspace stays working until all of them are done. The prompt and the replies, and the state of the work, are published as events.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="cancellation">Cancels the send.</param>
    /// <exception cref="RequestException">The workspace is unknown, the prompt is empty, or the AI could not take it.</exception>
    Task Prompt(Guid id, string text, CancellationToken cancellation);

    /// <summary>
    /// Sends a prompt to the AI in a workspace like <see cref="Prompt(Guid, string, CancellationToken)" />, but only when the AI is not working at that moment, so the prompt is never steered into or queued behind work it did not start.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="onlyWhenIdle">Whether to refuse the prompt while the AI is working.</param>
    /// <param name="cancellation">Cancels the send.</param>
    /// <exception cref="RequestException">The workspace is unknown, the prompt is empty, the AI could not take it, or the AI is working and <paramref name="onlyWhenIdle" /> is set.</exception>
    Task Prompt(Guid id, string text, bool onlyWhenIdle, CancellationToken cancellation);

    /// <summary>
    /// Stores a file the user attached to a prompt, so the AI can read it by its path.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The file name; any folders in it are dropped.</param>
    /// <param name="data">The file content, base64 encoded.</param>
    /// <param name="cancellation">Cancels the store.</param>
    /// <returns>The path of the stored file on the server.</returns>
    /// <exception cref="RequestException">The workspace is unknown, the data is not base64 or it is empty or larger than the limit.</exception>
    Task<string> Attach(Guid id, string name, string data, CancellationToken cancellation);

    /// <summary>
    /// Closes a workspace's session if the AI is idle, so the next prompt opens one with the workspace's current settings; does nothing while the AI works.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the session is closed.</returns>
    Task Release(Guid id);

    /// <summary>
    /// Stops a workspace's AI at once, whatever it is doing, and forgets the prompts steered into the work or waiting behind it; the conversation stays, so the next prompt carries on from what the AI had done. Does nothing when the AI is not working.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the AI no longer counts as working.</returns>
    Task Interrupt(Guid id);

    /// <summary>
    /// Ends every workspace's AI work and closes every session, including the commands the AI started, as the server stopping needs. Waits a few seconds for the work to end first.
    /// </summary>
    /// <returns>A task that completes once the sessions are closed.</returns>
    Task Close();

    /// <summary>
    /// Closes a workspace's session and forgets it, as a deleted workspace needs.
    /// </summary>
    /// <param name="workspace">The workspace being deleted.</param>
    /// <param name="conversation">Whether the AI service's stored conversation is deleted too.</param>
    /// <returns>A task that completes once the session is gone.</returns>
    Task Forget(WorkspaceInfo workspace, bool conversation);
}

/// <inheritdoc />
internal sealed class PromptService(IWorkspaceManager workspaces, IWorkspaceStore store, Func<ServerConfig, AiService, IPromptProvider> createProvider, IEventHub events, IWorkspaceActivity activity, IWorkspaceSnapshots snapshots) : IPromptService
{
    private readonly ConcurrentDictionary<Guid, Live> lives = new();

    private readonly ConcurrentDictionary<AiService, IPromptProvider> providers = new();

    private readonly string workInstruction = "Do all of the work yourself, in the foreground. Never use subagents, background tasks, background commands, scheduled or recurring work. Run every command to completion and wait for it, and only reply once all of the work is finished. While you work, write a short status update as a normal message, saying what you have done and what comes next, at least once every five minutes so the user can follow along; split the work so that no single step runs much longer than five minutes.";

    private readonly int defaultChunk = 50;

    private readonly int maxChunk = 200;

    private readonly int maxAttachment = 2 * 1024 * 1024;

    private readonly TimeSpan idleTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan stopWait = TimeSpan.FromSeconds(15);

    private readonly TimeSpan shutdownWait = TimeSpan.FromSeconds(10);

    private readonly TimeSpan windowInterval = TimeSpan.FromMinutes(1);

    private readonly string[] images = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private readonly string interruptedMarker = "[Request interrupted by user";

    private readonly string reminderMarker = "<system-reminder>";

    private readonly string attachedPrefix = "Attached file: ";

    private readonly string steeringNote = "You were sent this message while you were working and it may have been missed. If you already acted on it, say so briefly instead of doing it again; otherwise do it now.";

    private ServerConfig config = new() { Port = 0, Key = string.Empty };

    private CancellationToken stopping;

    /// <inheritdoc />
    public void Configure(ServerConfig config, CancellationToken stopping)
    {
        this.config = config;
        this.stopping = stopping;
        providers.Clear();
    }

    /// <inheritdoc />
    public async Task Close()
    {
        List<Tally> tallies = [];

        foreach (Live live in lives.Values)
        {
            tallies.Add(live.Tally);
            Detach(live)?.Dispose();
        }

        DateTime deadline = DateTime.UtcNow + shutdownWait;

        while (tallies.Any(tally => Volatile.Read(ref tally.Count) > 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }

    /// <inheritdoc />
    public bool IsBusy(Guid id) => lives.TryGetValue(id, out Live? live) && live.Working;

    /// <inheritdoc />
    public async Task<ChatHistory> History(Guid id, string? before, int? limit, CancellationToken cancellation)
    {
        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
        PromptHistoryPoint? cursor = Decode(before);
        Live live = lives.GetOrAdd(id, _ => new Live());
        await live.Gate.WaitAsync(cancellation);

        try
        {
            if (live.Session is null && workspace.AiSessionId is null)
            {
                return new ChatHistory { Messages = [], Working = live.Working };
            }

            await Session(id, workspace, live, cancellation);

            if (!live.Working)
            {
                lock (live.Sync)
                {
                    Expire(id, live);
                }
            }

            return await Read(live.Session!, cursor, Math.Clamp(limit ?? defaultChunk, 1, maxChunk), live.Working, cancellation);
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <inheritdoc />
    public Task Prompt(Guid id, string text, CancellationToken cancellation) => Prompt(id, text, false, cancellation);

    /// <inheritdoc />
    public async Task Prompt(Guid id, string text, bool onlyWhenIdle, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RequestException("The prompt is empty.");
        }

        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
        Live live = lives.GetOrAdd(id, _ => new Live());
        await live.Gate.WaitAsync(cancellation);

        try
        {
            bool started = !live.Working;

            if (!started && onlyWhenIdle)
            {
                throw new RequestException("The AI is working.");
            }

            if (started && !activity.TryEnter(id, WorkspaceState.Working))
            {
                throw new RequestException(activity.Describe(id));
            }

            if (started && activity.Get(id) == WorkspaceState.Working)
            {
                await snapshots.Capture(workspace, cancellation);
            }

            if (activity.Get(id) is WorkspaceState.Stopping or WorkspaceState.Canceling)
            {
                throw new RequestException(activity.Describe(id));
            }

            lock (live.Sync)
            {
                StopIdle(live);
            }

            IPromptSession session = await Session(id, workspace, live, cancellation);

            if (session.Configuration.Model is null)
            {
                throw new RequestException($"{workspace.Service.Label()} has no model for this workspace. Choose one in the workspace settings.");
            }

            PromptOptions options = await Options(text, cancellation);
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = id, Message = new ChatMessage { Role = MessageRole.User, Text = text } });

            bool queued = false;
            bool steering = false;

            lock (live.Sync)
            {
                bool busy = live.Working || session.State != PromptSessionState.Waiting;

                if (busy && onlyWhenIdle)
                {
                    throw new RequestException("The AI is working.");
                }

                if (busy && session.State == PromptSessionState.Working)
                {
                    steering = true;
                    live.Steered.Add((text, options));
                }
                else if (busy)
                {
                    live.Pending.Enqueue((text, options));
                    queued = true;
                }
                else
                {
                    live.Expecting = true;
                }

                if (!queued)
                {
                    live.Sending++;
                }

                SetWorking(id, live, true);
            }

            if (queued)
            {
                return;
            }

            try
            {
                await session.Send(text, options, cancellation);
            }
            catch (Exception exception) when (exception is PromptSessionException or ObjectDisposedException or InvalidOperationException or NotSupportedException or IOException)
            {
                lock (live.Sync)
                {
                    live.Sending--;

                    if (steering)
                    {
                        int index = live.Steered.FindLastIndex(item => item.Text == text);

                        if (index >= 0)
                        {
                            live.Steered.RemoveAt(index);
                        }
                    }
                    else
                    {
                        live.Expecting = false;
                    }

                    Reconcile(id, live, session);
                }

                throw new RequestException(exception.Message);
            }

            lock (live.Sync)
            {
                live.Sending--;
                Reconcile(id, live, session);
            }
        }
        catch (Exception) when (!live.Working)
        {
            if (activity.Get(id) == WorkspaceState.Working)
            {
                snapshots.Discard(id);
            }

            activity.Leave(id, WorkspaceState.Working);
            throw;
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceAi> GetAi(Guid id, CancellationToken cancellation)
    {
        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
        Live live = lives.GetOrAdd(id, _ => new Live());
        await live.Gate.WaitAsync(cancellation);

        try
        {
            IPromptSession session = await Session(id, workspace, live, cancellation);

            if (!live.Working)
            {
                lock (live.Sync)
                {
                    Expire(id, live);
                }
            }

            return new WorkspaceAi { Model = session.Configuration.Model?.Id, ModelSettings = session.Configuration.ModelSettings.ToDictionary(pair => pair.Key, pair => pair.Value) };
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAi(Guid id, string model, IReadOnlyDictionary<string, string> settings, CancellationToken cancellation)
    {
        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
        Live live = lives.GetOrAdd(id, _ => new Live());
        await live.Gate.WaitAsync(cancellation);

        try
        {
            IPromptSession session = await Session(id, workspace, live, cancellation);

            try
            {
                await Choose(Provider(workspace.Service), session, model, settings, cancellation);
            }
            catch (PromptSessionException exception)
            {
                throw new RequestException(exception.Message);
            }

            lock (live.Sync)
            {
                live.Carried = null;
            }
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <inheritdoc />
    public void Hold(Guid id, bool held)
    {
        Live live = lives.GetOrAdd(id, _ => new Live());

        lock (live.Sync)
        {
            live.Held = held;

            if (!held && !live.Working && live.Session is not null)
            {
                Expire(id, live);
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> Attach(Guid id, string name, string data, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        byte[] bytes = new byte[data.Length / 4 * 3 + 3];

        if (!Convert.TryFromBase64String(data, bytes, out int length) || length == 0)
        {
            throw new RequestException("The attachment is empty or not valid base64.");
        }

        if (length > maxAttachment)
        {
            throw new RequestException($"The attachment is larger than {maxAttachment / 1024 / 1024} MB.");
        }

        return await store.SaveAttachment(id, name, bytes.AsMemory(0, length), cancellation);
    }

    /// <inheritdoc />
    public async Task Release(Guid id)
    {
        if (!lives.TryGetValue(id, out Live? live))
        {
            return;
        }

        await live.Gate.WaitAsync(stopping);

        try
        {
            if (!live.Working && !live.Held && Detach(live) is { } session)
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Interrupt(Guid id)
    {
        if (!lives.TryGetValue(id, out Live? live))
        {
            return;
        }

        IPromptSession? session;
        await live.Gate.WaitAsync(CancellationToken.None);

        try
        {
            lock (live.Sync)
            {
                live.Pending.Clear();
                live.Steered.Clear();
                live.Epoch++;
                StopIdle(live);
                session = live.Session;
            }
        }
        finally
        {
            live.Gate.Release();
        }

        for (int attempt = 0; attempt < 100 && session is not null && live.Working && Starting(live); attempt++)
        {
            await Task.Delay(20);
        }

        if (session is not null && live.Working)
        {
            if (Volatile.Read(ref live.Tally.Count) > 0)
            {
                Detach(live)?.Dispose();
            }
            else
            {
                await Settle(live, session);
            }
        }

        lock (live.Sync)
        {
            SetWorking(id, live, false);
        }
    }

    /// <inheritdoc />
    public async Task Forget(WorkspaceInfo workspace, bool conversation)
    {
        await Interrupt(workspace.Id);

        if (lives.TryRemove(workspace.Id, out Live? live) && Detach(live) is { } session)
        {
            await session.DisposeAsync();
        }

        if (conversation && workspace.AiSessionId is { } sessionId && IsLive(sessionId))
        {
            try
            {
                await Provider(workspace.Service).Delete(sessionId, CancellationToken.None);
            }
            catch (Exception exception) when (exception is PromptSessionException or IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    private bool Starting(Live live)
    {
        lock (live.Sync)
        {
            return live.Expecting;
        }
    }

    private async Task Settle(Live live, IPromptSession session)
    {
        DateTime deadline = DateTime.UtcNow + stopWait;

        while (session.State != PromptSessionState.Waiting && DateTime.UtcNow < deadline)
        {
            try
            {
                await session.Interrupt(CancellationToken.None);
            }
            catch (Exception exception) when (exception is PromptSessionException or ObjectDisposedException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        for (int attempt = 0; attempt < 3 && session.State == PromptSessionState.Waiting && Volatile.Read(ref live.Tally.Count) > 0; attempt++)
        {
            await Task.Delay(100);
        }

        if (session.State != PromptSessionState.Waiting || Volatile.Read(ref live.Tally.Count) > 0)
        {
            Detach(live)?.Dispose();
        }
    }

    private IPromptSession? Detach(Live live)
    {
        lock (live.Sync)
        {
            IPromptSession? session = live.Session;

            if (session?.Configuration.Model is { } model)
            {
                live.Carried = new Carry(model.Id, session.Configuration.ModelSettings.ToDictionary(pair => pair.Key, pair => pair.Value));
            }

            live.Session = null;
            live.Watch?.Dispose();
            live.Watch = null;
            return session;
        }
    }

    private bool IsLive(string sessionId) => sessionId.Length == 32 && !sessionId.Contains('-', StringComparison.Ordinal);

    private IPromptProvider Provider(AiService service) => providers.GetOrAdd(service, _ => createProvider(config, service));

    private async Task<PromptOptions> Options(string text, CancellationToken cancellation)
    {
        List<PromptAttachment> attachments = [];

        foreach (string line in text.Split('\n').Reverse().TakeWhile(line => line.StartsWith(attachedPrefix, StringComparison.Ordinal)))
        {
            string path = line[attachedPrefix.Length..].Trim();

            if (images.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path))
            {
                try
                {
                    attachments.Add(await PromptAttachment.FromFile(path, cancellation));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return new PromptOptions { Attachments = attachments };
    }

    private async Task<IPromptSession> Session(Guid id, WorkspaceInfo workspace, Live live, CancellationToken cancellation)
    {
        if (live.Session is not null && live.Service != workspace.Service && !live.Working && !live.Held)
        {
            IPromptSession? old = Detach(live);

            lock (live.Sync)
            {
                live.Carried = null;
            }

            if (old is not null)
            {
                await old.DisposeAsync();
            }
        }

        return live.Session ??= await Open(id, workspace, live, cancellation);
    }

    private async Task<IPromptSession> Open(Guid id, WorkspaceInfo workspace, Live live, CancellationToken cancellation)
    {
        IPromptProvider provider = Provider(workspace.Service);
        IPromptSession session;
        bool fresh;

        try
        {
            (session, fresh) = workspace.AiSessionId is { } stored && IsLive(stored) ? await Load(provider, stored, workspace, cancellation) : (await Create(provider, workspace, cancellation), true);

            try
            {
                if (live.Carried is { } carried)
                {
                    await Restore(provider, session, carried, cancellation);
                }
                else if (fresh && session.Configuration.Model is { } initial)
                {
                    await Choose(provider, session, initial.Id, workspace.Service.DefaultSettings(), cancellation);
                }
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception) when (exception is PromptSessionException or DirectoryNotFoundException)
        {
            throw new RequestException(exception.Message);
        }

        Tally tally = new();
        IPromptSessionConfiguration settings = session.Configuration;
        settings.IsPersistent = true;
        settings.EnableShell = true;
        settings.Instructions = workInstruction;
        settings.BeforeToolCall = (call, _) =>
        {
            if (call.Name == "shell")
            {
                Interlocked.Increment(ref tally.Count);
            }

            return Task.FromResult(PromptToolDecision.Allow);
        };
        settings.AfterToolCall = (call, _, _, _) =>
        {
            if (call.Name == "shell")
            {
                Interlocked.Decrement(ref tally.Count);
            }

            return Task.CompletedTask;
        };

        lock (live.Sync)
        {
            live.Tally = tally;
            live.Service = workspace.Service;
            live.Carried = null;
            live.Watch = session.Output.Subscribe(new Watcher(output => Handle(id, live, session, workspace.Service, output)));
        }

        return session;
    }

    private async Task Choose(IPromptProvider provider, IPromptSession session, string modelId, IReadOnlyDictionary<string, string> chosen, CancellationToken cancellation)
    {
        PromptModel model = await provider.GetModel(modelId, null, cancellation);
        session.Configuration.Model = model;

        foreach (PromptModelSetting setting in model.Settings ?? [])
        {
            session.Configuration.ModelSettings[setting.Id] = chosen.TryGetValue(setting.Id, out string? value) && setting.Accepts(value) ? value : setting.Default;
        }
    }

    private async Task Restore(IPromptProvider provider, IPromptSession session, Carry carried, CancellationToken cancellation)
    {
        try
        {
            await Choose(provider, session, carried.Model, carried.Settings, cancellation);
        }
        catch (PromptSessionModelException)
        {
        }
    }

    private async Task<PromptModel> Initial(IPromptProvider provider, AiService service, CancellationToken cancellation)
    {
        IReadOnlyList<PromptModel> listed = [.. (await provider.GetModels(null, cancellation)).Where(model => model.CallsTools != false)];
        return listed.FirstOrDefault(candidate => candidate.Id == service.DefaultModel()) ?? listed.FirstOrDefault() ?? throw new PromptSessionModelException($"{service.Label()} offers no models. Is it installed and signed in?");
    }

    private async Task<(IPromptSession Session, bool Fresh)> Load(IPromptProvider provider, string stored, WorkspaceInfo workspace, CancellationToken cancellation)
    {
        try
        {
            return (await provider.Load(stored, null, cancellation), false);
        }
        catch (Exception exception) when (exception is PromptSessionNotFoundException or DirectoryNotFoundException)
        {
            return (await Create(provider, workspace with { AiSessionId = null }, cancellation), true);
        }
    }

    private async Task<IPromptSession> Create(IPromptProvider provider, WorkspaceInfo workspace, CancellationToken cancellation)
    {
        PromptSessionOptions where = new() { WorkspacePath = workspace.Folder, Sandbox = false };
        IPromptSession session = await provider.Create(where with { Model = await Initial(provider, workspace.Service, cancellation) }, cancellation);

        try
        {
            if (workspace.AiSessionId is { } legacy)
            {
                try
                {
                    await session.AttachTo(legacy, where, cancellation);
                }
                catch (PromptSessionNotFoundException)
                {
                }
            }

            await workspaces.AttachAiSession(workspace.Id, session.Id, CancellationToken.None);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private string Encode(PromptHistoryPoint point) => $"{point.Position.ToString(CultureInfo.InvariantCulture)}:{point.Index.ToString(CultureInfo.InvariantCulture)}";

    private PromptHistoryPoint? Decode(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        string[] parts = cursor.Split(':');

        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long position) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            throw new RequestException("The history cursor is not valid.");
        }

        return new PromptHistoryPoint(position, index);
    }

    private async Task<ChatHistory> Read(IPromptSession session, PromptHistoryPoint? before, int want, bool working, CancellationToken cancellation)
    {
        List<ChatMessage> newestFirst = [];
        PromptHistoryPoint? last = null;
        bool more = false;

        try
        {
            IAsyncEnumerable<PromptHistoryEntry> entries = session.GetHistory(cancellation);

            if (before is { } point)
            {
                entries = entries.OlderThan(point);
            }

            await foreach (PromptHistoryEntry entry in entries.WithCancellation(cancellation))
            {
                if (Message(entry) is not { } message)
                {
                    if (newestFirst.Count < want)
                    {
                        last = entry.Point;
                    }

                    continue;
                }

                if (newestFirst.Count == want)
                {
                    more = true;
                    break;
                }

                newestFirst.Add(message);
                last = entry.Point;
            }
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException)
        {
            throw new RequestException($"The conversation could not be read: {exception.Message}");
        }

        newestFirst.Reverse();
        return new ChatHistory { Messages = newestFirst, Older = more && last is { } cursor ? Encode(cursor) : null, Working = working };
    }

    private ChatMessage? Message(PromptHistoryEntry entry)
    {
        MessageRole? role = entry.Event switch
        {
            PromptInputEvent => MessageRole.User,
            PromptMessageEvent => MessageRole.Assistant,
            _ => null
        };

        if (role is not { } found || string.IsNullOrWhiteSpace(entry.Text) || entry.Text.StartsWith(interruptedMarker, StringComparison.Ordinal) || entry.Text.StartsWith(reminderMarker, StringComparison.Ordinal))
        {
            return null;
        }

        return new ChatMessage { Role = found, Text = entry.Text };
    }

    private void Handle(Guid id, Live live, IPromptSession session, AiService service, PromptEvent output)
    {
        switch (output)
        {
            case PromptMessageEvent { Text: { Length: > 0 } reply }:
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = id, Message = new ChatMessage { Role = MessageRole.Assistant, Text = reply } });
                break;
            case PromptFailureEvent failure:
                lock (live.Sync)
                {
                    live.Pending.Clear();
                    live.Steered.Clear();
                    live.Epoch++;
                }

                events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Error = failure.Exception is PromptToolStartException ? $"{service.Label()} could not be started: {failure.Message}" : failure.Message });
                break;
            case PromptStateEvent changed:
                lock (live.Sync)
                {
                    Sync(id, live, session, changed.State);
                }

                break;
        }
    }

    private async Task Drain(Guid id, Live live, IPromptSession session)
    {
        bool again = true;

        try
        {
            while (again)
            {
                List<(string Text, PromptOptions Options)> steered;
                int epoch;

                lock (live.Sync)
                {
                    steered = [.. live.Steered];
                    live.Steered.Clear();
                    epoch = live.Epoch;
                }

                if (steered.Count > 0)
                {
                    List<(string Text, PromptOptions Options)> missed = await Unanswered(session, steered);

                    lock (live.Sync)
                    {
                        foreach ((string text, PromptOptions options) in live.Epoch == epoch ? missed : [])
                        {
                            live.Pending.Enqueue((text, options with { Notes = [.. options.Notes, steeringNote] }));
                        }
                    }
                }

                await live.Gate.WaitAsync(CancellationToken.None);

                try
                {
                    (string Text, PromptOptions Options) next;

                    lock (live.Sync)
                    {
                        if (!ReferenceEquals(live.Session, session))
                        {
                            live.Pending.Clear();
                            live.Steered.Clear();
                            live.Draining = false;
                            SetWorking(id, live, false);
                            return;
                        }

                        if (!live.Pending.TryDequeue(out next))
                        {
                            if (live.Steered.Count == 0)
                            {
                                live.Draining = false;
                                SetWorking(id, live, false);
                                Expire(id, live);
                                return;
                            }

                            continue;
                        }

                        live.Expecting = true;
                    }

                    await session.Send(next.Text, next.Options, CancellationToken.None);
                }
                finally
                {
                    live.Gate.Release();
                }

                lock (live.Sync)
                {
                    again = (live.Pending.Count > 0 || live.Steered.Count > 0) && session.State == PromptSessionState.Waiting && !live.Expecting && live.Sending == 0;
                    live.Draining = again;
                }
            }
        }
        catch (Exception exception) when (exception is PromptSessionException or ObjectDisposedException or InvalidOperationException or NotSupportedException or IOException)
        {
            lock (live.Sync)
            {
                live.Pending.Clear();
                live.Steered.Clear();
                live.Expecting = false;
                live.Draining = false;
                SetWorking(id, live, false);
            }

            events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Error = exception.Message });
        }
    }

    private async Task<List<(string Text, PromptOptions Options)>> Unanswered(IPromptSession session, List<(string Text, PromptOptions Options)> steered)
    {
        List<string> inputs = [];

        try
        {
            await foreach (PromptHistoryEntry entry in session.GetHistory().Take(60))
            {
                if (entry.Event is PromptInputEvent)
                {
                    inputs.Add(entry.Text);
                }
            }
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException or ObjectDisposedException)
        {
        }

        List<(string Text, PromptOptions Options)> missed = [];

        foreach ((string text, PromptOptions options) in steered)
        {
            int found = inputs.FindIndex(input => input.Contains(text.Trim(), StringComparison.Ordinal));

            if (found >= 0)
            {
                inputs.RemoveAt(found);
            }
            else
            {
                missed.Add((text, options));
            }
        }

        return missed;
    }

    private void Reconcile(Guid id, Live live, IPromptSession session)
    {
        if (!ReferenceEquals(live.Session, session) || live.Expecting || live.Sending > 0 || session.State != PromptSessionState.Waiting)
        {
            return;
        }

        if (live.Pending.Count > 0 || live.Steered.Count > 0)
        {
            if (!live.Draining)
            {
                live.Draining = true;
                _ = Drain(id, live, session);
            }

            return;
        }

        SetWorking(id, live, false);
        Expire(id, live);
    }

    private void Sync(Guid id, Live live, IPromptSession session, PromptSessionState state)
    {
        if (!ReferenceEquals(live.Session, session))
        {
            return;
        }

        if (state != PromptSessionState.Waiting)
        {
            live.Expecting = false;
            SetWorking(id, live, true);
        }
        else
        {
            Reconcile(id, live, session);
        }
    }

    private void SetWorking(Guid id, Live live, bool working)
    {
        if (working)
        {
            activity.TryEnter(id, WorkspaceState.Working);
        }

        if (live.Working != working)
        {
            live.Working = working;

            if (!working)
            {
                if (activity.Get(id) == WorkspaceState.Working)
                {
                    snapshots.Discard(id);
                }

                activity.Leave(id, WorkspaceState.Working);
            }

            events.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = id, Working = working });
        }
    }

    private void StopIdle(Live live)
    {
        live.Idle?.Cancel();
        live.Idle?.Dispose();
        live.Idle = null;
    }

    private void Expire(Guid id, Live live)
    {
        StopIdle(live);
        CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        live.Idle = idle;
        _ = Task.Delay(idleTimeout, idle.Token).ContinueWith(task => task.IsCompletedSuccessfully ? Release(id) : Task.CompletedTask, TaskScheduler.Default);
    }

    private sealed class Watcher(Action<PromptEvent> next) : IObserver<PromptEvent>
    {
        public void OnNext(PromptEvent value)
        {
            try
            {
                next(value);
            }
            catch (Exception)
            {
                // Live AI drops a watcher that throws, which would leave the workspace working for good.
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed record Carry(string Model, Dictionary<string, string> Settings);

    private sealed class Tally
    {
        public int Count;
    }

    private sealed class Live
    {
        public Lock Sync { get; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IPromptSession? Session { get; set; }

        public IDisposable? Watch { get; set; }

        public AiService Service { get; set; }

        public Carry? Carried { get; set; }

        public bool Held { get; set; }

        public Queue<(string Text, PromptOptions Options)> Pending { get; } = new();

        public bool Draining { get; set; }

        public int Epoch { get; set; }

        public int Sending { get; set; }

        public List<(string Text, PromptOptions Options)> Steered { get; } = [];

        public Tally Tally { get; set; } = new();

        public bool Working { get; set; }

        public bool Expecting { get; set; }

        public CancellationTokenSource? Idle { get; set; }
    }
}
