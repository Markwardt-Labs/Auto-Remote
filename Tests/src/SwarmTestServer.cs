namespace Markwardt.AutoRemote.Tests;

/// <summary>
/// A whole server with a fake AI, listening on a free port over TLS, with a client connected as its user, so two of them can pair and delegate to each other.
/// </summary>
internal sealed class SwarmTestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();

    private Task running = Task.CompletedTask;

    private PromptService prompts = null!;

    private PrompterHost host = null!;

    public required string Data { get; init; }

    public FakePrompter Prompter { get; private set; } = null!;

    public IServerConnection Connection { get; private set; } = null!;

    public WorkQueue Queue { get; private set; } = null!;

    public SwarmCoordinator Coordinator { get; private set; } = null!;

    public int Port { get; private set; }

    /// <summary>
    /// Gets the prompts the AI got, with the folder of the session that got each.
    /// </summary>
    public List<(string Folder, string Text)> Asked { get; } = [];

    /// <summary>
    /// Gets or sets what the AI does with a prompt: it gets the session and the prompt and answers with its events.
    /// </summary>
    public Func<FakePromptSession, FakePrompt, IEnumerable<PromptEvent>> Ai { get; set; } = (_, _) => [new PromptMessageEvent { Text = "ok" }];

    /// <summary>
    /// Gets or sets a value indicating whether the AI of new sessions never finishes by itself.
    /// </summary>
    public bool Hangs { get; set; }

    public static async Task<SwarmTestServer> Start(string root, string name)
    {
        SwarmTestServer server = new() { Data = Path.Combine(root, name) };
        await server.Open(name);
        return server;
    }

    public async Task<SwarmStatus> Status() => await Connection.GetSwarm(CancellationToken.None);

    public async Task<SwarmPeer> Peer() => Assert.Single((await Status()).Peers);

    public async Task<SwarmTaskInfo> Found(Func<SwarmTaskInfo, bool> condition)
    {
        SwarmTaskInfo? found = null;
        await Until(async () => (found = (await Status()).Tasks.FirstOrDefault(condition)) is not null, async () => string.Join("; ", (await Status()).Tasks.Select(task => $"{task.Title} {task.State} {task.Error}")));
        return found!;
    }

    public static async Task Until(Func<Task<bool>> condition, Func<Task<string>>? describe = null, [CallerLineNumber] int line = 0)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await System.Threading.Tasks.Task.Delay(25);
        }

        Assert.Fail($"The condition of line {line} did not come true. {(describe is null ? string.Empty : await describe())}");
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await stopping.CancelAsync();
        await running;
        await prompts.Close();
        await host.DisposeAsync();
        await System.Threading.Tasks.Task.Delay(100);
        stopping.Dispose();
    }

    private async Task Open(string name)
    {
        ServerConfig config = new() { Port = 0, Key = "secret", DataFolder = Data };
        Directory.CreateDirectory(Data);
        Prompter = new FakePrompter
        {
            OnCreate = session =>
            {
                if (Hangs)
                {
                    session.Hang();
                }

                session.ReplyWith(prompt =>
                {
                    // A session that was closed and loaded again is a new object, which is the one the prompt runs on.
                    FakePromptSession current = Prompter.Sessions.Last(other => other.Id == session.Id);

                    lock (Asked)
                    {
                        Asked.Add((current.WorkspacePath ?? string.Empty, prompt.Text));
                    }

                    return Ai(current, prompt);
                });
            }
        };
        IEventHub events = new EventHub();
        Mock<IOllamaService> ollama = new();
        host = new PrompterHost(_ => Prompter, null, null, ollama.Object);
        FolderBrowser folders = new();
        WorkspaceStore store = new();
        GitService git = new();
        WorkspaceActivity activity = new(events);
        WorkspaceSnapshots snapshots = new(git);
        WorkspaceManager workspaces = new(store, folders, events, host);
        UsageService usage = new(host);
        ISessionTools? delegation = null;
        prompts = new PromptService(workspaces, store, host, events, activity, snapshots, null, () => delegation);
        SpeechService speech = new(Mock.Of<IWhisperEngine>());
        SavedInputs savedInputs = new(store, workspaces, events, speech);
        Queue = new WorkQueue(workspaces, prompts, host, activity, savedInputs, events);
        WorkspaceVersioning versioning = new(git, prompts, events, activity, Queue);
        ScriptRunner scripts = new(events);
        WindowGuard guard = new(workspaces, prompts, usage, host, events);
        WorkspaceLoops loops = new(workspaces, prompts, activity, usage, events, TimeSpan.FromMilliseconds(20), null, null, guard, Queue);
        SwarmBook book = new(events, new ISwarmIdentity.Factory());
        SwarmCourier courier = new(book, new SwarmSealer());
        SwarmRepository repository = new(git) { AllowLocalRemotes = true };
        SwarmWorker worker = new(book, courier, repository, workspaces, prompts, Queue, store, host, events, (_, _) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<string>>([]));
        Coordinator = new SwarmCoordinator(book, courier, repository, workspaces, Queue, activity) { ReportRetry = TimeSpan.FromMilliseconds(50) };
        delegation = new DelegationTools(book, Coordinator, worker, workspaces);
        SwarmExchange exchange = new(book, courier, worker, Coordinator);
        SwarmDialer dialer = new(new IMessageChannel.Factory());
        SwarmLink link = new(book, courier, exchange, dialer, Mock.Of<ILog>()) { BusyInterval = TimeSpan.FromMilliseconds(40), QuietInterval = TimeSpan.FromMilliseconds(80) };
        SwarmService swarm = new(book, courier, worker, Coordinator, new SwarmDrive(book, exchange), link, dialer, workspaces, activity, store, Mock.Of<ILog>());
        RequestHandler handler = new(workspaces, folders, new WorkspaceFileBrowser(), git, versioning, savedInputs, new ErrorHistory(store, workspaces, events), guard, prompts, new DiagramService(), scripts, usage, activity, new WorkspaceControl(activity, prompts, loops, versioning, snapshots), speech, host, loops, new WorkspaceWatcher(events, Mock.Of<ILog>()), Mock.Of<IServerUpdater>(), Mock.Of<ILog>(), Queue, swarm);
        await handler.Open(config, stopping.Token);
        ConnectionServer server = new(new Handshake(Mock.Of<IAppInfo>(info => info.Version == "1.2.3")), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), handler, events, Mock.Of<ILog>(), null, link);
        TaskCompletionSource<int> listening = new();
        server.Listening += listening.SetResult;
        running = server.Run(config, stopping.Token);
        Port = await listening.Task;
        Connection = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(new ConnectionSettings { Host = "127.0.0.1", Port = Port, AccessKey = "secret" }, CancellationToken.None);
        AiEnvironment environment = await Connection.GetAiEnvironment(CancellationToken.None);
        await Connection.SetAiEnvironment(environment with { LocalModels = new CapabilityNames { Fast = "small", Balanced = "medium", Strong = "large", Max = "huge" } }, CancellationToken.None);
        await Connection.SetSwarm((await Status()).Server with { Name = name }, CancellationToken.None);
    }
}
