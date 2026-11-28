namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Process entry point for the command line server.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the server with the given command line arguments.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using PosixSignalRegistration terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });

        IAppInfo info = new IAppInfo.Factory().Create(typeof(Program).Assembly);
        IReleaseFeed feed = new IReleaseFeed.Factory().Create();
        IExecutableInstaller installer = new IExecutableInstaller.Factory().Create(feed);
        installer.Clean();
        IServerRestart restart = new ServerRestart(cancellation, Console.Out, installer, [.. args], Environment.GetEnvironmentVariable, Launch, TimeSpan.FromSeconds(1));
        IServerUpdater updater = new ServerUpdater(info, feed, installer, restart);
        ILog log = new ConsoleLog();
        IFolderBrowser folders = new FolderBrowser();
        IWorkspaceStore store = new WorkspaceStore();
        IEventHub events = new EventHub();
        IPrompterHost host = new PrompterHost();
        IWorkspaceManager workspaces = new WorkspaceManager(store, folders, events, host);
        IGitService git = new GitService();
        IWorkspaceActivity activity = new WorkspaceActivity(events);
        IWorkspaceSnapshots snapshots = new WorkspaceSnapshots(git);
        IUsageService usage = new UsageService(host);
        ISessionTools? tools = null;
        ISideIntegrations? integrations = null;
        ISwarmBringIns? bringIns = null;
        IFollowUps? followUps = null;
        IShellCommandLog shellCommands = new ShellCommandLog();
        IPromptService prompts = new PromptService(workspaces, store, host, events, activity, snapshots, usage, () => tools, shellCommands, () => followUps);
        ISpeechService speech = new SpeechService(new WhisperEngine());
        ISavedInputs savedInputs = new SavedInputs(store, workspaces, events, speech);
        IWorkQueue queue = new WorkQueue(workspaces, prompts, host, activity, savedInputs, events, () => followUps);
        IWorkspaceVersioning versioning = new WorkspaceVersioning(git, prompts, events, activity, queue);
        IScriptRunner scripts = new ScriptRunner(events);
        IWindowGuard guard = new WindowGuard(workspaces, prompts, usage, host, events, null, id => followUps?.IsStopped(id) == true);
        IWorkspaceLoops loops = new WorkspaceLoops(workspaces, prompts, activity, usage, events, null, null, null, guard, queue, id => integrations?.IsDelivering(id) == true || bringIns?.IsDelivering(id) == true, () => followUps);
        ISwarmBook swarmBook = new SwarmBook(events, new ISwarmIdentity.Factory());
        ISwarmCourier courier = new SwarmCourier(swarmBook, new SwarmSealer());
        ISwarmRepository repository = new SwarmRepository(git);
        ISwarmWorker worker = new SwarmWorker(swarmBook, courier, repository, workspaces, prompts, queue, store, host, events);
        ISwarmCoordinator coordinator = new SwarmCoordinator(swarmBook, courier, repository, workspaces, log);
        tools = new CombinedSessionTools([new DelegationTools(swarmBook, coordinator, worker, workspaces), new SideSessionTools(() => integrations!, worker.IsDelegated), new OutcomeTools(() => followUps!, worker.Unfinished)]);
        IResourceMonitor resources = new ResourceMonitor(new ProcessSource(), shellCommands, workspaces, activity, scripts);
        ISwarmExchange exchange = new SwarmExchange(swarmBook, courier, worker, coordinator);
        ISwarmDialer dialer = new SwarmDialer(new IMessageChannel.Factory());
        ISwarmLink link = new SwarmLink(swarmBook, courier, exchange, dialer, log);
        IWorkspaceWatcher watcher = new WorkspaceWatcher(events, log);
        ISideSessionRepository sideRepository = new SideSessionRepository(git);
        ISideSessions sideSessions = new SideSessions(workspaces, sideRepository, store, prompts, queue, loops, scripts, guard, snapshots, watcher, events, log, () => followUps);
        IActivityLog journal = new ActivityLog(events, log);
        bringIns = new SwarmBringIns(swarmBook, coordinator, repository, workspaces, prompts, queue, activity, guard, snapshots, events, log, sideSessions, () => followUps, journal, id => integrations!.Reopen(id));
        ISwarmService swarm = new SwarmService(swarmBook, courier, worker, coordinator, bringIns, new SwarmDrive(swarmBook, exchange), link, dialer, workspaces, store, log);
        integrations = new SideIntegrations(workspaces, sideSessions, sideRepository, prompts, queue, activity, loops, scripts, guard, snapshots, events, log, swarm, null, null, () => followUps, journal, bringIns.Finish);
        IAutomationStore automation = new AutomationStore(log);
        followUps = new FollowUps(workspaces, savedInputs, prompts, queue, activity, versioning, git, loops, guard, events, journal, automation, new FailureKinds(), swarm.IsAwaited, id => integrations.IsDelivering(id) || bringIns.IsDelivering(id) || swarm.IsDelegated(id), null, integrations.Reopen, swarm.IsDelegated);
        IAgendaService agenda = new AgendaService(workspaces, followUps, savedInputs, loops, queue, guard, activity, events, id => [.. integrations.Pending(id), .. bringIns.Pending(id)]);
        IRequestHandler requestHandler = new RequestHandler(workspaces, folders, new WorkspaceFileBrowser(), git, versioning, savedInputs, new ErrorHistory(store, workspaces, events), guard, prompts, new DiagramService(), scripts, usage, activity, new WorkspaceControl(activity, prompts, versioning, snapshots), speech, host, loops, watcher, updater, log, queue, swarm, sideSessions, integrations, sideRepository, resources, followUps, journal, agenda);
        IApplication application = new Application(info, new ConfigLoader(), requestHandler, new ConnectionServer(new Handshake(info), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), requestHandler, events, log, new LocalListener(info, new LocalEndpoint(), new ILocalAccess.Factory(), log), link), Console.Out);
        int code;

        try
        {
            code = await application.Run(args, cancellation.Token);
        }
        finally
        {
            Console.WriteLine("Closing the AI sessions");
            await prompts.Close();
            await automation.Flush();
            await journal.Flush();
            Console.WriteLine("Closed the AI sessions");
            await host.DisposeAsync();
        }

        if (restart.IsRequested)
        {
            Environment.Exit(restart.Relaunch());
        }

        return code;
    }

    private static string? Launch(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info)?.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            return exception.Message;
        }
    }
}
