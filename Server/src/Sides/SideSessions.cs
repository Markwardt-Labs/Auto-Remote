namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The side sessions of workspaces: making them (a worktree of their own, an AI session that knows what main's knew, an empty chat and the workspace's setup), renaming and removing them.
/// </summary>
internal interface ISideSessions
{
    /// <summary>
    /// Sets where side sessions are stored from now on and removes what interrupted creations and removals left behind.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="cancellation">Cancels the setup when the server stops.</param>
    /// <returns>A task that completes once the leftovers are gone.</returns>
    Task Open(ServerConfig config, CancellationToken cancellation);

    /// <summary>
    /// Makes a side session. Its files and its AI's knowledge are taken before this returns; copying the workspace's files into it, its setup script and its first task happen after.
    /// </summary>
    /// <param name="mainId">The main workspace.</param>
    /// <param name="name">The name of the session.</param>
    /// <param name="start">What its files start from.</param>
    /// <param name="firstTask">The first prompt of the session, or <see langword="null" />.</param>
    /// <param name="bringIn">Whether it is brought into the main workspace by itself once its work is done.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The session, which is being set up.</returns>
    /// <exception cref="RequestException">The workspace cannot have side sessions, the name is taken or something could not be made.</exception>
    Task<WorkspaceInfo> Create(Guid mainId, string name, SideSessionStart start, string? firstTask, bool bringIn, CancellationToken cancellation);

    /// <summary>
    /// Makes a side session that goes on with the result of a delegated task: its files are as the task left them, what it changed is measured against what the task started from, its AI knows what the AI of the workspace the task came from knew, and its chat starts with the task's report. It is kept separate until the user brings it in.
    /// </summary>
    /// <param name="originId">The workspace or side session the task was delegated from; a side session of the latter belongs to the same main workspace.</param>
    /// <param name="name">The name of the session.</param>
    /// <param name="from">What it starts from.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The session, which is being set up.</returns>
    /// <exception cref="RequestException">The workspace cannot have side sessions, the name is taken or something could not be made.</exception>
    Task<WorkspaceInfo> Continue(Guid originId, string name, SideSessionContinuation from, CancellationToken cancellation);

    /// <summary>
    /// Renames a side session. Its folder and branch keep their names.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellation">Cancels the change.</param>
    /// <returns>The session.</returns>
    Task<WorkspaceInfo> Rename(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Removes a side session and everything it holds, unless it is being integrated.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>A task that completes once it is gone.</returns>
    Task Delete(Guid id);

    /// <summary>
    /// Removes every side session of a workspace that is being deleted; their integrations have to be cancelled first.
    /// </summary>
    /// <param name="mainId">The main workspace.</param>
    /// <returns>A task that completes once they are gone.</returns>
    Task DeleteAll(Guid mainId);

    /// <summary>
    /// Mends the links between a main repository and its side sessions after the main folder moved.
    /// </summary>
    /// <param name="mainId">The main workspace.</param>
    /// <returns>A task that completes once done.</returns>
    Task Repair(Guid mainId);

    /// <summary>
    /// Lists what changed in a side session since it started.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The changed files.</returns>
    Task<GitChanges> Changes(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Compares a file of a side session with how it was when the session started.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <param name="path">The file.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The comparison.</returns>
    Task<GitDiff> Diff(Guid id, string path, CancellationToken cancellation);

    /// <summary>
    /// Refuses a folder a workspace is about to be moved to that does not hold what its side sessions were made from.
    /// </summary>
    /// <param name="mainId">The workspace.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>A task that completes if the folder will do.</returns>
    /// <exception cref="RequestException">The side sessions belong to another repository.</exception>
    Task RequireSameRepository(Guid mainId, string folder, CancellationToken cancellation);

    /// <summary>
    /// Holds one side session still while its state is decided: its removal, and the start, finish and cancellation of its integration happen one at a time.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>Lets go when disposed.</returns>
    Task<IDisposable> Lock(Guid id);

    /// <summary>
    /// Removes a side session whose state the caller decided on while holding <see cref="Lock" />. What of it cannot be removed safely is left on disk and told in the main workspace's chat.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>A task that completes once it is gone.</returns>
    Task Remove(Guid id);

    /// <summary>
    /// Lists the folders no removal may touch: the workspaces and the user's home folder.
    /// </summary>
    /// <param name="except">A side session whose own folder is left out.</param>
    /// <returns>The folders.</returns>
    Task<IReadOnlyList<string>> Protected(Guid except);
}

/// <inheritdoc />
internal sealed class SideSessions(IWorkspaceManager workspaces, ISideSessionRepository repository, IWorkspaceStore store, IPromptService prompts, IWorkQueue queue, IWorkspaceLoops loops, IScriptRunner scripts, IWindowGuard guard, IWorkspaceSnapshots snapshots, IWorkspaceWatcher watcher, IEventHub events, ILog log, Func<IFollowUps?>? following = null) : ISideSessions
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    private readonly SemaphoreSlim creating = new(1, 1);

    private readonly ConcurrentDictionary<Guid, Making> making = new();

    private readonly int nameLimit = 60;

    private readonly string attachedPrefix = "Attached file: ";

    private readonly TimeSpan scriptPoll = TimeSpan.FromMilliseconds(200);

    private string root = string.Empty;

    private CancellationToken stopping;

    /// <inheritdoc />
    public async Task Open(ServerConfig config, CancellationToken cancellation)
    {
        stopping = cancellation;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.SessionsFolder ?? Path.Combine(config.DataFolder, "sessions")));

        // A session that was being made or removed when the server stopped is only a leftover.
        foreach ((Guid id, StoredSideSession side) in workspaces.Sides().Where(entry => entry.Side.State is SideSessionState.Creating or SideSessionState.Deleting).ToList())
        {
            log.Write($"Removing what is left of the side session '{side.Name}'");

            try
            {
                using (await Lock(id))
                {
                    await Remove(id);
                }
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
            {
                log.Write($"The side session '{side.Name}' could not be removed: {exception.Message}");
            }
        }
    }

    /// <inheritdoc />
    public Task<WorkspaceInfo> Create(Guid mainId, string name, SideSessionStart start, string? firstTask, bool bringIn, CancellationToken cancellation) => Create(mainId, name, start, firstTask, bringIn, null, null, cancellation);

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Continue(Guid originId, string name, SideSessionContinuation from, CancellationToken cancellation)
    {
        WorkspaceInfo origin = await workspaces.Get(originId, cancellation);
        return await Create(origin.Side?.ParentId ?? originId, name, SideSessionStart.Files, null, false, from, originId, cancellation);
    }

    private async Task<WorkspaceInfo> Create(Guid mainId, string name, SideSessionStart start, string? firstTask, bool bringIn, SideSessionContinuation? continuation, Guid? knowledge, CancellationToken cancellation)
    {
        WorkspaceInfo main = await workspaces.Get(mainId, cancellation);

        if (main.Side is not null)
        {
            throw new RequestException("A side session cannot have side sessions of its own.");
        }

        if (!Enum.IsDefined(start))
        {
            throw new RequestException("Choose what the session starts from.");
        }

        await repository.RequireRepository(main.Folder, cancellation);

        if ((await workspaces.List(cancellation)).Workspaces.Any(workspace => Inside(root, workspace.Folder)))
        {
            throw new RequestException($"Side sessions are stored in '{root}', which is inside a workspace. Set sessionsFolder in the server's config file to a folder outside every workspace.");
        }

        Guid id = Guid.NewGuid();
        string? task = string.IsNullOrWhiteSpace(firstTask) ? null : await CopyAttachments(main.Id, id, firstTask.Trim(), cancellation);
        // Known before the session is, so that a removal that comes while it is being made finds what to wait for.
        Making work = new(CancellationTokenSource.CreateLinkedTokenSource(stopping, cancellation));
        making[id] = work;
        bool handedOn = false;

        try
        {
            await creating.WaitAsync(cancellation);

            try
            {
                string folder = repository.Folder(root, mainId, id);
                await workspaces.AddSide(id, new StoredSideSession { ParentId = mainId, Name = ValidName(mainId, name, id), Service = main.Service, State = SideSessionState.Creating, StartedFrom = start, Root = root, Folder = folder, UnsentTask = task, IntegrateWhenFinished = bringIn }, cancellation);
            }
            finally
            {
                creating.Release();
            }

            try
            {
                await Make(main, id, start, continuation, knowledge ?? main.Id, work.Cancel.Token);
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                using (await Lock(id))
                {
                    await Remove(id);
                }

                if (exception is IOException or UnauthorizedAccessException)
                {
                    throw new RequestException($"The side session could not be made: {exception.Message}");
                }

                if (exception is OperationCanceledException && !stopping.IsCancellationRequested && !cancellation.IsCancellationRequested)
                {
                    throw new RequestException("The side session was deleted while it was being made.");
                }

                throw;
            }

            WorkspaceInfo made = await workspaces.Get(id, CancellationToken.None);
            // The request that made it may end before it is set up.
            Making setting = new(CancellationTokenSource.CreateLinkedTokenSource(stopping));
            making[id] = setting;
            handedOn = true;

            // A removal that already asked the making to end has to end the setting up as well; the end of the request alone does not.
            if (work.IsHalted)
            {
                await setting.Cancel.CancelAsync();
            }

            work.Done.TrySetResult();
            work.Cancel.Dispose();
            _ = Task.Run(() => SetUp(id, task, setting), CancellationToken.None);
            return made;
        }
        finally
        {
            if (!handedOn)
            {
                Finished(id, work);
            }
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Rename(Guid id, string name, CancellationToken cancellation)
    {
        StoredSideSession side = Find(id);
        string valid = ValidName(side.ParentId, name, id);
        await workspaces.UpdateSide(id, current => current with { Name = valid });
        return await workspaces.Get(id, cancellation);
    }

    /// <inheritdoc />
    public async Task Delete(Guid id)
    {
        Find(id);
        await Halt(id);

        using (await Lock(id))
        {
            if (workspaces.GetSide(id) is not { } side)
            {
                return;
            }

            if (side.State == SideSessionState.Integrating)
            {
                throw new RequestException("The side session is being brought into main. Cancel that first.");
            }

            await Remove(id);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAll(Guid mainId)
    {
        foreach ((Guid id, StoredSideSession _) in workspaces.Sides(mainId))
        {
            await Halt(id);

            using (await Lock(id))
            {
                await Remove(id);
            }
        }
    }

    /// <inheritdoc />
    public async Task Repair(Guid mainId)
    {
        WorkspaceInfo main = await workspaces.Get(mainId, CancellationToken.None);
        await repository.Repair(main.Folder, [.. workspaces.Sides(mainId).Select(entry => entry.Side.Folder)]);
    }

    /// <inheritdoc />
    public Task<GitChanges> Changes(Guid id, CancellationToken cancellation)
    {
        StoredSideSession side = Find(id);
        return repository.Changes(side.Folder, side.BaseTree, cancellation);
    }

    /// <inheritdoc />
    public Task<GitDiff> Diff(Guid id, string path, CancellationToken cancellation)
    {
        StoredSideSession side = Find(id);
        return repository.Diff(side.Folder, side.BaseTree, path, cancellation);
    }

    /// <inheritdoc />
    public async Task RequireSameRepository(Guid mainId, string folder, CancellationToken cancellation)
    {
        List<Guid> made = [.. workspaces.Sides(mainId).Where(entry => entry.Side.BaseTree.Length > 0).Select(entry => entry.Id)];

        // A folder that does not exist is refused by the move itself.
        if (made.Count > 0 && Directory.Exists(folder) && !await repository.Holds(folder, made, cancellation))
        {
            throw new RequestException("The side sessions of this workspace belong to the repository it is in now. Bring them into main or delete them before moving it to a different one.");
        }
    }

    /// <inheritdoc />
    public async Task<IDisposable> Lock(Guid id)
    {
        SemaphoreSlim gate = locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        return new Releaser(gate);
    }

    /// <inheritdoc />
    public async Task Remove(Guid id)
    {
        if (workspaces.GetSide(id) is not { } side)
        {
            return;
        }

        await workspaces.UpdateSide(id, current => current with { State = SideSessionState.Deleting, Integration = null });
        WorkspaceInfo session = await workspaces.Get(id, CancellationToken.None);
        await loops.Cancel(id);
        await queue.Cancel(id);
        await scripts.StopAll(id);
        watcher.Forget(id);
        // Ends its work and deletes its AI conversation, which is a copy of its own.
        await prompts.Forget(session, true);

        if (side.Integration is { } integration)
        {
            prompts.Unshare(side.ParentId, integration.Id);
        }

        string main = string.Empty;

        try
        {
            main = (await workspaces.Get(side.ParentId, CancellationToken.None)).Folder;
        }
        catch (RequestException)
        {
        }

        try
        {
            await repository.Remove(main, id, side, await Protected(id));
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
        {
            log.Write($"The files of the side session '{side.Name}' were left in place: {exception.Message}");

            if (main.Length > 0)
            {
                events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = side.ParentId, Time = DateTimeOffset.UtcNow, Error = $"The side session '{side.Name}' was removed, but its files were left in '{side.Folder}': {exception.Message}", IsNotice = true });
            }
        }

        snapshots.Discard(id);
        await workspaces.Delete(id, false, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> Protected(Guid except)
    {
        WorkspaceCatalog catalog = await workspaces.List(CancellationToken.None);
        return [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. catalog.Workspaces.Select(workspace => workspace.Folder), .. workspaces.Sides().Where(entry => entry.Id != except).Select(entry => entry.Side.Folder)];
    }

    private StoredSideSession Find(Guid id) => workspaces.GetSide(id) ?? throw new RequestException("The side session no longer exists.");

    private string ValidName(Guid mainId, string name, Guid id)
    {
        string trimmed = name.Trim();

        if (trimmed.Length == 0 || trimmed.Any(char.IsControl))
        {
            throw new RequestException("Give the session a name.");
        }

        if (trimmed.Length > nameLimit)
        {
            throw new RequestException($"The name can have at most {nameLimit} characters.");
        }

        if (trimmed.Equals("main", StringComparison.OrdinalIgnoreCase) || workspaces.Sides(mainId).Any(entry => entry.Id != id && entry.Side.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RequestException($"There is already a session named '{trimmed}'.");
        }

        return trimmed;
    }

    private bool Inside(string child, string parent) => child == parent || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // A first task may name files attached to another chat of the same workspace, which a sandboxed AI of the new session could not read there, so it gets copies of its own.
    private async Task<string> CopyAttachments(Guid mainId, Guid id, string task, CancellationToken cancellation)
    {
        string[] lines = task.Split('\n');
        string attachments = Path.GetDirectoryName(store.AttachmentsFolder(mainId)) is { Length: > 0 } root ? root + Path.DirectorySeparatorChar : string.Empty;

        for (int index = lines.Length - 1; index >= 0 && lines[index].StartsWith(attachedPrefix, StringComparison.Ordinal); index--)
        {
            string path = lines[index][attachedPrefix.Length..].Trim();

            // Only what was attached to a chat is copied, nothing else the text happens to name.
            if (attachments.Length > 0 && Path.GetFullPath(path).StartsWith(attachments, StringComparison.Ordinal) && File.Exists(path))
            {
                try
                {
                    string name = Path.GetFileName(path);
                    int separator = name.IndexOf('-', StringComparison.Ordinal);
                    string copy = await store.SaveAttachment(id, separator >= 0 ? name[(separator + 1)..] : name, await File.ReadAllBytesAsync(path, cancellation), cancellation);
                    lines[index] = attachedPrefix + copy;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log.Write($"An attached file could not be copied for a new side session: {exception.Message}");
                }
            }
        }

        return string.Join('\n', lines);
    }

    private async Task Make(WorkspaceInfo main, Guid id, SideSessionStart start, SideSessionContinuation? continuation, Guid knowledge, CancellationToken cancellation)
    {
        StoredSideSession side = workspaces.GetSide(id) ?? throw new RequestException("The side session no longer exists.");
        // Git is not cut off by a removal: what it makes is removed whole once it is done.
        (string commit, string tree) = continuation is null
            ? await repository.Create(main.Folder, id, side.Folder, start, stopping)
            : await repository.CreateFrom(main.Folder, id, side.Folder, continuation.BaseCommit, continuation.ResultCommit, stopping);
        cancellation.ThrowIfCancellationRequested();
        await workspaces.UpdateSide(id, current => current with { StartCommit = commit, BaseTree = tree });
        // An empty chat of its own, so that what its AI learned before it was made is never shown or read back as its chat.
        await store.SaveMessages(id, continuation is null ? [] : [new ChatMessage { Role = MessageRole.Assistant, Text = continuation.Report, Time = DateTimeOffset.UtcNow }], CancellationToken.None);
        string fork = await prompts.Fork(knowledge, side.Folder, cancellation);

        try
        {
            cancellation.ThrowIfCancellationRequested();
            await workspaces.AttachSession(id, fork, CancellationToken.None);
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await prompts.Discard(fork);
            throw;
        }
    }

    // Ends the making and setting up of a session and waits until nothing of it adds to the session any more.
    private async Task Halt(Guid id)
    {
        while (making.TryGetValue(id, out Making? work))
        {
            work.IsHalted = true;

            try
            {
                await work.Cancel.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            await work.Done.Task;
        }
    }

    private void Finished(Guid id, Making work)
    {
        making.TryRemove(new KeyValuePair<Guid, Making>(id, work));
        work.Done.TrySetResult();
        work.Cancel.Dispose();
    }

    // Runs after the session was returned: what the workspace wants copied and run in it, and then its first task. Removing the session meanwhile ends it.
    private async Task SetUp(Guid id, string? task, Making work)
    {
        try
        {
            CancellationToken cancellation = work.Cancel.Token;
            cancellation.ThrowIfCancellationRequested();

            if (workspaces.GetSide(id) is not { State: SideSessionState.Creating } side)
            {
                return;
            }

            WorkspaceInfo main = await workspaces.Get(side.ParentId, cancellation);
            string? error = null;

            try
            {
                Copy(main.Folder, side.Folder, main.SessionFiles, cancellation);

                if (main.SessionSetupScript is { } script)
                {
                    await RunSetup(id, script, cancellation);
                }
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
            {
                error = $"Setting up the session failed: {exception.Message}";
            }

            cancellation.ThrowIfCancellationRequested();

            if (await workspaces.UpdateSide(id, current => current.State == SideSessionState.Creating ? current with { State = SideSessionState.Ready, Error = error, UnsentTask = task } : current) is not { State: SideSessionState.Ready } || error is not null || task is null)
            {
                return;
            }

            // Going past the usage limit takes the user's word, which they can give by sending it themselves.
            if (await guard.Over(id, side.Service, task, cancellation) is not null)
            {
                await workspaces.UpdateSide(id, current => current with { Error = "The first task was not sent, because a usage window is at or past the max usage window limit. Send it from the message box to start it anyway." });
                return;
            }

            // The task a session is started with is done by itself, so its AI is told nobody may be there.
            WorkStep step = new() { Source = ActivitySource.Send, Text = task, Preface = "This is the task this side session was started with." };
            IFollowUps? followed = following?.Invoke();
            followed?.Expect(id, step);

            try
            {
                await queue.Submit(id, task, WorkSource.Direct, cancellation);
                followed?.Sent(id, step.Id);
                await workspaces.UpdateSide(id, current => current with { UnsentTask = null });
            }
            catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException)
            {
                followed?.NotSent(id, step.Id);
                await workspaces.UpdateSide(id, current => current with { Error = $"The first task could not be sent: {exception.Message}" });
            }
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            log.Write($"Setting up a side session ended: {exception.Message}");
        }
        finally
        {
            Finished(id, work);
        }
    }

    private void Copy(string from, string into, string? list, CancellationToken cancellation)
    {
        foreach (string line in (list ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string source = Path.GetFullPath(Path.Combine(from, line));
            string relative = Path.GetRelativePath(from, source);

            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar)[0] == ".git")
            {
                throw new RequestException($"'{line}' is not a path inside the workspace, so it cannot be copied.");
            }

            string target = Path.Combine(into, relative);

            if (File.Exists(source))
            {
                CopyFile(source, target, cancellation);
            }
            else if (Directory.Exists(source))
            {
                CopyFolder(new DirectoryInfo(source), target, cancellation);
            }
            else
            {
                throw new RequestException($"'{line}' does not exist in the workspace, so it could not be copied.");
            }
        }
    }

    // What git already brought into the session is left as it is.
    private void CopyFile(string source, string target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        if (File.Exists(target) || Directory.Exists(target))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (new FileInfo(source).LinkTarget is { } link)
        {
            File.CreateSymbolicLink(target, link);
        }
        else
        {
            File.Copy(source, target);
        }
    }

    private void CopyFolder(DirectoryInfo source, string target, CancellationToken cancellation)
    {
        // A link to a folder is made again as the link it is, never followed, which could copy without end.
        if (source.LinkTarget is { } link)
        {
            if (!Directory.Exists(target) && !File.Exists(target))
            {
                Directory.CreateSymbolicLink(target, link);
            }

            return;
        }

        Directory.CreateDirectory(target);

        foreach (FileInfo file in source.EnumerateFiles())
        {
            CopyFile(file.FullName, Path.Combine(target, file.Name), cancellation);
        }

        foreach (DirectoryInfo folder in source.EnumerateDirectories())
        {
            CopyFolder(folder, Path.Combine(target, folder.Name), cancellation);
        }
    }

    private async Task RunSetup(Guid id, string script, CancellationToken cancellation)
    {
        // As the runner names its runs.
        string path = script.Replace('\\', '/').Trim('/');
        WorkspaceInfo session = await workspaces.Get(id, cancellation);
        await scripts.Run(session, path, new Dictionary<string, string>(), cancellation);

        while (scripts.IsRunning(id))
        {
            await Task.Delay(scriptPoll, cancellation);
        }

        if (scripts.List(id).Runs.FirstOrDefault(entry => entry.Path == path) is not { State: ScriptState.Succeeded })
        {
            throw new RequestException($"The setup script '{script}' did not succeed. Its output is in the Script tab.");
        }
    }

    private sealed class Making(CancellationTokenSource cancel)
    {
        public CancellationTokenSource Cancel { get; } = cancel;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsHalted { get; set; }
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
