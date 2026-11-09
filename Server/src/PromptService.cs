namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps a live AI session per workspace, gives it the prompts and publishes what is said in it. The conversation itself is stored by the AI service and read back through Live AI.
/// </summary>
internal interface IPromptService
{
    /// <summary>
    /// Supplies what ends every session when the server stops.
    /// </summary>
    /// <param name="stopping">Ends every session when cancelled.</param>
    void Configure(CancellationToken stopping);

    /// <summary>
    /// Checks whether the AI is currently working in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if the AI is working.</returns>
    bool IsBusy(Guid id);

    /// <summary>
    /// Reads a chunk of a workspace's chat, which Auto Remote keeps itself: what was sent to the AI and what it wrote, nothing the AI service keeps for its own use. A workspace that has no chat yet takes over its conversation from the AI service's stored session once.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="before">The cursor of <see cref="ChatHistory.Older" /> to read before, or <see langword="null" /> for the newest messages.</param>
    /// <param name="limit">The most messages to read, or <see langword="null" /> for the default.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The chunk in order and whether the AI is working.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the cursor is not valid.</exception>
    Task<ChatHistory> History(Guid id, string? before, int? limit, CancellationToken cancellation);

    /// <summary>
    /// Reads how the AI of a workspace is run, as its session stores it, making the session when the workspace has none yet.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="RequestException">No such workspace exists, or the AI could not be set up.</exception>
    Task<WorkspaceSettings> GetSettings(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Changes how the AI of a workspace is run, from its next prompt on; the session stores the change. The provider cannot be changed.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="settings">The new settings; the provider and the descriptors in them are ignored.</param>
    /// <param name="cancellation">Cancels the change.</param>
    /// <returns>The settings as they now are.</returns>
    /// <exception cref="RequestException">No such workspace exists, or a setting is not allowed.</exception>
    Task<WorkspaceSettings> SetSettings(Guid id, WorkspaceSettings settings, CancellationToken cancellation);

    /// <summary>
    /// Gives every open AI session the built-in instructions for its current status interval, which a change of the environment's interval makes stale; sessions that are not open get them when they are opened.
    /// </summary>
    /// <param name="cancellation">Cancels the update.</param>
    /// <returns>A task that completes once the open sessions have been updated.</returns>
    Task RefreshInstructions(CancellationToken cancellation);

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
    /// Asks the AI of a workspace a question in a fork of its conversation, which is thrown away afterwards, so neither the conversation nor the state of the AI changes and it may be asked while the AI works.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The question.</param>
    /// <param name="cancellation">Cancels the question.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="RequestException">The workspace is unknown, the question is empty, or the AI could not answer.</exception>
    Task<string> Ask(Guid id, string text, CancellationToken cancellation);

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
internal sealed class PromptService(IWorkspaceManager workspaces, IWorkspaceStore store, IPrompterHost host, IEventHub events, IWorkspaceActivity activity, IWorkspaceSnapshots snapshots, IUsageService? usage = null) : IPromptService
{
    private readonly ConcurrentDictionary<Guid, Live> lives = new();

    private readonly int defaultChunk = 50;

    private readonly int maxChunk = 200;

    private readonly int historyScan = 20000;

    private readonly int maxAttachment = 20 * 1024 * 1024;

    private readonly TimeSpan idleTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan stopWait = TimeSpan.FromSeconds(15);

    private readonly TimeSpan shutdownWait = TimeSpan.FromSeconds(10);

    private readonly TimeSpan windowInterval = TimeSpan.FromMinutes(1);

    private readonly string[] images = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private readonly string interruptedMarker = "[Request interrupted by user";

    private readonly string reminderMarker = "<system-reminder>";

    private readonly string compactionMarker = "This session is being continued from a previous conversation";

    private readonly string attachedPrefix = "Attached file: ";

    private readonly string steeringNote = "You were sent this message while you were working and it may have been missed. If you already acted on it, say so briefly instead of doing it again; otherwise do it now.";

    private CancellationToken stopping;

    /// <inheritdoc />
    public void Configure(CancellationToken stopping) => this.stopping = stopping;

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

        while (tallies.Any(tally => tally.Count > 0) && DateTime.UtcNow < deadline)
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
        int? cursor = Decode(before);
        Live live = lives.GetOrAdd(id, _ => new Live());
        await live.Gate.WaitAsync(cancellation);

        try
        {
            Task writing;

            lock (live.Sync)
            {
                writing = live.Writing;
            }

            await writing.ContinueWith(static _ => { }, TaskScheduler.Default);
            IReadOnlyList<ChatMessage>? stored = await store.LoadMessages(id, cancellation);

            if (stored is null)
            {
                if (live.Session is null && workspace.SessionId is null)
                {
                    return new ChatHistory { Messages = [], Working = live.Working };
                }

                IPromptSession session = await Session(id, workspace, live, cancellation);
                List<ChatMessage> imported = await Import(session, cancellation);
                stored = [.. imported, .. await store.LoadMessages(id, cancellation) ?? []];
                await store.SaveMessages(id, stored, cancellation);

                if (!live.Working)
                {
                    lock (live.Sync)
                    {
                        Expire(id, live);
                    }
                }
            }

            int end = cursor is { } point ? Math.Min(point, stored.Count) : stored.Count;
            int start = Math.Max(0, end - Math.Clamp(limit ?? defaultChunk, 1, maxChunk));
            return new ChatHistory { Messages = [.. stored.Skip(start).Take(end - start)], Older = start > 0 ? start.ToString(CultureInfo.InvariantCulture) : null, Working = live.Working };
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
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkspacesChanged, WorkspaceId = Guid.Empty });
            PromptSendOptions options = await Options(text, cancellation);

            if (onlyWhenIdle && (live.Working || session.State != PromptSessionState.Waiting))
            {
                throw new RequestException("The AI is working.");
            }

            Say(id, live, new ChatMessage { Role = MessageRole.User, Text = text, Time = DateTimeOffset.UtcNow });

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
            catch (Exception exception) when (exception is PromptSessionException or ObjectDisposedException or InvalidOperationException or NotSupportedException or ArgumentException or IOException)
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
    public async Task<string> Ask(Guid id, string text, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RequestException("The question is empty.");
        }

        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
        Live live = lives.GetOrAdd(id, _ => new Live());
        await live.Gate.WaitAsync(cancellation);
        IPromptSession session;

        try
        {
            session = await Session(id, workspace, live, cancellation);

            lock (live.Sync)
            {
                StopIdle(live);
            }
        }
        finally
        {
            live.Gate.Release();
        }

        try
        {
            return await session.Request(text, new PromptRequestOptions { Mode = PromptRequestMode.Fork }, cancellation);
        }
        catch (Exception exception) when (exception is PromptSessionException or ObjectDisposedException or InvalidOperationException or NotSupportedException or IOException)
        {
            throw new RequestException(exception.Message);
        }
        finally
        {
            lock (live.Sync)
            {
                if (!live.Working)
                {
                    Expire(id, live);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceSettings> GetSettings(Guid id, CancellationToken cancellation)
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

            return Describe(workspace, session);
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceSettings> SetSettings(Guid id, WorkspaceSettings settings, CancellationToken cancellation)
    {
        if (!Enum.IsDefined(settings.Capability) || !Enum.IsDefined(settings.Effort))
        {
            throw new RequestException("The capability or the effort is not one the AI knows.");
        }

        if (settings.StatusMinutes is < 1 or > 1440)
        {
            throw new RequestException("The status interval must be from 1 to 1440 minutes.");
        }

        WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
        Live live = lives.GetOrAdd(id, _ => new Live());
        await live.Gate.WaitAsync(cancellation);

        try
        {
            IPromptSession session = await Session(id, workspace, live, cancellation);

            IPromptSessionConfiguration configuration = session.Configuration;
            WorkspaceSettings before = Describe(workspace, session);

            try
            {
                Apply(configuration, settings);
            }
            catch (Exception exception) when (exception is PromptSessionException or ArgumentException)
            {
                try
                {
                    Apply(configuration, before);
                }
                catch (Exception restoring) when (restoring is PromptSessionException or ArgumentException)
                {
                }

                throw new RequestException(exception.Message);
            }

            WorkspaceSettings described = Describe(await workspaces.SetOverrides(id, settings.ReviewPrompt, settings.ExpandPrompt, settings.StatusMinutes, cancellation), session);

            if (!live.Working)
            {
                lock (live.Sync)
                {
                    Expire(id, live);
                }
            }

            return described;
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RefreshInstructions(CancellationToken cancellation)
    {
        foreach ((Guid id, Live live) in lives.ToList())
        {
            await live.Gate.WaitAsync(cancellation);

            try
            {
                if (live.Session is { } session)
                {
                    WorkspaceInfo workspace = await workspaces.Get(id, cancellation);
                    string wanted = host.ComposeInstructions(host.UserInstructions(session.Configuration.Instructions), workspace.StatusMinutes);

                    if (session.Configuration.Instructions != wanted)
                    {
                        session.Configuration.Instructions = wanted;
                    }
                }
            }
            catch (Exception exception) when (exception is RequestException or PromptSessionException or ObjectDisposedException or ArgumentException)
            {
            }
            finally
            {
                live.Gate.Release();
            }
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
            if (live.Tally.Count > 0)
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

        if (conversation && workspace.SessionId is { } sessionId)
        {
            try
            {
                await host.Prompter.Delete(sessionId, CancellationToken.None);
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

        for (int attempt = 0; attempt < 3 && session.State == PromptSessionState.Waiting && live.Tally.Count > 0; attempt++)
        {
            await Task.Delay(100);
        }

        if (session.State != PromptSessionState.Waiting || live.Tally.Count > 0)
        {
            Detach(live)?.Dispose();
        }
    }

    private IPromptSession? Detach(Live live)
    {
        lock (live.Sync)
        {
            IPromptSession? session = live.Session;
            live.Session = null;
            live.Watch?.Dispose();
            live.Watch = null;
            return session;
        }
    }

    private void Apply(IPromptSessionConfiguration configuration, WorkspaceSettings settings)
    {
        configuration.Capability = Enum.Parse<PromptCapability>(settings.Capability.ToString());
        configuration.Effort = Enum.Parse<PromptEffort>(settings.Effort.ToString());
        configuration.IsShellAllowed = settings.IsShellAllowed;
        configuration.IsSystemAccessAllowed = settings.IsSystemAccessAllowed;
        configuration.IsWebAllowed = settings.IsWebAllowed;
        configuration.IsImageGenerationAllowed = settings.IsImageGenerationAllowed;
        configuration.Instructions = host.ComposeInstructions(settings.Instructions, settings.StatusMinutes);
    }

    private WorkspaceSettings Describe(WorkspaceInfo workspace, IPromptSession session)
    {
        AiEnvironment environment = host.Get();
        return new()
        {
            Service = workspace.Service,
            Capability = Enum.Parse<AiCapability>(session.Configuration.Capability.ToString()),
            Effort = Enum.Parse<AiEffort>(session.Configuration.Effort.ToString()),
            IsShellAllowed = session.Configuration.IsShellAllowed,
            IsSystemAccessAllowed = session.Configuration.IsSystemAccessAllowed,
            IsWebAllowed = session.Configuration.IsWebAllowed,
            IsImageGenerationAllowed = session.Configuration.IsImageGenerationAllowed,
            Instructions = host.UserInstructions(session.Configuration.Instructions),
            StatusMinutes = workspace.StatusMinutes,
            GlobalStatusMinutes = environment.StatusMinutes,
            ReviewPrompt = workspace.ReviewPrompt,
            ExpandPrompt = workspace.ExpandPrompt,
            GlobalReviewPrompt = environment.ReviewPrompt,
            GlobalExpandPrompt = environment.ExpandPrompt,
            Descriptors = session.Descriptors.ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }

    private async Task<PromptSendOptions> Options(string text, CancellationToken cancellation)
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

        return new PromptSendOptions { Attachments = attachments };
    }

    private async Task<IPromptSession> Session(Guid id, WorkspaceInfo workspace, Live live, CancellationToken cancellation)
        => live.Session ??= await Open(id, workspace, live, cancellation);

    private async Task<IPromptSession> Open(Guid id, WorkspaceInfo workspace, Live live, CancellationToken cancellation)
    {
        IPromptSession session;

        try
        {
            session = await Obtain(workspace, cancellation);
            string wanted = host.ComposeInstructions(host.UserInstructions(session.Configuration.Instructions), workspace.StatusMinutes);

            if (session.Configuration.Instructions != wanted)
            {
                session.Configuration.Instructions = wanted;
            }

        }
        catch (Exception exception) when (exception is PromptSessionException or DirectoryNotFoundException or ArgumentException)
        {
            throw new RequestException(exception.Message);
        }

        lock (live.Sync)
        {
            live.Tally = new Tally();
            live.Watch = session.Subscribe(new Watcher(output => Handle(id, live, session, workspace.Service, output)));
        }

        return session;
    }

    private async Task<IPromptSession> Obtain(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        if (workspace.SessionId is { } stored)
        {
            IPromptSession? loaded = null;

            try
            {
                loaded = await host.Prompter.Load(stored, cancellation);

                if (!SameFolder(loaded.WorkspacePath, workspace.Folder))
                {
                    await loaded.Move(workspace.Folder, cancellation);
                }

                return loaded;
            }
            catch (Exception exception) when (exception is PromptSessionException and not PromptToolStartException or DirectoryNotFoundException or ArgumentException)
            {
                if (loaded is not null)
                {
                    await loaded.DisposeAsync();
                }
            }
        }

        return await Replace(workspace, cancellation);
    }

    private bool SameFolder(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.Ordinal);

    private async Task<IPromptSession> Replace(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        string sessionId = await host.CreateSession(workspace.Folder, workspace.Service, null, cancellation);
        await workspaces.AttachSession(workspace.Id, sessionId, CancellationToken.None);
        return await host.Prompter.Load(sessionId, cancellation);
    }

    private int? Decode(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        return int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int position) ? position : throw new RequestException("The history cursor is not valid.");
    }

    private void Say(Guid id, Live live, ChatMessage message)
    {
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = id, Message = message });

        lock (live.Sync)
        {
            live.Writing = Write(live.Writing, id, message);
        }
    }

    private async Task Write(Task previous, Guid id, ChatMessage message)
    {
        await previous.ContinueWith(static _ => { }, TaskScheduler.Default);

        try
        {
            await store.AppendMessage(id, message, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<List<ChatMessage>> Import(IPromptSession session, CancellationToken cancellation)
    {
        List<ChatMessage> newestFirst = [];
        bool afterUser = false;

        try
        {
            await foreach (PromptHistoryEntry entry in session.GetHistory(cancellation).WithCancellation(cancellation))
            {
                if (entry.Event is PromptInputEvent && entry.Text.StartsWith(reminderMarker, StringComparison.Ordinal))
                {
                    if (afterUser)
                    {
                        newestFirst.RemoveAt(newestFirst.Count - 1);
                    }

                    afterUser = false;
                    continue;
                }

                ChatMessage? message = Message(entry);

                if (message is not null)
                {
                    newestFirst.Add(message);
                }

                afterUser = message?.Role == MessageRole.User;
            }
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException)
        {
            throw new RequestException($"The conversation could not be read: {exception.Message}");
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    private ChatMessage? Message(PromptHistoryEntry entry)
    {
        MessageRole? role = entry.Event switch
        {
            PromptInputEvent => MessageRole.User,
            PromptMessageEvent => MessageRole.Assistant,
            _ => null
        };

        if (role is not { } found || string.IsNullOrWhiteSpace(entry.Text) || entry.Text.StartsWith(interruptedMarker, StringComparison.Ordinal) || entry.Text.StartsWith(reminderMarker, StringComparison.Ordinal) || entry.Text.StartsWith(compactionMarker, StringComparison.Ordinal))
        {
            return null;
        }

        return new ChatMessage { Role = found, Text = entry.Text, Time = entry.Time };
    }

    private void Handle(Guid id, Live live, IPromptSession session, AiService service, PromptEvent output)
    {
        switch (output)
        {
            case PromptMessageEvent { Text: { Length: > 0 } reply }:
                Say(id, live, new ChatMessage { Role = MessageRole.Assistant, Text = reply, Time = DateTimeOffset.UtcNow });
                break;
            case PromptToolCallEvent { Name: "shell" } call:
                live.Tally.Running[call.Id] = 0;
                break;
            case PromptToolResultEvent result:
                live.Tally.Running.TryRemove(result.Id, out _);
                break;
            case PromptWindowsEvent windows:
                usage?.Note(service, windows.Windows);
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
                List<(string Text, PromptSendOptions Options)> steered;
                int epoch;

                lock (live.Sync)
                {
                    steered = [.. live.Steered];
                    live.Steered.Clear();
                    epoch = live.Epoch;
                }

                if (steered.Count > 0)
                {
                    List<(string Text, PromptSendOptions Options)> missed = await Unanswered(session, steered);

                    lock (live.Sync)
                    {
                        foreach ((string text, PromptSendOptions options) in live.Epoch == epoch ? missed : [])
                        {
                            live.Pending.Enqueue((text, options with { Notes = [.. options.Notes, steeringNote] }));
                        }
                    }
                }

                await live.Gate.WaitAsync(CancellationToken.None);

                try
                {
                    (string Text, PromptSendOptions Options) next;

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

    private async Task<List<(string Text, PromptSendOptions Options)>> Unanswered(IPromptSession session, List<(string Text, PromptSendOptions Options)> steered)
    {
        List<(string Text, PromptSendOptions Options)> open = [.. steered];
        List<(string Text, PromptSendOptions Options)> missed = [];
        bool acted = false;
        int read = 0;

        try
        {
            await foreach (PromptHistoryEntry entry in session.GetHistory())
            {
                if (open.Count == 0)
                {
                    break;
                }

                if (++read > historyScan)
                {
                    open.Clear();
                    break;
                }

                if (entry.Event is PromptInputEvent input)
                {
                    if (input.Text.StartsWith(reminderMarker, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int found = open.FindIndex(item => input.Text.Contains(item.Text.Trim(), StringComparison.Ordinal));

                    if (found < 0)
                    {
                        break;
                    }

                    if (!acted)
                    {
                        missed.Add(open[found]);
                    }

                    open.RemoveAt(found);
                }
                else if (entry.Event is PromptMessageEvent or PromptToolCallEvent)
                {
                    acted = true;
                }
            }
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException or ObjectDisposedException)
        {
            open.Clear();
        }

        missed.AddRange(open);
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

    private sealed class Tally
    {
        public ConcurrentDictionary<string, byte> Running { get; } = new();

        public int Count => Running.Count;
    }

    private sealed class Live
    {
        public Lock Sync { get; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IPromptSession? Session { get; set; }

        public IDisposable? Watch { get; set; }

        public bool Held { get; set; }

        public Task Writing { get; set; } = Task.CompletedTask;

        public Queue<(string Text, PromptSendOptions Options)> Pending { get; } = new();

        public bool Draining { get; set; }

        public int Epoch { get; set; }

        public int Sending { get; set; }

        public List<(string Text, PromptSendOptions Options)> Steered { get; } = [];

        public Tally Tally { get; set; } = new();

        public bool Working { get; set; }

        public bool Expecting { get; set; }

        public CancellationTokenSource? Idle { get; set; }
    }
}
