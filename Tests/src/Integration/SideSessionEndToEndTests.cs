namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class SideSessionEndToEndTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly GitService git = new();

    private SwarmTestServer server = null!;

    private readonly List<ManualResetEventSlim> held = [];

    private Func<FakePromptSession, FakePrompt, IEnumerable<PromptEvent>> integrate = (_, _) => [new PromptMessageEvent { Text = "not yet" }];

    private IServerConnection Connection => server.Connection;

    public async Task InitializeAsync()
    {
        string dotnet = temp.Combine("fake-dotnet");
        // Scripts are run as "dotnet run --file <script>"; this one runs the script's lines with the shell.
        await Executable(dotnet, "#!/bin/sh\nexec /bin/sh \"$3\"\n");
        server = await SwarmTestServer.Start(temp.Path, "server", dotnet);
        server.Ai = Work;
    }

    private async Task Executable(string path, string text)
    {
        await File.WriteAllTextAsync(path, text);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public async Task DisposeAsync()
    {
        held.ForEach(release => release.Set());
        await server.DisposeAsync();
        temp.Dispose();
    }

    // What was asked, without what the server adds for the AI.
    private string Asked(string text) => text.Split("\n\n[Auto Remote]\n")[0];

    // "write NAME TEXT" writes a file where the session works, and a request to bring a session in is left to the test.
    private IEnumerable<PromptEvent> Work(FakePromptSession session, FakePrompt prompt)
    {
        if (prompt.Text.StartsWith("Integrate the side session", StringComparison.Ordinal))
        {
            return integrate(session, prompt);
        }

        string[] words = Asked(prompt.Text).Split(' ', 3);

        if (words[0] == "write")
        {
            File.WriteAllText(Path.Combine(session.WorkspacePath!, words[1]), words[2] + "\n");
        }

        return [new PromptMessageEvent { Text = $"did: {words[0]}" }];
    }

    private string Use(FakePromptSession session, string tool, object arguments)
    {
        PromptTool found = Assert.Single(session.Transient.Tools, candidate => candidate.Name == tool);
        return Task.Run(() => found.Handler(JsonSerializer.SerializeToElement(arguments), CancellationToken.None)).GetAwaiter().GetResult();
    }

    private string IntegrationId(string request) => Regex.Match(request, "integration ([0-9a-f]{12})").Groups[1].Value;

    private string SideFolder(string request) => Regex.Match(request, "Its files are in (.+?)\\. Its change list").Groups[1].Value;

    // What main's AI does: reads the session's folder, brings its files over and finishes.
    private IEnumerable<PromptEvent> CopyAndFinish(FakePromptSession session, FakePrompt prompt)
    {
        string from = SideFolder(prompt.Text);

        foreach (string file in Directory.EnumerateFiles(from, "*.txt"))
        {
            File.Copy(file, Path.Combine(session.WorkspacePath!, Path.GetFileName(file)), true);
        }

        return [new PromptMessageEvent { Text = Use(session, "finish_integration", new { integration_id = IntegrationId(prompt.Text) }) }];
    }

    private async Task<string> Git(string folder, params string[] arguments)
    {
        GitOutput result = await git.Run(folder, ["-c", "user.name=t", "-c", "user.email=t@t", .. arguments], CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        return result.Output.Trim();
    }

    private async Task<(WorkspaceInfo Workspace, string Folder)> Project(string name = "project")
    {
        string folder = temp.Combine(name);
        Directory.CreateDirectory(folder);
        await Git(folder, "init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "one\n");
        await File.WriteAllTextAsync(Path.Combine(folder, ".gitignore"), "ignored/\n");
        await Git(folder, "add", "-A");
        await Git(folder, "commit", "--quiet", "-m", "first");
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "one\ntwo\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "uncommitted.txt"), "not yet committed\n");
        Directory.CreateDirectory(Path.Combine(folder, "ignored"));
        await File.WriteAllTextAsync(Path.Combine(folder, "ignored", "secret.env"), "key\n");
        return (await Connection.CreateWorkspace(folder, AiService.Local, null, null, CancellationToken.None), folder);
    }

    private async Task<IReadOnlyList<WorkspaceInfo>> Sessions() => (await Connection.ListWorkspaces(CancellationToken.None)).Sessions;

    private async Task<WorkspaceInfo?> Session(Guid id) => (await Sessions()).FirstOrDefault(session => session.Id == id);

    private async Task<WorkspaceInfo> Side(Guid main, string name = "fix-login", SideSessionStart start = SideSessionStart.Files, string? task = null, bool bringIn = false)
    {
        WorkspaceInfo made = await Connection.CreateSideSession(main, name, start, task, bringIn, CancellationToken.None);
        await SwarmTestServer.Until(async () => await Session(made.Id) is { Side.State: SideSessionState.Ready }, async () => string.Join("; ", (await Sessions()).Select(session => $"{session.Side!.State} {session.Side.Error}")));
        return (await Session(made.Id))!;
    }

    private async Task Idle(Guid id) => await SwarmTestServer.Until(async () => await Connection.GetWorkspaceState(id, CancellationToken.None) == WorkspaceState.Ready);

    private async Task<string> Chat(Guid id) => string.Join("\n", (await Connection.GetHistory(id, null, null, null, CancellationToken.None)).Messages.Select(message => message.Text));

    private async Task Say(Guid id, string text)
    {
        await Connection.Prompt(id, text, CancellationToken.None);
        await SwarmTestServer.Until(async () => (await Chat(id)).Contains($"did: {text.Split(' ')[0]}") && await Connection.GetWorkspaceState(id, CancellationToken.None) == WorkspaceState.Ready, () => Chat(id));
    }

    // How often main was given a request to integrate: each time it is sent, it shows in main's chat.
    private async Task<int> Requests(Guid main)
        => (await Connection.GetHistory(main, null, null, null, CancellationToken.None)).Messages.Count(message => message.Role == MessageRole.User && message.Text.StartsWith("Integrate the side session", StringComparison.Ordinal));

    private async Task Requested(Guid main, int times) => await SwarmTestServer.Until(async () => await Requests(main) == times, async () => $"{await Requests(main)} requests");

    // Main's AI is held on the request it was given until the test lets it end, and then says what it did without confirming it.
    private (Task<string> Requested, ManualResetEventSlim Release) HoldTheRequest(Func<FakePromptSession, FakePrompt, IEnumerable<PromptEvent>>? then = null)
    {
        TaskCompletionSource<string> requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualResetEventSlim release = new();
        held.Add(release);
        integrate = (session, prompt) =>
        {
            requested.TrySetResult(IntegrationId(prompt.Text));
            release.Wait(TimeSpan.FromSeconds(20));
            return then?.Invoke(session, prompt) ?? [new PromptMessageEvent { Text = "half done" }];
        };
        return (requested.Task.WaitAsync(TimeSpan.FromSeconds(15)), release);
    }

    private async Task Gone(WorkspaceInfo side) => await SwarmTestServer.Until(async () => await Session(side.Id) is null, async () => $"{(await Session(side.Id))?.Side?.State} {(await Session(side.Id))?.Side?.Error}");

    private async Task<IReadOnlyList<ActivityEntry>> Activity(Guid id) => (await Connection.GetActivity(id, null, 100, CancellationToken.None)).Entries;

    private string[] AiGot(string start)
    {
        lock (server.Asked)
        {
            return [.. server.Asked.Select(asked => asked.Text).Where(text => text.StartsWith(start, StringComparison.Ordinal))];
        }
    }

    private FakePromptSession Main(string folder) => server.Prompter.Sessions.Last(session => session.WorkspacePath == folder);

    private async Task NothingIsLeft(string folder, WorkspaceInfo side)
    {
        Assert.False(Directory.Exists(side.Folder));
        Assert.False(Directory.Exists(side.Folder + ".integration"));
        Assert.Equal("main", await Git(folder, "branch", "--format=%(refname:short)"));
        Assert.Empty(await Git(folder, "for-each-ref", "--format=%(refname)", "refs/auto-remote"));
        Assert.Single((await Git(folder, "worktree", "list")).Split('\n'));
    }

    [Fact]
    public async Task ASessionFromTheCurrentFilesIsACopyOutsideTheWorkspaceWithAnEmptyChatThatItsAiStartsFromMain()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        await Say(workspace.Id, "remember apples");
        string before = await Git(folder, "status", "--porcelain");

        WorkspaceInfo side = await Side(workspace.Id);

        Assert.Equal(("project / fix-login", workspace.Id, "fix-login", SideSessionStart.Files, false), (side.Name, side.Side!.ParentId, side.Side.Name, side.Side.StartedFrom, side.Side.IntegrateWhenFinished));
        Assert.Equal(Path.Combine(server.Data, "sessions", workspace.Id.ToString("N"), side.Id.ToString("N")), side.Folder);
        Assert.Equal(workspace.Id, Assert.Single((await Connection.ListWorkspaces(CancellationToken.None)).Workspaces).Id);
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(Path.Combine(side.Folder, "a.txt")));
        Assert.True(File.Exists(Path.Combine(side.Folder, "uncommitted.txt")));
        Assert.False(Directory.Exists(Path.Combine(side.Folder, "ignored")));
        Assert.Equal(before, await Git(side.Folder, "status", "--porcelain"));
        Assert.Equal($"auto-remote/session/{side.Id:N}", await Git(side.Folder, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(before, await Git(folder, "status", "--porcelain"));
        Assert.Empty(await Chat(side.Id));
        FakePromptSession forked = server.Prompter.Sessions.Single(session => session.Id == side.SessionId);
        Assert.Contains(forked.Prompts, prompt => prompt.Text.Contains("remember apples"));

        await Say(side.Id, "write side.txt made on the side");

        Assert.True(File.Exists(Path.Combine(side.Folder, "side.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "side.txt")));
        string chat = await Chat(side.Id);
        Assert.Contains("write side.txt", chat);
        Assert.DoesNotContain("remember apples", chat);
        Assert.DoesNotContain("write side.txt", await Chat(workspace.Id));
        FakePromptSession working = server.Prompter.Sessions.Last(session => session.WorkspacePath == side.Folder);
        Assert.Contains(side.Folder, working.Configuration.Instructions);
        // Whether its work is done is told by how it ends, so it takes no tool to say so; every AI can say that it cannot finish.
        Assert.Equal(["finish_integration", "report_unfinished"], working.Transient.Tools.Select(tool => tool.Name).Where(name => name is "finish_integration" or "report_unfinished" or "mark_task_complete").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASessionFromTheCurrentCommitHasNoneOfTheUncommittedWork()
    {
        (WorkspaceInfo workspace, _) = await Project();

        WorkspaceInfo side = await Side(workspace.Id, start: SideSessionStart.Commit);

        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(side.Folder, "a.txt")));
        Assert.False(File.Exists(Path.Combine(side.Folder, "uncommitted.txt")));
        Assert.Empty(await Git(side.Folder, "status", "--porcelain"));
        Assert.Empty(await Connection.GetSessionChanges(side.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SessionsNeedARepositoryWithACommitAndNamesOfTheirOwn()
    {
        string plain = temp.Combine("plain");
        Directory.CreateDirectory(plain);
        WorkspaceInfo bare = await Connection.CreateWorkspace(plain, AiService.Local, null, null, CancellationToken.None);

        RemoteException none = await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(bare.Id, "x", SideSessionStart.Commit, null, false, CancellationToken.None));
        Assert.Contains("git repository", none.Message);

        await Git(plain, "init", "--quiet");
        RemoteException empty = await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(bare.Id, "x", SideSessionStart.Commit, null, false, CancellationToken.None));
        Assert.Contains("commit", empty.Message);
        Assert.Empty(await Sessions());

        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo first = await Side(workspace.Id, "one");
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(workspace.Id, "ONE", SideSessionStart.Commit, null, false, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(workspace.Id, "Main", SideSessionStart.Commit, null, false, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(workspace.Id, "  ", SideSessionStart.Commit, null, false, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(first.Id, "nested", SideSessionStart.Commit, null, false, CancellationToken.None));

        WorkspaceInfo renamed = await Connection.RenameSideSession(first.Id, "uno", CancellationToken.None);

        Assert.Equal(("project / uno", first.Folder), (renamed.Name, renamed.Folder));
        Assert.Equal($"auto-remote/session/{first.Id:N}", await Git(renamed.Folder, "symbolic-ref", "--short", "HEAD"));
    }

    [Fact]
    public async Task SetupCopiesTheListedFilesAndRunsTheScriptBeforeTheFirstTaskStarts()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        await File.WriteAllTextAsync(Path.Combine(folder, "setup.cs"), "sleep 0.3\ncat ignored/secret.env > prepared.txt\n");
        WorkspaceSettings settings = await Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        await Connection.SetWorkspaceSettings(workspace.Id, settings with { SessionFiles = "ignored\n", SessionSetupScript = "setup.cs" }, CancellationToken.None);

        WorkspaceInfo side = await Side(workspace.Id, task: "write task.txt done after setup");

        await SwarmTestServer.Until(() => Task.FromResult(File.Exists(Path.Combine(side.Folder, "task.txt"))), async () => (await Session(side.Id))!.Side!.Error ?? "no task");
        Assert.Equal("key\n", await File.ReadAllTextAsync(Path.Combine(side.Folder, "prepared.txt")));
        Assert.Equal((null, null), ((await Session(side.Id))!.Side!.Error, (await Session(side.Id))!.Side!.UnsentTask));
        Assert.False(File.Exists(Path.Combine(folder, "prepared.txt")));
    }

    [Fact]
    public async Task AFailedSetupKeepsTheFirstTaskUnsentAndSaysWhy()
    {
        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceSettings settings = await Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        await Connection.SetWorkspaceSettings(workspace.Id, settings with { SessionFiles = "missing.env" }, CancellationToken.None);

        WorkspaceInfo side = await Side(workspace.Id, task: "write task.txt never");

        Assert.Contains("missing.env", side.Side!.Error);
        Assert.Equal("write task.txt never", side.Side.UnsentTask);
        Assert.False(File.Exists(Path.Combine(side.Folder, "task.txt")));

        await Say(side.Id, "write own.txt by hand");

        Assert.Equal((null, null), ((await Session(side.Id))!.Side!.Error, (await Session(side.Id))!.Side!.UnsentTask));
    }

    [Fact]
    public async Task GitInASessionIsLimitedAndItsCommitsAreNeverPushed()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        string remote = temp.Combine("remote.git");
        await Git(temp.Path, "init", "--quiet", "--bare", remote);
        await Git(folder, "remote", "add", "origin", remote);
        await Git(folder, "push", "--quiet", "-u", "origin", "main");
        await Git(folder, "branch", "other");
        // In the repository only, which its sessions share: setting an identity through the server writes the global configuration.
        await Git(folder, "config", "user.name", "t");
        await Git(folder, "config", "user.email", "t@t");
        WorkspaceInfo side = await Side(workspace.Id);
        string branch = $"auto-remote/session/{side.Id:N}";

        foreach (Func<Task> refused in new Func<Task>[]
        {
            () => Connection.GitCheckout(side.Id, "other", false, CancellationToken.None),
            () => Connection.GitCreateBranch(side.Id, "more", CancellationToken.None),
            () => Connection.GitCreateTag(side.Id, "v1", CancellationToken.None),
            () => Connection.GitDeleteBranch(side.Id, "other", CancellationToken.None),
            () => Connection.GitSetRemote(side.Id, remote, CancellationToken.None),
            () => Connection.GitMerge(side.Id, "other", "main", null, CancellationToken.None),
            () => Connection.GitRebase(side.Id, branch, "other", null, CancellationToken.None),
            () => Connection.GitFetch(side.Id, CancellationToken.None),
            () => Connection.GitCheckout(workspace.Id, branch, false, CancellationToken.None),
            () => Connection.GitDeleteBranch(workspace.Id, branch, CancellationToken.None),
            () => Connection.GitMerge(workspace.Id, branch, "main", null, CancellationToken.None),
            () => Connection.DeleteWorkspace(side.Id, false, CancellationToken.None),
            () => Connection.RenameWorkspace(side.Id, "else", CancellationToken.None)
        })
        {
            await Assert.ThrowsAsync<RemoteException>(refused);
        }

        Assert.Equal("fix-login", (await Connection.GetGitStatus(side.Id, CancellationToken.None)).Branch);
        GitHistory history = await Connection.GetGitHistory(workspace.Id, 0, null, CancellationToken.None);
        GitRef shown = Assert.Single(history.Commits.SelectMany(commit => commit.Refs), reference => reference.Kind == GitRefKind.Session);
        Assert.Equal("fix-login", shown.Name);
        Assert.DoesNotContain(history.Commits.SelectMany(commit => commit.Refs), reference => reference.Name.Contains("auto-remote"));

        await Connection.GitCommit(side.Id, "from the side", CancellationToken.None);
        await Idle(side.Id);

        Assert.Equal("from the side", await Git(side.Folder, "log", "-1", "--format=%s"));
        Assert.Equal("first", await Git(folder, "log", "-1", "--format=%s"));
        Assert.DoesNotContain("auto-remote", await Git(remote, "for-each-ref", "--format=%(refname)"));
        // Committing changes no file, so the session still has changed nothing since it started.
        Assert.Empty(await Connection.GetSessionChanges(side.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ChangesSinceTheStartCoverWhatTheSessionCommittedAndWhatItDidNot()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await File.WriteAllTextAsync(Path.Combine(side.Folder, "committed.txt"), "c\n");
        await Git(side.Folder, "add", "committed.txt");
        await Git(side.Folder, "commit", "--quiet", "-m", "side commit");
        await File.WriteAllTextAsync(Path.Combine(side.Folder, "uncommitted.txt"), "changed on the side\n");
        File.Delete(Path.Combine(side.Folder, ".gitignore"));
        await File.WriteAllTextAsync(Path.Combine(folder, "main-only.txt"), "m\n");

        IReadOnlyList<GitChange> changes = await Connection.GetSessionChanges(side.Id, CancellationToken.None);
        GitDiff diff = await Connection.GetSessionDiff(side.Id, "uncommitted.txt", CancellationToken.None);

        Assert.Equal([(".gitignore", GitChangeStatus.Deleted), ("committed.txt", GitChangeStatus.Added), ("uncommitted.txt", GitChangeStatus.Modified)], changes.Select(change => (change.Path, change.Status)).OrderBy(change => change.Path, StringComparer.Ordinal));
        Assert.Contains("changed on the side", JsonSerializer.Serialize(diff));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.GetSessionChanges(workspace.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IntegratingGivesMainsAiTheFilesToReadAndFinishingRemovesEverythingOfTheSession()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Say(side.Id, "write side.txt made on the side");
        List<string> shared = [];
        string files = string.Empty;
        string handoff = string.Empty;
        integrate = (session, prompt) =>
        {
            shared.AddRange(session.SharedFolders);
            files = File.ReadAllText(Path.Combine(side.Folder + ".integration", "changes.txt")) + File.ReadAllText(Path.Combine(side.Folder + ".integration", "changes.diff"));
            handoff = File.ReadAllText(Path.Combine(side.Folder + ".integration", "handoff.md"));
            return CopyAndFinish(session, prompt);
        };

        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);

        await SwarmTestServer.Until(async () => await Session(side.Id) is null, async () => $"{(await Session(side.Id))?.Side?.State} {(await Session(side.Id))?.Side?.Error} {await Chat(workspace.Id)}");
        await Idle(workspace.Id);
        Assert.Equal([side.Folder, side.Folder + ".integration"], shared);
        Assert.Contains("side.txt", files);
        Assert.DoesNotContain("uncommitted.txt", files);
        Assert.NotEmpty(handoff.Trim());
        Assert.Equal("made on the side\n", await File.ReadAllTextAsync(Path.Combine(folder, "side.txt")));
        // Bringing it in is committed in main, and nothing of the session's branch is left.
        Assert.Equal(["did: Write", "first"], (await Git(folder, "log", "--format=%s", "--all")).Split('\n'));
        await NothingIsLeft(folder, side);
        Assert.Empty(server.Prompter.Sessions.Last(session => session.WorkspacePath == folder).SharedFolders);
        Assert.DoesNotContain(server.Prompter.Sessions, session => session.Id == side.SessionId);
        Assert.Equal(1, await Requests(workspace.Id));
    }

    [Fact]
    public async Task ASessionCannotBeIntegratedOrPromptedOrDeletedWhileItWorksOrIntegrates()
    {
        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);

        await Assert.ThrowsAsync<RemoteException>(() => Connection.Prompt(side.Id, "write x.txt y", CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.IntegrateSideSession(side.Id, CancellationToken.None));
        RemoteException refused = await Assert.ThrowsAsync<RemoteException>(() => Connection.DeleteSideSession(side.Id, CancellationToken.None));

        Assert.Contains("Cancel", refused.Message);
        Assert.True(Directory.Exists(side.Folder));
    }

    [Fact]
    public async Task ARequestThatIsNotConfirmedIsGivenOnceMoreAndThenTheSessionIsGivenBackAndNothingElseWaits()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Say(side.Id, "write side.txt made on the side");
        await Connection.SetIntegrateWhenFinished(side.Id, false, CancellationToken.None);

        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);

        await SwarmTestServer.Until(async () => (await Session(side.Id))?.Side is { State: SideSessionState.Ready, Error: not null }, async () => $"{await Requests(workspace.Id)} requests");
        await Idle(workspace.Id);
        await Task.Delay(600);
        Assert.Equal(2, await Requests(workspace.Id));
        SideSession given = (await Session(side.Id))!.Side!;
        Assert.Equal((false, "Needs attention: it was not brought in. Bringing it in was not confirmed."), (given.IntegrateWhenFinished, given.Error));
        Assert.True(File.Exists(Path.Combine(side.Folder, "side.txt")));
        Assert.False(Directory.Exists(side.Folder + ".integration"));
        await SwarmTestServer.Until(() => Task.FromResult(Main(folder).SharedFolders.Count == 0));
        Assert.Contains(await Activity(workspace.Id), entry => entry is { Source: ActivitySource.BringIn, NeedsAttention: true, Commit: null });
        Assert.False((await Connection.GetAgenda(workspace.Id, CancellationToken.None)).IsStopped);

        await Say(workspace.Id, "next step");

        Assert.Contains("Bringing the side session \"fix-login\" in was not confirmed, so it stays as it was.", Assert.Single(AiGot("next step")));
        await Say(side.Id, "write more.txt still usable");
        Assert.Equal(2, await Requests(workspace.Id));
    }

    [Fact]
    public async Task ABringInThatMainsAiSaysItCannotFinishGivesTheSessionBackAtOnce()
    {
        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        integrate = (session, _) => [new PromptMessageEvent { Text = Use(session, "report_unfinished", new { reason = "The two designs cannot be combined." }) }];

        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);

        await SwarmTestServer.Until(async () => (await Session(side.Id))?.Side is { State: SideSessionState.Ready, Error: not null });
        await Idle(workspace.Id);
        await Task.Delay(400);
        Assert.Equal(1, await Requests(workspace.Id));
        Assert.Contains("The two designs cannot be combined.", (await Session(side.Id))!.Side!.Error);
        await SwarmTestServer.Until(async () => (await Activity(workspace.Id)).Any(entry => entry is { Source: ActivitySource.BringIn, Outcome: ActivityOutcome.Unfinished, NeedsAttention: true, Reason: "The two designs cannot be combined." }));
    }

    [Fact]
    public async Task ASideSessionStartedWithATaskIsBroughtInByItselfOnceItsWorkEndsWellAndMainCommitsIt()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        await Git(folder, "config", "user.name", "t");
        await Git(folder, "config", "user.email", "t@t");
        List<string> pushed = [];
        integrate = CopyAndFinish;

        WorkspaceInfo made = await Connection.CreateSideSession(workspace.Id, "task", SideSessionStart.Files, "write task.txt made by the task", true, CancellationToken.None);

        await Gone(made);
        await Idle(workspace.Id);
        await SwarmTestServer.Until(async () => (await Activity(workspace.Id)).Any(entry => entry is { Source: ActivitySource.BringIn, Outcome: ActivityOutcome.Done, Commit: not null }), async () => string.Join("; ", (await Activity(workspace.Id)).Select(entry => $"{entry.Title} {entry.Outcome} {entry.Commit} {entry.CommitError}")));
        Assert.Equal("made by the task\n", await File.ReadAllTextAsync(Path.Combine(folder, "task.txt")));
        Assert.Equal(1, await Requests(workspace.Id));
        Assert.Equal("2", await Git(folder, "rev-list", "--count", "HEAD"));
        Assert.Contains("task.txt", await Git(folder, "show", "--name-only", "--format=", "HEAD"));
        ActivityEntry brought = Assert.Single(await Activity(workspace.Id), entry => entry.Source == ActivitySource.BringIn);
        Assert.Contains(brought.Files, file => file.StartsWith("task: ", StringComparison.Ordinal) && file.EndsWith("task.txt", StringComparison.Ordinal));
        Assert.StartsWith("task: ", brought.Handoff);
        Assert.Contains(brought.Notes, note => note.Contains("Brought in the side session \"task\"."));
        Assert.Contains("This work runs unattended", Assert.Single(AiGot("write task.txt")));
        Assert.Contains("This work runs unattended", Assert.Single(AiGot("Integrate the side session")));
        await NothingIsLeft(folder, made);
    }

    [Fact]
    public async Task TheCommitOfABringInCanBeTurnedOffWhileItWaitsForItsTurn()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Say(side.Id, "write side.txt made on the side");
        integrate = CopyAndFinish;
        using ManualResetEventSlim release = new();
        server.Ai = (session, prompt) =>
        {
            if (Asked(prompt.Text) == "long work")
            {
                release.Wait(TimeSpan.FromSeconds(15));
            }

            return Work(session, prompt);
        };
        await Connection.Prompt(workspace.Id, "long work", CancellationToken.None);
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        AgendaItem? waiting = null;
        await SwarmTestServer.Until(async () => (waiting = (await Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items.SingleOrDefault(item => item.Kind == AgendaKind.BringIn)) is not null);
        Assert.Equal((side.Id, "Bring in fix-login", true), (waiting!.Id, waiting.Title, waiting.AutoCommit));
        // It has a turn of its own, so it does not join the work under way.
        await Task.Delay(400);
        Assert.Equal(0, await Requests(workspace.Id));

        await Connection.SetBringInCommit(workspace.Id, side.Id, false, CancellationToken.None);

        Assert.False(Assert.Single((await Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items, item => item.Kind == AgendaKind.BringIn).AutoCommit);
        Assert.False((await Session(side.Id))!.Side!.CommitBringIn);
        release.Set();
        await Gone(side);
        await Idle(workspace.Id);
        Assert.True(File.Exists(Path.Combine(folder, "side.txt")));
        Assert.Equal("1", await Git(folder, "rev-list", "--count", "HEAD"));
    }

    [Fact]
    public async Task CancellingKeepsTheSessionTakesAwayTheAccessAndALateFinishRemovesNothing()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Say(side.Id, "write side.txt made on the side");
        (Task<string> requested, ManualResetEventSlim release) = HoldTheRequest();
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        string id = await requested;

        await Connection.CancelIntegration(side.Id, CancellationToken.None);

        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);
        await SwarmTestServer.Until(() => Task.FromResult(Main(folder).SharedFolders.Count == 0));
        Assert.False(Directory.Exists(side.Folder + ".integration"));
        string late = Use(Main(folder), "finish_integration", new { integration_id = id });
        Assert.Contains("Nothing was removed", late);
        Assert.True(File.Exists(Path.Combine(side.Folder, "side.txt")));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CancelIntegration(side.Id, CancellationToken.None));
        release.Set();
        await Idle(workspace.Id);
        await Task.Delay(400);
        // The turn that was cancelled under way asks for nothing again.
        Assert.Equal(1, await Requests(workspace.Id));

        await Say(side.Id, "write more.txt after cancelling");
        integrate = CopyAndFinish;
        await Idle(workspace.Id);
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        await Gone(side);
        await NothingIsLeft(folder, side);
        Assert.True(File.Exists(Path.Combine(folder, "more.txt")));
    }

    [Fact]
    public async Task CancellingAsTheAiFinishesLeavesTheSessionEitherWholeOrGone()
    {
        (WorkspaceInfo workspace, string folder) = await Project();

        for (int round = 0; round < 6; round++)
        {
            WorkspaceInfo side = await Side(workspace.Id, $"race-{round}");
            (Task<string> requested, ManualResetEventSlim release) = HoldTheRequest();
            await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
            string id = await requested;
            FakePromptSession main = server.Prompter.Sessions.Last(session => session.WorkspacePath == folder);
            int delay = round * 3;
            Task<string> finish = Task.Run(async () =>
            {
                await Task.Delay(delay);
                return Use(main, "finish_integration", new { integration_id = id });
            });
            Exception? cancel = await Record.ExceptionAsync(() => Connection.CancelIntegration(side.Id, CancellationToken.None));
            string said = await finish;
            release.Set();
            await Idle(workspace.Id);

            if (said.Contains("Nothing was removed"))
            {
                Assert.Null(cancel);
                Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);
                Assert.True(File.Exists(Path.Combine(side.Folder, "a.txt")));
                await Connection.DeleteSideSession(side.Id, CancellationToken.None);
            }
            else
            {
                Assert.IsType<RemoteException>(cancel);
                Assert.Null(await Session(side.Id));
            }

            await NothingIsLeft(folder, side);
        }
    }

    [Fact]
    public async Task ASessionKeptSeparateIsNotBroughtInAndSettingItToBringInOnceItsWorkEndedWellBringsItIn()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        integrate = CopyAndFinish;
        WorkspaceInfo side = await Side(workspace.Id);

        await Say(side.Id, "write side.txt made on the side");
        await Task.Delay(400);

        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);
        Assert.Equal(0, await Requests(workspace.Id));

        await Connection.SetIntegrateWhenFinished(side.Id, true, CancellationToken.None);

        await Gone(side);
        await Idle(workspace.Id);
        Assert.True(File.Exists(Path.Combine(folder, "side.txt")));
        await NothingIsLeft(folder, side);
    }

    [Fact]
    public async Task ASessionSetToBringInThatHasDoneNoWorkIsNotBroughtIn()
    {
        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);

        await Connection.SetIntegrateWhenFinished(side.Id, true, CancellationToken.None);
        await Task.Delay(500);

        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);
        Assert.Equal(0, await Requests(workspace.Id));
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("failed")]
    [InlineData("unfinished")]
    public async Task WorkThatWasStoppedOrFailedOrThatTheAiCouldNotFinishIsNotBroughtInUntilWorkEndsWell(string how)
    {
        (WorkspaceInfo workspace, _) = await Project();
        integrate = CopyAndFinish;
        WorkspaceInfo side = await Side(workspace.Id);
        await Connection.SetIntegrateWhenFinished(side.Id, true, CancellationToken.None);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        server.Ai = (session, prompt) =>
        {
            if (Asked(prompt.Text) != "go on")
            {
                return Work(session, prompt);
            }

            if (how == "unfinished")
            {
                Use(session, "report_unfinished", new { reason = "Something is missing." });
            }

            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return how == "failed" ? throw new PromptSessionException("The model broke down.") : [new PromptMessageEvent { Text = how }];
        };

        await Connection.Prompt(side.Id, "go on", CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        if (how == "stopped")
        {
            Task stop = Connection.StopWorkspace(side.Id, CancellationToken.None);
            await Task.Delay(200);
            release.Set();
            await stop;
        }
        else
        {
            release.Set();
        }

        await Idle(side.Id);
        await Task.Delay(500);
        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);
        Assert.Equal(0, await Requests(workspace.Id));

        // Given more work that ends well, it is brought in.
        await Connection.Prompt(side.Id, "carry on", CancellationToken.None);
        await Gone(side);
    }

    [Fact]
    public async Task TheWorkOfMainThatIntegratedASessionCanNoLongerBeRevertedAwayWithIt()
    {
        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        bool before = false;
        (bool Revertible, string? Refusal) after = default;
        integrate = (session, prompt) =>
        {
            before = server.Snapshots.Has(workspace.Id);
            PromptEvent[] done = [.. CopyAndFinish(session, prompt)];
            after = (server.Snapshots.Has(workspace.Id), server.Snapshots.Refusal(workspace.Id));
            return done;
        };

        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        await SwarmTestServer.Until(async () => await Session(side.Id) is null);
        await Idle(workspace.Id);

        Assert.True(before);
        Assert.False(after.Revertible);
        Assert.Contains("brought a side session in", after.Refusal);
        Assert.Null(server.Snapshots.Refusal(workspace.Id));
    }

    [Fact]
    public async Task ASessionDeletedWhileGitIsStillMakingItLeavesNothingBehind()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        // Git runs this as it makes the files of the session, which holds the making open.
        await Executable(Path.Combine(folder, ".git", "hooks", "post-checkout"), "#!/bin/sh\nsleep 2\n");
        Task<WorkspaceInfo> creating = Connection.CreateSideSession(workspace.Id, "slow", SideSessionStart.Files, "write never.txt never", false, CancellationToken.None);
        await SwarmTestServer.Until(async () => (await Sessions()).Count == 1);
        WorkspaceInfo made = (await Sessions())[0];

        await Connection.DeleteSideSession(made.Id, CancellationToken.None);

        RemoteException refused = await Assert.ThrowsAsync<RemoteException>(() => creating);
        Assert.Contains("deleted while", refused.Message);
        await Task.Delay(500);
        Assert.Empty(await Sessions());
        File.Delete(Path.Combine(folder, ".git", "hooks", "post-checkout"));
        await NothingIsLeft(folder, made);
        Assert.False(File.Exists(Path.Combine(server.Data, "chats", made.Id.ToString("N") + ".jsonl")));
        Assert.All(server.Prompter.Sessions, session => Assert.NotEqual(made.Folder, session.WorkspacePath));
    }

    [Theory]
    [InlineData("/setup.cs")]
    [InlineData("scripts\\setup.cs")]
    public async Task ASetupScriptThatFailsIsSeenToHoweverItsPathIsWritten(string path)
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(folder, "setup.cs"), "exit 1\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "scripts", "setup.cs"), "exit 1\n");
        WorkspaceSettings settings = await Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        await Connection.SetWorkspaceSettings(workspace.Id, settings with { SessionSetupScript = path }, CancellationToken.None);

        WorkspaceInfo side = await Side(workspace.Id, task: "write task.txt never");

        Assert.Contains("did not succeed", side.Side!.Error);
        Assert.Equal("write task.txt never", side.Side.UnsentTask);
        Assert.False(File.Exists(Path.Combine(side.Folder, "task.txt")));
    }

    [Fact]
    public async Task ARepositoryWithNoFilesAtAllCanStillStartASessionFromItsCurrentFiles()
    {
        string folder = temp.Combine("empty");
        Directory.CreateDirectory(folder);
        await Git(folder, "init", "--quiet", "--initial-branch=main");
        await Git(folder, "commit", "--quiet", "--allow-empty", "-m", "empty");
        WorkspaceInfo workspace = await Connection.CreateWorkspace(folder, AiService.Local, null, null, CancellationToken.None);

        WorkspaceInfo side = await Side(workspace.Id);

        Assert.True(Directory.Exists(side.Folder));
    }

    [Fact]
    public async Task AWorkspaceIsNotRenamedWhileASessionIsIntegratedNorMovedToAnotherRepository()
    {
        (WorkspaceInfo workspace, _) = await Project();
        (_, string unrelated) = await Project("unrelated");
        string spare = temp.Combine("spare");
        Directory.CreateDirectory(spare);
        await Git(spare, "init", "--quiet");
        WorkspaceInfo side = await Side(workspace.Id);

        RemoteException other = await Assert.ThrowsAsync<RemoteException>(() => Connection.MoveWorkspaceToFolder(workspace.Id, spare, CancellationToken.None));

        Assert.Contains("repository", other.Message);
        (Task<string> requested, ManualResetEventSlim release) = HoldTheRequest();
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        await requested;
        RemoteException renaming = await Assert.ThrowsAsync<RemoteException>(() => Connection.RenameWorkspace(workspace.Id, "renamed", CancellationToken.None));
        Assert.Contains("being brought in", renaming.Message);
        release.Set();
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task AnIntegrationGoesOnAfterTheServerStartsAgain()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Say(side.Id, "write side.txt made on the side");
        (Task<string> requested, ManualResetEventSlim release) = HoldTheRequest();
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        string id = await requested;

        List<string> shared = [];
        List<string> ids = [];
        // What main's AI does once it is asked again; the one already held stays as it is.
        integrate = (session, prompt) =>
        {
            shared.AddRange(session.SharedFolders);
            ids.Add(IntegrationId(prompt.Text));
            return CopyAndFinish(session, prompt);
        };

        await server.Restart("server");

        // What the AI of the server that is gone still does reaches nobody.
        release.Set();

        await Gone(side);
        await Idle(workspace.Id);
        Assert.Equal([id], ids);
        Assert.Equal([side.Folder, side.Folder + ".integration"], shared);
        Assert.True(File.Exists(Path.Combine(folder, "side.txt")));
        Assert.Equal(2, await Requests(workspace.Id));
        // It stays the same piece of work, whose AI is told to look at what is there already.
        await SwarmTestServer.Until(async () => (await Activity(workspace.Id)).Where(entry => entry.Source == ActivitySource.BringIn).Select(entry => entry.Outcome).SequenceEqual([ActivityOutcome.Done]), async () => string.Join("; ", (await Activity(workspace.Id)).Select(entry => $"{entry.Title} {entry.Outcome} {entry.Reason}")));
        string[] asked = AiGot("Integrate the side session");
        Assert.DoesNotContain("The server restarted", asked[0]);
        Assert.Contains("The server restarted while you were bringing this in. Check what is already here", asked[^1]);
        await NothingIsLeft(folder, side);
    }

    [Fact]
    public async Task DeletingASessionDiscardsItAndDeletingTheWorkspaceDeletesItsSessionsEvenWhenTheFolderIsKept()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo first = await Side(workspace.Id, "one");
        WorkspaceInfo second = await Side(workspace.Id, "two");
        await Say(first.Id, "write lost.txt discarded");

        await Connection.DeleteSideSession(first.Id, CancellationToken.None);

        Assert.False(Directory.Exists(first.Folder));
        Assert.Equal(second.Id, Assert.Single(await Sessions()).Id);
        Assert.False(File.Exists(Path.Combine(folder, "lost.txt")));
        Assert.DoesNotContain(first.Id.ToString("N"), await Git(folder, "for-each-ref", "--format=%(refname)"));

        await Connection.DeleteWorkspace(workspace.Id, false, CancellationToken.None);

        Assert.Empty(await Sessions());
        Assert.True(File.Exists(Path.Combine(folder, "a.txt")));
        await NothingIsLeft(folder, second);
        Assert.False(Directory.Exists(Path.Combine(server.Data, "sessions", workspace.Id.ToString("N"))));
    }

    [Fact]
    public async Task ARenamedWorkspaceKeepsItsSessionsWorking()
    {
        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);

        WorkspaceInfo renamed = await Connection.RenameWorkspace(workspace.Id, "renamed", CancellationToken.None);

        Assert.Equal("renamed / fix-login", (await Session(side.Id))!.Name);
        Assert.Equal($"auto-remote/session/{side.Id:N}", await Git(side.Folder, "symbolic-ref", "--short", "HEAD"));
        Assert.NotEmpty(await Git(side.Folder, "status", "--porcelain"));
        await Connection.DeleteSideSession(side.Id, CancellationToken.None);
        await NothingIsLeft(renamed.Folder, side);
    }

    [Fact]
    public async Task ASessionThatWasStillBeingMadeWhenTheServerStoppedIsCleanedUp()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        await File.WriteAllTextAsync(Path.Combine(folder, "setup.cs"), "sleep 30\n");
        WorkspaceSettings settings = await Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        await Connection.SetWorkspaceSettings(workspace.Id, settings with { SessionSetupScript = "setup.cs" }, CancellationToken.None);
        WorkspaceInfo side = await Connection.CreateSideSession(workspace.Id, "slow", SideSessionStart.Files, null, false, CancellationToken.None);
        await Assert.ThrowsAsync<RemoteException>(() => Connection.Prompt(side.Id, "write x.txt y", CancellationToken.None));
        Assert.Equal(SideSessionState.Creating, (await Session(side.Id))!.Side!.State);

        await server.Restart("server");

        Assert.Empty(await Sessions());
        await NothingIsLeft(folder, side);
    }

    [Fact]
    public async Task ASessionStillBeingMadeCanBeDeleted()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        await File.WriteAllTextAsync(Path.Combine(folder, "setup.cs"), "sleep 30\n");
        WorkspaceSettings settings = await Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        await Connection.SetWorkspaceSettings(workspace.Id, settings with { SessionSetupScript = "setup.cs" }, CancellationToken.None);
        WorkspaceInfo side = await Connection.CreateSideSession(workspace.Id, "slow", SideSessionStart.Files, "write never.txt never", false, CancellationToken.None);

        await Connection.DeleteSideSession(side.Id, CancellationToken.None);

        Assert.Empty(await Sessions());
        await NothingIsLeft(folder, side);
    }
}
