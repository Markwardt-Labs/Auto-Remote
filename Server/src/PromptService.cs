namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Keeps a live AI conversation per workspace and records what is said in it.
/// </summary>
internal interface IPromptService
{
    /// <summary>
    /// Supplies the settings used to run the AI services.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="stopping">Ends every conversation when cancelled.</param>
    void Configure(ServerConfig config, CancellationToken stopping);

    /// <summary>
    /// Checks whether the AI is currently working in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if the AI is working.</returns>
    bool IsBusy(Guid id);

    /// <summary>
    /// Reads a chunk of a workspace's conversation.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="before">The position before which to read, or <see langword="null" /> for the newest messages.</param>
    /// <param name="limit">The most messages to read, or <see langword="null" /> for the default.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The chunk in order and whether the AI is working.</returns>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task<ChatHistory> History(Guid id, int? before, int? limit, CancellationToken cancellation);

    /// <summary>
    /// Sends a prompt to the AI in a workspace and records it. A prompt sent while the AI is working joins that work. The reply and the state of the work are published as events.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="cancellation">Cancels the send.</param>
    /// <exception cref="RequestException">The workspace is unknown, the prompt is empty, or the AI could not take it.</exception>
    Task Prompt(Guid id, string text, CancellationToken cancellation);

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
    /// Ends a workspace's conversation process if the AI is idle, so the next prompt starts a fresh one with the workspace's current settings; does nothing while the AI works.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the process has ended.</returns>
    Task Release(Guid id);
}

/// <inheritdoc />
internal sealed class PromptService(IWorkspaceManager workspaces, IWorkspaceStore store, IAiRunner runner, IEventHub events) : IPromptService
{
    private readonly ConcurrentDictionary<Guid, Live> lives = new();

    private readonly int defaultChunk = 50;

    private readonly int maxChunk = 200;

    private readonly int maxAttachment = 2 * 1024 * 1024;

    private readonly string[] lostSessionMarkers = ["No conversation found", "no rollout found"];

    private readonly TimeSpan idleTimeout = TimeSpan.FromMinutes(5);

    private ServerConfig config = new() { Port = 0, WebPort = 0, Key = string.Empty };

    private CancellationToken stopping;

    /// <inheritdoc />
    public void Configure(ServerConfig config, CancellationToken stopping)
    {
        this.config = config;
        this.stopping = stopping;
    }

    /// <inheritdoc />
    public bool IsBusy(Guid id) => lives.TryGetValue(id, out Live? live) && live.Working;

    /// <inheritdoc />
    public async Task<ChatHistory> History(Guid id, int? before, int? limit, CancellationToken cancellation)
    {
        await workspaces.Get(id, cancellation);
        IReadOnlyList<ChatMessage> all = await store.ReadHistory(id, cancellation);
        int end = Math.Clamp(before ?? all.Count, 0, all.Count);
        int start = Math.Max(0, end - Math.Clamp(limit ?? defaultChunk, 1, maxChunk));
        return new ChatHistory { Messages = [.. all.Skip(start).Take(end - start)], Start = start, Working = IsBusy(id) };
    }

    /// <inheritdoc />
    public async Task Prompt(Guid id, string text, CancellationToken cancellation)
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
            live.Idle?.Cancel();

            if (live.Conversation is null)
            {
                Begin(id, live, workspace.AiSessionId ?? Guid.NewGuid(), workspace.AiSessionId is not null, workspace);
            }

            await Record(id, new ChatMessage { Role = MessageRole.User, Text = text }, cancellation);
            live.Unanswered.Add(text);
            SetWorking(id, live, true);

            try
            {
                await live.Conversation!.Send(text, cancellation);
            }
            catch (RequestException) when (live.Resumed && !live.Confirmed)
            {
                // A resumed process whose session is gone can exit before taking the prompt; Pump then sees its failure and restarts with the unanswered prompts.
            }
            catch (RequestException)
            {
                live.Unanswered.Clear();
                await End(id, live, live.Conversation!);
                throw;
            }
        }
        finally
        {
            live.Gate.Release();
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
            if (!live.Working && live.Conversation is not null)
            {
                await End(id, live, live.Conversation);
            }
        }
        finally
        {
            live.Gate.Release();
        }
    }

    private void Begin(Guid id, Live live, Guid aiSession, bool resume, WorkspaceInfo workspace)
    {
        IAiConversation conversation = runner.Start(config, workspace.Service, workspace.Folder, workspace.Model, workspace.Effort, aiSession, resume);
        live.Conversation = conversation;
        live.AiSession = aiSession;
        live.Resumed = resume;
        live.Confirmed = false;
        live.Workspace = workspace;
        _ = Pump(id, live, conversation);
    }

    private async Task Pump(Guid id, Live live, IAiConversation conversation)
    {
        try
        {
            await foreach (AiOutput output in conversation.Read(stopping))
            {
                await live.Gate.WaitAsync(CancellationToken.None);

                try
                {
                    if (live.Conversation != conversation)
                    {
                        return;
                    }

                    await Handle(id, live, conversation, output);

                    if (live.Conversation != conversation)
                    {
                        return;
                    }
                }
                finally
                {
                    live.Gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await live.Gate.WaitAsync(CancellationToken.None);

        try
        {
            if (live.Conversation == conversation)
            {
                if (live.Working)
                {
                    events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Error = "The AI stopped." });
                }

                live.Unanswered.Clear();
                await End(id, live, conversation);
            }
        }
        finally
        {
            live.Gate.Release();
        }
    }

    private async Task Handle(Guid id, Live live, IAiConversation conversation, AiOutput output)
    {
        switch (output.Kind)
        {
            case AiOutputKind.Reply:
                await Confirm(id, live);
                SetWorking(id, live, true);
                await Record(id, new ChatMessage { Role = MessageRole.Assistant, Text = output.Text }, CancellationToken.None);
                break;
            case AiOutputKind.Session:
                if (Guid.TryParse(output.Text, out Guid reported))
                {
                    live.AiSession = reported;
                }

                break;
            case AiOutputKind.Finished:
                await Confirm(id, live);
                live.Unanswered.Clear();
                SetWorking(id, live, false);
                Expire(id, live);
                break;
            case AiOutputKind.Failed:
                if (live.Resumed && !live.Confirmed && lostSessionMarkers.Any(marker => output.Text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    await Restart(id, live, conversation);
                    break;
                }

                live.Unanswered.Clear();
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Error = output.Text });
                SetWorking(id, live, false);
                await End(id, live, conversation);
                break;
        }
    }

    private async Task Restart(Guid id, Live live, IAiConversation lost)
    {
        List<string> again = [.. live.Unanswered];
        WorkspaceInfo workspace = live.Workspace!;
        live.Conversation = null;
        await lost.DisposeAsync();

        try
        {
            Begin(id, live, Guid.NewGuid(), false, workspace);

            foreach (string text in again)
            {
                await live.Conversation!.Send(text, stopping);
            }
        }
        catch (RequestException exception)
        {
            live.Unanswered.Clear();
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Error = exception.Message });
            SetWorking(id, live, false);

            if (live.Conversation is { } started)
            {
                await End(id, live, started);
            }
        }
    }

    private async Task Confirm(Guid id, Live live)
    {
        if (!live.Confirmed)
        {
            live.Confirmed = true;

            if (!live.Resumed)
            {
                await workspaces.AttachAiSession(id, live.AiSession, CancellationToken.None);
            }
        }
    }

    private async Task Record(Guid id, ChatMessage message, CancellationToken cancellation)
    {
        int index = await store.AppendHistory(id, message, cancellation);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = id, Index = index, Message = message });
    }

    private void SetWorking(Guid id, Live live, bool working)
    {
        if (live.Working != working)
        {
            live.Working = working;
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = id, Working = working });
        }
    }

    private async Task End(Guid id, Live live, IAiConversation conversation)
    {
        if (live.Conversation == conversation)
        {
            live.Conversation = null;
        }

        SetWorking(id, live, false);
        await conversation.DisposeAsync();
    }

    private void Expire(Guid id, Live live)
    {
        live.Idle?.Cancel();
        CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        live.Idle = idle;
        _ = Task.Delay(idleTimeout, idle.Token).ContinueWith(task => task.IsCompletedSuccessfully ? Release(id) : Task.CompletedTask, TaskScheduler.Default);
    }

    private sealed class Live
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IAiConversation? Conversation { get; set; }

        public WorkspaceInfo? Workspace { get; set; }

        public Guid AiSession { get; set; }

        public bool Resumed { get; set; }

        public bool Confirmed { get; set; }

        public bool Working { get; set; }

        public List<string> Unanswered { get; } = [];

        public CancellationTokenSource? Idle { get; set; }
    }
}
