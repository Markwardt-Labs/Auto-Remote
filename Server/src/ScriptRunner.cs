namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Runs the single-file <c>.cs</c> scripts and <c>.task</c> files of workspaces and keeps their runs and output for clients to view.
/// </summary>
internal interface IScriptRunner
{
    /// <summary>
    /// Supplies the settings used to run scripts.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="stopping">Stops every run when cancelled.</param>
    void Configure(ServerConfig config, CancellationToken stopping);

    /// <summary>
    /// Checks whether a script or task is running in a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if a run is in progress.</returns>
    bool IsRunning(Guid id);

    /// <summary>
    /// Lists the runs held for a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>The runs with their output.</returns>
    ScriptRuns List(Guid id);

    /// <summary>
    /// Starts a script or task and returns once it started; progress is published as events. A run of the same file that was kept is replaced.
    /// </summary>
    /// <param name="workspace">The workspace, whose folder is the working folder of the run.</param>
    /// <param name="path">The <c>.cs</c> or <c>.task</c> file, relative to the workspace folder.</param>
    /// <param name="cancellation">Cancels reading the task file.</param>
    /// <exception cref="RequestException">A run is in progress, or the path leaves the workspace folder, is missing, or is not a script or task, or a script the task lists is not usable.</exception>
    Task Run(WorkspaceInfo workspace, string path, CancellationToken cancellation);

    /// <summary>
    /// Stops a run, all the scripts of a task.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The file of the run.</param>
    void Stop(Guid id, string path);

    /// <summary>
    /// Stops whatever script or task is running in a workspace and waits until its processes have ended; does nothing when none runs.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once nothing runs in the workspace.</returns>
    Task StopAll(Guid id);

    /// <summary>
    /// Writes a line to the standard input of a running script.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The script.</param>
    /// <param name="text">The line.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <exception cref="RequestException">The script is not running.</exception>
    Task SendInput(Guid id, string path, string text, CancellationToken cancellation);

    /// <summary>
    /// Forgets a finished run, with the runs of the scripts of a task.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="path">The file of the run.</param>
    /// <exception cref="RequestException">The run is still in progress.</exception>
    void Remove(Guid id, string path);
}

/// <inheritdoc />
internal sealed class ScriptRunner(IEventHub events, IWorkspaceActivity activity, IWorkspaceSnapshots snapshots) : IScriptRunner
{
    private readonly ConcurrentDictionary<Guid, Workspace> workspaces = new();

    private readonly int maxOutput = 500_000;

    private readonly TimeSpan flushInterval = TimeSpan.FromMilliseconds(100);

    private ServerConfig config = new() { Port = 0, Key = string.Empty };

    private CancellationToken stopping;

    /// <inheritdoc />
    public void Configure(ServerConfig config, CancellationToken stopping)
    {
        this.config = config;
        this.stopping = stopping;
    }

    /// <inheritdoc />
    public bool IsRunning(Guid id) => workspaces.TryGetValue(id, out Workspace? workspace) && workspace.Active;

    /// <inheritdoc />
    public ScriptRuns List(Guid id)
    {
        if (!workspaces.TryGetValue(id, out Workspace? workspace))
        {
            return new ScriptRuns { Runs = [] };
        }

        lock (workspace.Lock)
        {
            return new ScriptRuns { Runs = [.. workspace.Entries.Select(entry => entry.ToRun(true))] };
        }
    }

    /// <inheritdoc />
    public async Task Run(WorkspaceInfo workspace, string path, CancellationToken cancellation)
    {
        string root = Path.TrimEndingDirectorySeparator(workspace.Folder);
        string relative = Normalize(path);
        bool isTask = relative.EndsWith(".task", StringComparison.OrdinalIgnoreCase);

        if (!isTask && !relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new RequestException("Only .cs scripts and .task files can run.");
        }

        Resolve(root, relative);
        List<List<string>> batches = isTask ? await ReadBatches(root, relative, cancellation) : [[relative]];
        Workspace state = workspaces.GetOrAdd(workspace.Id, _ => new Workspace());
        Entry? main;

        lock (state.Lock)
        {
            if (state.Active)
            {
                throw new RequestException("A script is already running in this workspace.");
            }

            if (!activity.TryEnter(workspace.Id, WorkspaceState.Running))
            {
                throw new RequestException(activity.Describe(workspace.Id));
            }

            Forget(workspace.Id, state, relative);
            main = new Entry(relative, isTask, null, ScriptState.Running, [.. batches.SelectMany(batch => batch)]);
            state.Entries.Add(main);
            Publish(workspace.Id, main);

            if (isTask)
            {
                foreach (string script in main.Scripts)
                {
                    Forget(workspace.Id, state, script);
                    Entry child = new(script, false, relative, ScriptState.Pending, []);
                    state.Entries.Add(child);
                    Publish(workspace.Id, child);
                }
            }

            state.Active = true;
        }

        if (activity.Get(workspace.Id) == WorkspaceState.Running)
        {
            await snapshots.Capture(workspace, CancellationToken.None);
        }

        _ = Task.Run(() => Execute(workspace, root, state, main, batches), CancellationToken.None);
    }

    /// <inheritdoc />
    public void Stop(Guid id, string path)
    {
        if (workspaces.TryGetValue(id, out Workspace? workspace))
        {
            Entry? entry;

            lock (workspace.Lock)
            {
                entry = workspace.Entries.FirstOrDefault(item => item.Path == Normalize(path) && item.State is ScriptState.Running or ScriptState.Pending);
            }

            entry?.Cancel();
        }
    }

    /// <inheritdoc />
    public async Task StopAll(Guid id)
    {
        if (!workspaces.TryGetValue(id, out Workspace? workspace))
        {
            return;
        }

        for (int attempt = 0; attempt < 200 && IsRunning(id); attempt++)
        {
            List<Entry> active;

            lock (workspace.Lock)
            {
                active = [.. workspace.Entries.Where(entry => entry.TaskPath is null && entry.State is ScriptState.Running or ScriptState.Pending)];
            }

            foreach (Entry entry in active)
            {
                entry.Cancel();
            }

            await Task.Delay(50);
        }
    }

    /// <inheritdoc />
    public async Task SendInput(Guid id, string path, string text, CancellationToken cancellation)
    {
        Entry? entry = null;
        Workspace? workspace = null;

        if (workspaces.TryGetValue(id, out workspace))
        {
            lock (workspace.Lock)
            {
                entry = workspace.Entries.FirstOrDefault(item => item.Path == Normalize(path) && item.State == ScriptState.Running && !item.IsTask);
            }
        }

        if (entry is null || workspace is null)
        {
            throw new RequestException("That script is not running.");
        }

        await entry.WriteInput(text, cancellation);
        Append(id, workspace, entry, text + "\n");
    }

    /// <inheritdoc />
    public void Remove(Guid id, string path)
    {
        if (!workspaces.TryGetValue(id, out Workspace? workspace))
        {
            return;
        }

        lock (workspace.Lock)
        {
            Entry? entry = workspace.Entries.FirstOrDefault(item => item.Path == Normalize(path));

            if (entry is null)
            {
                return;
            }

            if (entry.State is ScriptState.Running or ScriptState.Pending || workspace.Entries.Any(item => item.TaskPath == entry.Path && item.State is ScriptState.Running or ScriptState.Pending))
            {
                throw new RequestException("Stop the run before removing it.");
            }

            Forget(id, workspace, entry.Path);
        }
    }

    private string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private string Resolve(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new RequestException($"The path '{relative}' leaves the workspace folder.");
        }

        if (!File.Exists(full))
        {
            throw new RequestException($"The file '{relative}' does not exist.");
        }

        return full;
    }

    private async Task<List<List<string>>> ReadBatches(string root, string relative, CancellationToken cancellation)
    {
        string content = await File.ReadAllTextAsync(Resolve(root, relative), cancellation);
        List<List<string>> batches = [[]];
        HashSet<string> seen = [];

        foreach (string raw in content.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line == "-")
            {
                batches.Add([]);
                continue;
            }

            string script = Normalize(line);

            if (!script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                throw new RequestException($"The task lists '{line}', which is not a .cs script.");
            }

            Resolve(root, script);

            if (seen.Add(script))
            {
                batches[^1].Add(script);
            }
        }

        batches = [.. batches.Where(batch => batch.Count > 0)];

        if (batches.Count == 0)
        {
            throw new RequestException("The task lists no scripts.");
        }

        return batches;
    }

    private void Forget(Guid id, Workspace workspace, string path)
    {
        foreach (Entry entry in workspace.Entries.Where(item => item.Path == path || item.TaskPath == path).ToList())
        {
            workspace.Entries.Remove(entry);
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.ScriptRemoved, WorkspaceId = id, Path = entry.Path });
        }
    }

    private void Publish(Guid id, Entry entry) => events.Publish(new RemoteEvent { Kind = RemoteEventKind.ScriptChanged, WorkspaceId = id, Script = entry.ToRun(false) });

    private void Append(Guid id, Workspace workspace, Entry entry, string text)
    {
        lock (workspace.Lock)
        {
            entry.Output.Append(text);
            entry.Pending.Append(text);

            if (entry.Output.Length > maxOutput)
            {
                entry.Output.Remove(0, entry.Output.Length - maxOutput);
                entry.Trimmed = true;
            }

            if (!entry.Flushing)
            {
                entry.Flushing = true;
                _ = FlushLater(id, workspace, entry);
            }
        }
    }

    private async Task FlushLater(Guid id, Workspace workspace, Entry entry)
    {
        await Task.Delay(flushInterval, CancellationToken.None);

        lock (workspace.Lock)
        {
            entry.Flushing = false;
            Flush(id, entry);
        }
    }

    private void Flush(Guid id, Entry entry)
    {
        if (entry.Pending.Length > 0)
        {
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.ScriptOutput, WorkspaceId = id, Path = entry.Path, Text = entry.Pending.ToString() });
            entry.Pending.Clear();
        }
    }

    private void Finish(Guid id, Workspace workspace, Entry entry, ScriptState state)
    {
        lock (workspace.Lock)
        {
            Flush(id, entry);
            entry.State = state;
            Publish(id, entry);
        }
    }

    private async Task Execute(WorkspaceInfo workspace, string root, Workspace state, Entry main, List<List<string>> batches)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        main.Bind(linked);
        ScriptState result = ScriptState.Failed;

        try
        {
            result = main.IsTask ? await RunTask(workspace.Id, root, state, main, batches, linked) : await RunScript(workspace.Id, root, state, main, linked.Token);
        }
        finally
        {
            lock (state.Lock)
            {
                state.Active = false;
            }

            if (activity.Get(workspace.Id) == WorkspaceState.Running)
            {
                snapshots.Discard(workspace.Id);
            }

            activity.Leave(workspace.Id, WorkspaceState.Running);
            Finish(workspace.Id, state, main, result);
        }
    }

    private async Task<ScriptState> RunTask(Guid id, string root, Workspace state, Entry main, List<List<string>> batches, CancellationTokenSource linked)
    {
        ScriptState result = ScriptState.Succeeded;

        try
        {
            foreach (List<string> batch in batches)
            {
                if (linked.IsCancellationRequested)
                {
                    result = ScriptState.Stopped;
                    break;
                }

                List<Entry> children;

                lock (state.Lock)
                {
                    children = [.. batch.Select(script => state.Entries.First(item => item.Path == script && item.TaskPath == main.Path))];

                    foreach (Entry child in children)
                    {
                        child.State = ScriptState.Running;
                        Publish(id, child);
                    }
                }

                ScriptState[] results = await Task.WhenAll(children.Select(async child =>
                {
                    child.Bind(linked);
                    ScriptState childResult = await RunScript(id, root, state, child, linked.Token);
                    Finish(id, state, child, childResult);
                    return childResult;
                }));

                if (results.Any(item => item != ScriptState.Succeeded))
                {
                    result = linked.IsCancellationRequested ? ScriptState.Stopped : ScriptState.Failed;
                    break;
                }
            }
        }
        finally
        {
            lock (state.Lock)
            {
                foreach (Entry child in state.Entries.Where(item => item.TaskPath == main.Path && item.State == ScriptState.Pending).ToList())
                {
                    child.State = ScriptState.Skipped;
                    Publish(id, child);
                }
            }
        }

        return result;
    }

    private async Task<ScriptState> RunScript(Guid id, string root, Workspace state, Entry entry, CancellationToken cancellation)
    {
        ProcessStartInfo start = new(config.DotnetCommand)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in (string[])["run", "--file", Path.Combine(root, entry.Path)])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Append(id, state, entry, $"Could not start '{config.DotnetCommand}': {exception.Message}\n");
            return ScriptState.Failed;
        }

        entry.Attach(process);

        using CancellationTokenRegistration registration = cancellation.Register(() => Kill(process));
        Task pumps = Task.WhenAll(Pump(id, state, entry, process.StandardOutput), Pump(id, state, entry, process.StandardError));

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await pumps;
        }
        finally
        {
            entry.Detach();
        }

        return cancellation.IsCancellationRequested ? ScriptState.Stopped : process.ExitCode == 0 ? ScriptState.Succeeded : ScriptState.Failed;
    }

    private void Kill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private async Task Pump(Guid id, Workspace state, Entry entry, StreamReader reader)
    {
        char[] buffer = new char[4096];
        int read;

        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            Append(id, state, entry, new string(buffer, 0, read));
        }
    }

    private sealed class Workspace
    {
        public Lock Lock { get; } = new();

        public List<Entry> Entries { get; } = [];

        public bool Active { get; set; }
    }

    private sealed class Entry(string path, bool isTask, string? taskPath, ScriptState state, IReadOnlyList<string> scripts)
    {
        private readonly SemaphoreSlim writing = new(1, 1);

        private CancellationTokenSource? cancellation;

        private Process? process;

        public string Path { get; } = path;

        public bool IsTask { get; } = isTask;

        public string? TaskPath { get; } = taskPath;

        public IReadOnlyList<string> Scripts { get; } = scripts;

        public ScriptState State { get; set; } = state;

        public StringBuilder Output { get; } = new();

        public StringBuilder Pending { get; } = new();

        public bool Trimmed { get; set; }

        public bool Flushing { get; set; }

        public ScriptRun ToRun(bool withOutput) => new()
        {
            Path = Path,
            Name = System.IO.Path.GetFileNameWithoutExtension(Path),
            State = State,
            IsTask = IsTask,
            TaskPath = TaskPath,
            Scripts = Scripts,
            Output = withOutput ? Output.ToString() : string.Empty,
            Trimmed = Trimmed,
        };

        public void Bind(CancellationTokenSource source) => cancellation = source;

        public void Cancel()
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Attach(Process started) => process = started;

        public void Detach() => process = null;

        public async Task WriteInput(string text, CancellationToken token)
        {
            Process? current = process;

            if (current is null)
            {
                throw new RequestException("That script is not running.");
            }

            await writing.WaitAsync(token);

            try
            {
                await current.StandardInput.WriteLineAsync(text.AsMemory(), token);
                await current.StandardInput.FlushAsync(token);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                throw new RequestException("That script is not running.");
            }
            finally
            {
                writing.Release();
            }
        }
    }
}
