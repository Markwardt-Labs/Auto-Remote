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

        IAppInfo info = new IAppInfo.Factory().Create(typeof(Program).Assembly);
        IFolderBrowser folders = new FolderBrowser();
        IWorkspaceStore store = new WorkspaceStore();
        IWorkspaceManager workspaces = new WorkspaceManager(store, folders);
        IEventHub events = new EventHub();
        IGitService git = new GitService();
        IPromptService prompts = new PromptService(workspaces, store, new AiRunner(), events);
        IRequestHandler requestHandler = new RequestHandler(workspaces, folders, new WorkspaceFileBrowser(), git, new WorkspaceVersioning(git, prompts, events), prompts, new UsageService(), events);
        IApplication application = new Application(info, new ConfigLoader(), requestHandler, new ConnectionServer(new Handshake(), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), requestHandler, events, new ConsoleLog()), Console.Out);
        return await application.Run(args, cancellation.Token);
    }
}
