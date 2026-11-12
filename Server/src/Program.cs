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
        IPromptService prompts = new PromptService(workspaces, store, host, events, activity, snapshots, usage);
        IWorkspaceVersioning versioning = new WorkspaceVersioning(git, prompts, events, activity);
        IScriptRunner scripts = new ScriptRunner(events, activity);
        IWorkspaceLoops loops = new WorkspaceLoops(workspaces, prompts, activity, usage, events);
        IRequestHandler requestHandler = new RequestHandler(workspaces, folders, new WorkspaceFileBrowser(), git, versioning, new SavedInputs(store, workspaces, events), prompts, scripts, usage, activity, new WorkspaceControl(activity, prompts, loops, scripts, versioning, snapshots), new SpeechService(new WhisperEngine()), host, loops, new WorkspaceWatcher(events, log), updater, log);
        IApplication application = new Application(info, new ConfigLoader(), requestHandler, new ConnectionServer(new Handshake(info), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), requestHandler, events, log), Console.Out);
        int code;

        try
        {
            code = await application.Run(args, cancellation.Token);
        }
        finally
        {
            Console.WriteLine("Closing the AI sessions");
            await prompts.Close();
            Console.WriteLine("Closed the AI sessions");
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
