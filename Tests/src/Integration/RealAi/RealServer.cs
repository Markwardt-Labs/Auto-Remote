namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

internal sealed class RealServer : IAsyncDisposable
{
    private readonly List<RemoteEvent> events = [];

    private readonly List<string> output = [];

    private readonly List<WorkspaceInfo> created = [];

    private readonly Dictionary<string, object?> settings;

    private Process? process;

    private IServerConnection? connection;

    private RealServer(string root, Dictionary<string, object?> settings)
    {
        Root = root;
        this.settings = settings;
    }

    public string Root { get; }

    public ConnectionSettings Settings => new() { Host = "127.0.0.1", Port = Convert.ToInt32(settings["port"], CultureInfo.InvariantCulture), AccessKey = "secret" };

    public string Data => Path.Combine(Root, "data");

    public IServerConnection Connection => connection ?? throw new InvalidOperationException("The server is not connected.");

    public IReadOnlyList<string> Output
    {
        get
        {
            lock (output)
            {
                return [.. output];
            }
        }
    }

    public static async Task<RealServer> Start(string? root = null, Dictionary<string, object?>? configure = null, Func<string, Task>? prepare = null)
    {
        string folder = root ?? Path.Combine(Path.GetTempPath(), "realai-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        Dictionary<string, object?> settings = new() { ["key"] = "secret", ["dataFolder"] = Path.Combine(folder, "data"), ["defaultFolder"] = folder, ["speechModel"] = string.Empty };

        foreach ((string name, object? value) in configure ?? [])
        {
            settings[name] = value;
        }

        RealServer server = new(folder, settings);

        if (prepare is not null)
        {
            await prepare(folder);
        }

        await server.Launch();
        return server;
    }

    public List<RemoteEvent> Events()
    {
        lock (events)
        {
            return [.. events];
        }
    }

    public int Mark() => Events().Count;

    public async Task Launch()
    {
        string executable = Executable();
        int port = FreePort();
        settings["port"] = port;
        string config = Path.Combine(Root, "config.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(settings));
        ProcessStartInfo start = new(executable, [config]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
        process = Process.Start(start)!;
        TaskCompletionSource listening = new();
        process.OutputDataReceived += (_, line) => Record(line.Data, listening);
        process.ErrorDataReceived += (_, line) => Record(line.Data, listening);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(60));
        connection = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(new ConnectionSettings { Host = "127.0.0.1", Port = port, AccessKey = "secret" }, CancellationToken.None);
        connection.EventReceived += remoteEvent =>
        {
            lock (events)
            {
                events.Add(remoteEvent);
            }
        };
    }

    public async Task Stop(bool kill = false)
    {
        if (process is null)
        {
            return;
        }

        int id = process.Id;

        if (kill)
        {
            process.Kill(true);
        }
        else
        {
            using Process signal = Process.Start("kill", ["-TERM", id.ToString(CultureInfo.InvariantCulture)])!;
            await signal.WaitForExitAsync();
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
        process.Dispose();
        process = null;
        (connection as IDisposable)?.Dispose();
        connection = null;
    }

    public async Task<WorkspaceInfo> Workspace(string name, AiService service, bool repository = true, string? folder = null, bool configure = true)
    {
        string path = folder ?? Path.Combine(Root, "work", name);
        Directory.CreateDirectory(path);

        if (repository && !Directory.Exists(Path.Combine(path, ".git")))
        {
            Git(path, "init", "-b", "main");
            Git(path, "config", "user.email", "tester@example.com");
            Git(path, "config", "user.name", "Tester");
            await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# " + name + "\n");
            Git(path, "add", "-A");
            Git(path, "commit", "-m", "base");
        }

        WorkspaceInfo workspace = await Connection.CreateWorkspace(path, service, null, CancellationToken.None);
        if (configure)
        {
            await Economize(workspace);
        }

        created.Add(workspace);
        return workspace;
    }

    public async Task Economize(WorkspaceInfo workspace, AiEffort effort = AiEffort.Low)
        => await Connection.SetWorkspaceSettings(workspace.Id, (await Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None)) with { Capability = AiCapability.Fast, Effort = effort }, CancellationToken.None);

    public async Task Environment(Func<AiEnvironment, AiEnvironment> change)
        => await Connection.SetAiEnvironment(change(await Connection.GetAiEnvironment(CancellationToken.None)), CancellationToken.None);

    public void Track(WorkspaceInfo workspace) => created.Add(workspace);

    public async Task<string> Ask(WorkspaceInfo workspace, string text, int seconds = 240)
    {
        int mark = Mark();
        await Connection.Prompt(workspace.Id, text, CancellationToken.None);
        await Idle(workspace, mark, seconds);
        return Replies(workspace, mark);
    }

    public async Task Idle(WorkspaceInfo workspace, int mark, int seconds = 240)
    {
        await Until(() => Events().Skip(mark).Any(item => item.WorkspaceId == workspace.Id && item.Kind == RemoteEventKind.WorkingChanged && item.Working == true) && Events().Skip(mark).Last(item => item.WorkspaceId == workspace.Id && item.Kind == RemoteEventKind.WorkingChanged).Working == false, seconds, "the AI to finish");
    }

    public string Replies(WorkspaceInfo workspace, int mark) => string.Join("\n", Events().Skip(mark).Where(item => item.WorkspaceId == workspace.Id && item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.Assistant).Select(item => item.Message!.Text));

    public List<string> Failures(WorkspaceInfo workspace, int mark = 0) => [.. Events().Skip(mark).Where(item => item.WorkspaceId == workspace.Id && item.Kind == RemoteEventKind.Failed).Select(item => item.Error ?? string.Empty)];

    public async Task Until(Func<bool> condition, int seconds, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {seconds} s waiting for {what}.\nEvents: {string.Join(" | ", Events().TakeLast(12).Select(item => $"{item.Kind} {item.State} {item.Working} {item.Message?.Role} {item.Message?.Text?[..Math.Min(80, item.Message.Text.Length)]} {item.Error}"))}\nOutput: {string.Join("\n", Output.TakeLast(15))}");
            }

            await Task.Delay(250);
        }
    }

    public static string Git(string folder, params string[] arguments)
    {
        ProcessStartInfo start = new("git", arguments) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        using Process git = Process.Start(start)!;
        string result = git.StandardOutput.ReadToEnd();
        string error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return result.Trim();
    }

    public static int Processes(string pattern)
    {
        using Process search = Process.Start(new ProcessStartInfo("pgrep", ["-f", pattern]) { RedirectStandardOutput = true })!;
        string found = search.StandardOutput.ReadToEnd();
        search.WaitForExit();
        return found.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public async ValueTask DisposeAsync()
    {
        if (process is not null)
        {
            foreach (WorkspaceInfo workspace in created)
            {
                try
                {
                    await Connection.DeleteWorkspace(workspace.Id, true, CancellationToken.None);
                }
                catch (Exception exception) when (exception is RemoteException or IOException or InvalidOperationException)
                {
                }
            }

            await Stop(true);
        }

        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }

    private void Record(string? line, TaskCompletionSource listening)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.Add(line);
        }

        if (line.StartsWith("Listening on port", StringComparison.Ordinal))
        {
            listening.TrySetResult();
        }
    }

    private int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string Executable()
    {
        string? folder = AppContext.BaseDirectory;

        while (folder is not null && !Directory.Exists(Path.Combine(folder, "Server")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        string candidate = Path.Combine(folder ?? string.Empty, "Server", "bin", "Debug", "net10.0", "linux-x64", "Markwardt.AutoRemote.Server");
        return File.Exists(candidate) ? candidate : throw new FileNotFoundException("The server executable was not built: " + candidate);
    }
}
