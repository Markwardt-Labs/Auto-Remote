namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class SideSessionEndToEndTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly GitService git = new();

    private SwarmTestServer server = null!;

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
        await server.DisposeAsync();
        temp.Dispose();
    }

    // "write NAME TEXT" writes a file where the session works, "complete" marks the task complete and a request to integrate is left to the test.
    private IEnumerable<PromptEvent> Work(FakePromptSession session, FakePrompt prompt)
    {
        if (prompt.Text.StartsWith("Integrate the side session", StringComparison.Ordinal))
        {
            return integrate(session, prompt);
        }

        string[] words = prompt.Text.Split(' ', 3);

        if (words[0] == "write")
        {
            File.WriteAllText(Path.Combine(session.WorkspacePath!, words[1]), words[2] + "\n");
        }
        else if (words[0] == "complete")
        {
            return [new PromptMessageEvent { Text = Use(session, "mark_task_complete", new { }) }];
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

    private async Task<WorkspaceInfo> Side(Guid main, string name = "fix-login", SideSessionStart start = SideSessionStart.Files, string? task = null)
    {
        WorkspaceInfo made = await Connection.CreateSideSession(main, name, start, task, CancellationToken.None);
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
        Assert.Contains(working.Transient.Tools, tool => tool.Name == "mark_task_complete");
        Assert.DoesNotContain(working.Transient.Tools, tool => tool.Name == "finish_integration");
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

        RemoteException none = await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(bare.Id, "x", SideSessionStart.Commit, null, CancellationToken.None));
        Assert.Contains("git repository", none.Message);

        await Git(plain, "init", "--quiet");
        RemoteException empty = await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(bare.Id, "x", SideSessionStart.Commit, null, CancellationToken.None));
        Assert.Contains("commit", empty.Message);
        Assert.Empty(await Sessions());

        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo first = await Side(workspace.Id, "one");
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(workspace.Id, "ONE", SideSessionStart.Commit, null, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(workspace.Id, "Main", SideSessionStart.Commit, null, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(workspace.Id, "  ", SideSessionStart.Commit, null, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CreateSideSession(first.Id, "nested", SideSessionStart.Commit, null, CancellationToken.None));

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
        Assert.Equal("first", await Git(folder, "log", "--format=%s", "--all"));
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
    public async Task ARequestThatIsNotFinishedIsGivenOnceMoreAndThenOnlyGoesWithMainsOtherWork()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);

        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);

        await Requested(workspace.Id, 2);
        await Task.Delay(600);
        Assert.Equal(2, await Requests(workspace.Id));
        Assert.Equal(SideSessionState.Integrating, (await Session(side.Id))!.Side!.State);

        // Main works again, on something that takes a while: the request joins it.
        using ManualResetEventSlim release = new();
        server.Ai = (session, prompt) =>
        {
            if (prompt.Text.StartsWith("long", StringComparison.Ordinal))
            {
                release.Wait(TimeSpan.FromSeconds(15));
            }

            return Work(session, prompt);
        };
        await Connection.Prompt(workspace.Id, "long work", CancellationToken.None);
        await Task.Delay(500);
        release.Set();
        await Requested(workspace.Id, 3);
        await Idle(workspace.Id);
        await Task.Delay(600);
        Assert.Equal(3, await Requests(workspace.Id));
        Assert.Equal(SideSessionState.Integrating, (await Session(side.Id))!.Side!.State);
    }

    [Fact]
    public async Task CancellingKeepsTheSessionTakesAwayTheAccessAndALateFinishRemovesNothing()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Say(side.Id, "write side.txt made on the side");
        FakePromptSession? main = null;
        string id = string.Empty;
        integrate = (session, prompt) =>
        {
            main = session;
            id = IntegrationId(prompt.Text);
            return [new PromptMessageEvent { Text = "half done" }];
        };
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        await Requested(workspace.Id, 2);
        await Idle(workspace.Id);

        await Connection.CancelIntegration(side.Id, CancellationToken.None);

        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);
        await SwarmTestServer.Until(() => Task.FromResult(Main(folder).SharedFolders.Count == 0));
        Assert.False(Directory.Exists(side.Folder + ".integration"));
        string late = Use(Main(folder), "finish_integration", new { integration_id = id });
        Assert.Contains("Nothing was removed", late);
        Assert.True(File.Exists(Path.Combine(side.Folder, "side.txt")));
        await Assert.ThrowsAsync<RemoteException>(() => Connection.CancelIntegration(side.Id, CancellationToken.None));

        await Say(side.Id, "write more.txt after cancelling");
        integrate = CopyAndFinish;
        await Idle(workspace.Id);
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        await SwarmTestServer.Until(async () => await Session(side.Id) is null);
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
            string id = string.Empty;
            integrate = (_, prompt) =>
            {
                id = IntegrationId(prompt.Text);
                return [new PromptMessageEvent { Text = "waiting" }];
            };
            await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
            await SwarmTestServer.Until(() => Task.FromResult(id.Length > 0));
            await Idle(workspace.Id);
            FakePromptSession main = server.Prompter.Sessions.Last(session => session.WorkspacePath == folder);
            int delay = round * 3;
            Task<string> finish = Task.Run(async () =>
            {
                await Task.Delay(delay);
                return Use(main, "finish_integration", new { integration_id = id });
            });
            Exception? cancel = await Record.ExceptionAsync(() => Connection.CancelIntegration(side.Id, CancellationToken.None));
            string said = await finish;
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
    public async Task ASessionSetToIntegrateWhenFinishedIsIntegratedOnceItsAiMarksTheTaskComplete()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        integrate = CopyAndFinish;
        WorkspaceInfo side = await Side(workspace.Id);

        await Say(side.Id, "write side.txt made on the side");
        await Connection.SetIntegrateWhenFinished(side.Id, true, CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);

        await Connection.Prompt(side.Id, "complete", CancellationToken.None);

        await SwarmTestServer.Until(async () => await Session(side.Id) is null, async () => $"{(await Session(side.Id))?.Side?.State} {(await Session(side.Id))?.Side?.Error}");
        await Idle(workspace.Id);
        Assert.True(File.Exists(Path.Combine(folder, "side.txt")));
        await NothingIsLeft(folder, side);
    }

    [Fact]
    public async Task MarkingTheTaskCompleteIntegratesNothingUnlessTheUserAskedForIt()
    {
        (WorkspaceInfo workspace, _) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);

        await Connection.Prompt(side.Id, "complete", CancellationToken.None);
        await SwarmTestServer.Until(async () => (await Chat(side.Id)).Contains("did not set"), () => Chat(side.Id));
        await Task.Delay(300);

        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);

        await Say(side.Id, "write more.txt which reopens the task");
        await Connection.SetIntegrateWhenFinished(side.Id, true, CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(SideSessionState.Ready, (await Session(side.Id))!.Side!.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkThatWasStoppedOrFailedAfterTheAiMarkedItsTaskCompleteIsNotIntegrated(bool stopped)
    {
        (WorkspaceInfo workspace, _) = await Project();
        integrate = CopyAndFinish;
        WorkspaceInfo side = await Side(workspace.Id);
        await Connection.SetIntegrateWhenFinished(side.Id, true, CancellationToken.None);
        using ManualResetEventSlim marked = new();
        using ManualResetEventSlim release = new();
        server.Ai = (session, prompt) =>
        {
            if (prompt.Text != "mark then go on")
            {
                return Work(session, prompt);
            }

            Use(session, "mark_task_complete", new { });
            marked.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return stopped ? [new PromptMessageEvent { Text = "stopped" }] : throw new PromptSessionException("The model broke down.");
        };

        await Connection.Prompt(side.Id, "mark then go on", CancellationToken.None);
        Assert.True(marked.Wait(TimeSpan.FromSeconds(10)));

        if (stopped)
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

        // Asked again, and ended well, it is integrated.
        await Connection.Prompt(side.Id, "complete", CancellationToken.None);
        await SwarmTestServer.Until(async () => await Session(side.Id) is null, async () => $"{(await Session(side.Id))?.Side?.State}");
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
        Assert.Contains("integrated a side session", after.Refusal);
        Assert.Null(server.Snapshots.Refusal(workspace.Id));
    }

    [Fact]
    public async Task ASessionDeletedWhileGitIsStillMakingItLeavesNothingBehind()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        // Git runs this as it makes the files of the session, which holds the making open.
        await Executable(Path.Combine(folder, ".git", "hooks", "post-checkout"), "#!/bin/sh\nsleep 2\n");
        Task<WorkspaceInfo> creating = Connection.CreateSideSession(workspace.Id, "slow", SideSessionStart.Files, "write never.txt never", CancellationToken.None);
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
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        await Idle(workspace.Id);
        RemoteException renaming = await Assert.ThrowsAsync<RemoteException>(() => Connection.RenameWorkspace(workspace.Id, "renamed", CancellationToken.None));
        Assert.Contains("being integrated", renaming.Message);
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task AnIntegrationGoesOnAfterTheServerStartsAgain()
    {
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await Side(workspace.Id);
        await Say(side.Id, "write side.txt made on the side");
        await Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        await Requested(workspace.Id, 2);
        await Idle(workspace.Id);

        await server.Restart("server");

        await Task.Delay(500);
        Assert.Equal(2, await Requests(workspace.Id));
        Assert.Equal(SideSessionState.Integrating, (await Session(side.Id))!.Side!.State);
        List<string> shared = [];
        integrate = (session, prompt) =>
        {
            shared.AddRange(session.SharedFolders);
            return CopyAndFinish(session, prompt);
        };
        server.Ai = (session, prompt) =>
        {
            if (!prompt.Text.StartsWith("long", StringComparison.Ordinal))
            {
                return Work(session, prompt);
            }

            // The request joins this work: as a message into it, or as part of its prompt when it arrives just as it starts.
            int joined = prompt.Text.IndexOf("Integrate the side session", StringComparison.Ordinal);
            Assert.True(joined >= 0 || SpinWait.SpinUntil(() => prompt.Steered.Count > 0, TimeSpan.FromSeconds(10)));
            return integrate(session, new FakePrompt { Time = prompt.Time, Text = joined >= 0 ? prompt.Text[joined..] : prompt.Steered[0] });
        };

        await Connection.Prompt(workspace.Id, "long work", CancellationToken.None);

        await SwarmTestServer.Until(async () => await Session(side.Id) is null, () => Chat(workspace.Id));
        await Idle(workspace.Id);
        Assert.Equal([side.Folder, side.Folder + ".integration"], shared);
        Assert.True(File.Exists(Path.Combine(folder, "side.txt")));
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
        WorkspaceInfo side = await Connection.CreateSideSession(workspace.Id, "slow", SideSessionStart.Files, null, CancellationToken.None);
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
        WorkspaceInfo side = await Connection.CreateSideSession(workspace.Id, "slow", SideSessionStart.Files, "write never.txt never", CancellationToken.None);

        await Connection.DeleteSideSession(side.Id, CancellationToken.None);

        Assert.Empty(await Sessions());
        await NothingIsLeft(folder, side);
    }
}
