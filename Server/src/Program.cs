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
        ILog log = new ConsoleLog();
        IFolderBrowser folders = new FolderBrowser();
        IWorkspaceStore store = new WorkspaceStore();
        IEventHub events = new EventHub();
        IWorkspaceManager workspaces = new WorkspaceManager(store, folders, events);
        IGitService git = new GitService();
        IWorkspaceActivity activity = new WorkspaceActivity(events);
        IWorkspaceSnapshots snapshots = new WorkspaceSnapshots(git);
        IAiProviders providers = new AiProviders();
        IPromptService prompts = new PromptService(workspaces, store, providers.Create, events, activity, snapshots);
        IWorkspaceVersioning versioning = new WorkspaceVersioning(git, prompts, events, activity);
        IScriptRunner scripts = new ScriptRunner(events, activity);
        IUsageService usage = new UsageService(providers.Create);
        IWorkspaceLoops loops = new WorkspaceLoops(workspaces, prompts, activity, usage, events);
        IRequestHandler requestHandler = new RequestHandler(workspaces, folders, new WorkspaceFileBrowser(), git, versioning, prompts, scripts, usage, activity, new WorkspaceControl(activity, prompts, loops, scripts, versioning, snapshots), new SpeechService(new WhisperEngine()), new ModelService(providers.Create), loops, new WorkspaceWatcher(events, log));
        IApplication application = new Application(info, new ConfigLoader(), requestHandler, new ConnectionServer(new Handshake(), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), requestHandler, events, log), Console.Out);
        try
        {
            return await application.Run(args, cancellation.Token);
        }
        finally
        {
            Console.WriteLine("Closing the AI sessions");
            await prompts.Close();
            Console.WriteLine("Closed the AI sessions");
        }
    }
}
