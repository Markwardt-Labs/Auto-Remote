namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class SwarmEndToEndTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly GitService git = new();

    private SwarmTestServer coordinator = null!;

    private SwarmTestServer worker = null!;

    public async Task InitializeAsync()
    {
        coordinator = await SwarmTestServer.Start(temp.Path, "coordinator");
        worker = await SwarmTestServer.Start(temp.Path, "worker");
        worker.Ai = WorkOnFiles;
    }

    public async Task DisposeAsync()
    {
        await coordinator.DisposeAsync();
        await worker.DisposeAsync();
        temp.Dispose();
    }

    private IEnumerable<PromptEvent> WorkOnFiles(FakePromptSession session, FakePrompt prompt)
    {
        string folder = session.WorkspacePath!;
        File.WriteAllText(Path.Combine(folder, "done.txt"), $"done by the worker: {prompt.Text.Split('\n')[0]}\n");
        return [new PromptMessageEvent { Text = "Report: wrote done.txt and checked it." }];
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
        await Git(folder, "add", "-A");
        await Git(folder, "commit", "--quiet", "-m", "first");
        await File.WriteAllTextAsync(Path.Combine(folder, "uncommitted.txt"), "not yet committed\n");
        return (await coordinator.Connection.CreateWorkspace(folder, AiService.Local, null, null, CancellationToken.None), folder);
    }

    private async Task PairByNetwork()
    {
        SwarmStatus coded = await coordinator.Connection.CreatePairingCode(CancellationToken.None);
        await worker.Connection.PairWithCode($"127.0.0.1:{coordinator.Port}", coded.PairingCode!.Code.ToLowerInvariant().Replace('-', ' '), CancellationToken.None);
    }

    private async Task Permit(bool accepts = true)
    {
        SwarmPeer atCoordinator = await coordinator.Peer();
        await coordinator.Connection.SetSwarmPeer(atCoordinator with { SendWork = true }, CancellationToken.None);
        SwarmPeer atWorker = await worker.Peer();
        await worker.Connection.SetSwarmPeer(atWorker with { AcceptWork = true }, CancellationToken.None);
        await worker.Connection.SetSwarm((await worker.Status()).Server with { AcceptDelegatedWork = accepts }, CancellationToken.None);
    }

    private async Task KnowsTheWorker() => await SwarmTestServer.Until(async () => await coordinator.Peer() is { Capabilities.Count: 4, AcceptsOurWork: true });

    private SwarmTaskRequest Request(string title, string? peer = null) => new() { Title = title, Instructions = $"Please {title}.", ExpectedResult = "done.txt exists", Capability = AiCapability.Strong, PeerId = peer };

    [Fact]
    public async Task PairingByCodePairsBothWithNothingAllowedAndAWrongCodeIsRefused()
    {
        await coordinator.Connection.CreatePairingCode(CancellationToken.None);

        RemoteException refused = await Assert.ThrowsAsync<RemoteException>(() => worker.Connection.PairWithCode($"127.0.0.1:{coordinator.Port}", "0000-0000-0000-0000-0000-0000", CancellationToken.None));

        Assert.Contains("code is wrong", refused.Message);
        Assert.Empty((await coordinator.Status()).Peers);

        await PairByNetwork();

        SwarmPeer first = await coordinator.Peer();
        SwarmPeer second = await worker.Peer();
        Assert.Equal(("worker", false, false, SwarmMode.Direct, string.Empty), (first.Name, first.SendWork, first.AcceptWork, first.Mode, first.Address));
        Assert.Equal(("coordinator", false, false, SwarmMode.Direct, $"127.0.0.1:{coordinator.Port}"), (second.Name, second.SendWork, second.AcceptWork, second.Mode, second.Address));
        Assert.Equal(new SwarmPermissions(), second.Permissions);
        Assert.Equal((await worker.Status()).Server.Fingerprint, first.Fingerprint);
        Assert.Null((await coordinator.Status()).PairingCode);
        await SwarmTestServer.Until(async () => (await coordinator.Peer()).IsConnected && (await worker.Peer()).IsConnected);
    }

    [Fact]
    public async Task ATaskDelegatedDirectlyRunsOnTheWorkerComesBackAsABranchAndTheAiBringsItInWhenAsked()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, string folder) = await Project();
        List<string> told = [];
        coordinator.Ai = Bringer(told);

        await coordinator.Connection.DelegateTask(workspace.Id, Request("write the file") with { BringIn = false }, CancellationToken.None);

        SwarmTaskInfo done = await coordinator.Found(task => task.State == SwarmTaskState.Done);
        Assert.Equal("1", Environment.GetEnvironmentVariable("OPENCODE_DISABLE_PROJECT_CONFIG"));
        Assert.Equal(("write the file", "worker", true, false, false), (done.Title, done.PeerName, done.IsManual, done.BringIn, done.IsBringingIn));
        Assert.True(done.CanBringIn);
        Assert.StartsWith("swarm/", done.Branch);
        Assert.Contains("wrote done.txt", done.Summary);
        (string workerFolder, string asked) = Assert.Single(worker.Asked);
        Assert.Contains("Please write the file.", asked);
        Assert.Contains("done.txt exists", asked);
        Assert.Equal("done.txt", await Git(folder, "diff", "--name-only", $"{done.Branch}^", done.Branch!));
        Assert.Contains("uncommitted.txt", await Git(folder, "ls-tree", "--name-only", done.Branch!));
        Assert.Equal("main", await Git(folder, "symbolic-ref", "--short", "HEAD"));
        Assert.False(File.Exists(Path.Combine(folder, "done.txt")));
        SwarmTaskDetail detail = await coordinator.Connection.GetSwarmTask(done.Id, CancellationToken.None);
        Assert.Contains(detail.Messages, message => message.Role == MessageRole.Assistant && message.Text.Contains("wrote done.txt"));

        SwarmStatus asking = await coordinator.Connection.BringInSwarmTask(done.Id, CancellationToken.None);

        Assert.Contains("turn of its own", asking.Report);
        await SwarmTestServer.Until(async () => told.Count == 1 && (await coordinator.Status()).Tasks.Count == 0, () => Task.FromResult(string.Join(" | ", told)));
        Assert.StartsWith("done by the worker", await File.ReadAllTextAsync(Path.Combine(folder, "done.txt")));
        Assert.Contains("was removed", Assert.Single(told));
        Assert.StartsWith("[Auto Remote] Bring in the result", Assert.Single(Prompts(coordinator, "integration ")));
        await SwarmTestServer.Until(async () => await Git(folder, "branch", "--format=%(refname:short)", "--list", "swarm/*") == string.Empty);
        Assert.Equal(string.Empty, await Git(folder, "for-each-ref", "refs/auto-remote/tasks", "refs/auto-remote/results"));
        await SwarmTestServer.Until(async () => await Activity(workspace.Id, entry => entry is { Source: ActivitySource.BringIn, Outcome: ActivityOutcome.Done }) is { } entry && entry.Files.Any(file => file.Contains("done.txt")) && entry.Handoff?.Contains("wrote done.txt") == true);
        await SwarmTestServer.Until(async () => !Directory.Exists(workerFolder) && (await worker.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Count == 0);
        Assert.Empty(Directory.Exists(Path.Combine(coordinator.Data, "swarm", "results")) ? Directory.GetDirectories(Path.Combine(coordinator.Data, "swarm", "results")) : []);
    }

    [Fact]
    public async Task ASideSessionIsNotIntegratedWhileTasksItDelegatedAreUnderWayAndTheirRecordsGoWithIt()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, string folder) = await Project();
        WorkspaceInfo side = await coordinator.Connection.CreateSideSession(workspace.Id, "delegating", SideSessionStart.Files, null, false, CancellationToken.None);
        await SwarmTestServer.Until(async () => (await coordinator.Connection.ListWorkspaces(CancellationToken.None)).Sessions.Single().Side!.State == SideSessionState.Ready);
        worker.Hangs = true;
        await coordinator.Connection.DelegateTask(side.Id, Request("take long"), CancellationToken.None);
        SwarmTaskInfo task = await coordinator.Found(found => found.State == SwarmTaskState.Running);

        RemoteException refused = await Assert.ThrowsAsync<RemoteException>(() => coordinator.Connection.IntegrateSideSession(side.Id, CancellationToken.None));

        Assert.Contains("delegated", refused.Message);
        await coordinator.Connection.CancelSwarmTask(task.Id, CancellationToken.None);
        await coordinator.Found(found => found.State == SwarmTaskState.Cancelled);
        await coordinator.Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        string id = string.Empty;
        await SwarmTestServer.Until(() =>
        {
            lock (coordinator.Asked)
            {
                id = coordinator.Asked.Select(asked => Regex.Match(asked.Text, "integration ([0-9a-f]{12})").Groups[1].Value).FirstOrDefault(found => found.Length > 0) ?? string.Empty;
            }

            return System.Threading.Tasks.Task.FromResult(id.Length > 0);
        });
        await SwarmTestServer.Until(async () => await coordinator.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None) == WorkspaceState.Ready);

        Use(coordinator.Prompter.Sessions.Last(session => session.WorkspacePath == folder), "finish_integration", new { integration_id = id });

        Assert.Empty((await coordinator.Connection.ListWorkspaces(CancellationToken.None)).Sessions);
        Assert.Empty((await coordinator.Status()).Tasks);
    }

    private string Use(FakePromptSession session, string tool, object arguments)
    {
        PromptTool found = Assert.Single(session.Transient.Tools, candidate => candidate.Name == tool);
        return Task.Run(() => found.Handler(JsonSerializer.SerializeToElement(arguments), CancellationToken.None)).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task TheAiDelegatesDependentTasksEndsItsTurnAndGetsTheirResultsInATurnOfItsOwnToBringIn()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, string folder) = await Project();
        List<string> toolResults = [];
        List<string> brought = [];
        List<bool> startedWithEarlierWork = [];
        List<string> given = [];
        worker.Ai = (session, prompt) =>
        {
            string title = prompt.Text.Split('\n')[0].TrimStart('#', ' ');
            string own = session.WorkspacePath!;

            lock (startedWithEarlierWork)
            {
                startedWithEarlierWork.Add(File.Exists(Path.Combine(own, "first.txt")));
            }

            // Combining what it builds on is the work of the AI.
            foreach (System.Text.RegularExpressions.Match match in Regex.Matches(prompt.Text, "Folder: (.+)"))
            {
                string results = match.Groups[1].Value.Trim();
                given.AddRange(session.SharedFolders.Where(shared => results.StartsWith(shared, StringComparison.Ordinal)));
                Assert.False(results.StartsWith(own, StringComparison.Ordinal));
                Assert.Contains("first.txt", File.ReadAllText(Path.Combine(results, "changes.diff")));
                Assert.Contains("Report for first.", File.ReadAllText(Path.Combine(results, "report.md")));
                Copy(results, own);
            }

            File.WriteAllText(Path.Combine(own, $"{title}.txt"), title);
            return [new PromptMessageEvent { Text = $"Report for {title}." }];
        };
        Func<FakePromptSession, FakePrompt, IEnumerable<PromptEvent>> bring = Bringer(brought);
        coordinator.Ai = (session, prompt) =>
        {
            if (prompt.Text.Contains("split this up"))
            {
                toolResults.Add(Use(session, "list_workers", new { }));
                toolResults.Add(Use(session, "delegate_tasks", new { tasks = new object[] { new { title = "first", instructions = "Write first.txt.", capability = "strong" }, new { title = "second", instructions = "Write second.txt.", worker = "worker", afterTasksInThisCall = new[] { 1 } } } }));
                return [new PromptMessageEvent { Text = "Delegated; ending my turn." }];
            }

            if (prompt.Text.StartsWith("[Auto Remote] Your delegated tasks have ended", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(session.Transient.Tools, tool => tool.Name == "apply_task");
                toolResults.Add(Use(session, "review_task", new { taskId = Regex.Match(prompt.Text, "Task ([0-9a-f]{32})").Groups[1].Value }));
                return bring(session, prompt);
            }

            return [new PromptMessageEvent { Text = $"tools: {string.Join(",", session.Transient.Tools.Select(tool => tool.Name))}" }];
        };
        await coordinator.Connection.Prompt(workspace.Id, "which tools", CancellationToken.None);
        await SwarmTestServer.Until(async () => (await Chat(workspace.Id)).Any(message => message.Role == MessageRole.Assistant));
        // No delegation tools; every workspace has the ones that confirm what it brought in and say what it could not finish.
        Assert.Equal("tools: finish_integration,report_unfinished", (await Chat(workspace.Id)).Last().Text);
        WorkspaceSettings settings = await coordinator.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        Assert.False(settings.IsDelegationAllowed);
        Assert.True((await coordinator.Connection.SetWorkspaceSettings(workspace.Id, settings with { IsDelegationAllowed = true }, CancellationToken.None)).IsDelegationAllowed);
        await SwarmTestServer.Until(async () => (await coordinator.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None)) == WorkspaceState.Ready);

        await coordinator.Connection.Prompt(workspace.Id, "split this up", CancellationToken.None);

        await SwarmTestServer.Until(async () => brought.Count == 2 && (await coordinator.Status()).Tasks.Count == 0, async () => string.Join(" | ", toolResults) + string.Join(" | ", brought) + string.Join(" | ", (await coordinator.Status()).Tasks.Select(task => $"{task.Title} {task.State} {task.Error}")));
        Assert.Contains("worker (id ", toolResults[0]);
        Assert.Contains("Strong (large)", toolResults[0]);
        Assert.Contains("run shell commands", toolResults[0]);
        Assert.Contains("end your turn", toolResults[1]);
        Assert.Contains("first.txt", toolResults[2]);
        Assert.Equal([false, false], startedWithEarlierWork);
        Assert.NotEmpty(given);
        string report = Assert.Single(Prompts(coordinator, "Your delegated tasks have ended"));
        Assert.Equal(2, Regex.Matches(report, @"\(integration [0-9a-f]{12}\)").Count);
        Assert.Contains("should already include theirs: \"first\" (integration ", report);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(folder, "first.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(folder, "second.txt")));
        await SwarmTestServer.Until(async () => await Activity(workspace.Id, entry => entry is { Source: ActivitySource.DelegatedResults, Outcome: ActivityOutcome.Done }) is not null);
        Assert.All(worker.Asked, asked => Assert.DoesNotContain("split this up", asked.Text));
        // Nothing is told or asked again.
        await Task.Delay(500);
        Assert.Single(Prompts(coordinator, "Your delegated tasks have ended"));
    }

    private async Task<IReadOnlyList<ChatMessage>> Chat(Guid workspace) => (await coordinator.Connection.GetHistory(workspace, null, 200, 100_000, CancellationToken.None)).Messages;

    [Fact]
    public async Task WhileATaskRunsTheWorkersWorkspaceTakesNothingElseTheChatFollowsLiveAndCancellingStopsIt()
    {
        worker.Hangs = true;
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, _) = await Project();

        await coordinator.Connection.DelegateTask(workspace.Id, Request("hang on"), CancellationToken.None);

        SwarmTaskInfo running = await coordinator.Found(task => task.State == SwarmTaskState.Running);
        await SwarmTestServer.Until(async () => (await coordinator.Connection.GetSwarmTask(running.Id, CancellationToken.None)).Messages.Any(message => message.Text.Contains("Please hang on.")));
        WorkspaceCatalog catalog = await worker.Connection.ListWorkspaces(CancellationToken.None);
        WorkspaceDelegation delegation = Assert.Single(catalog.Delegations);
        Assert.Equal(("coordinator", "hang on"), (delegation.From, delegation.Title));
        Guid delegated = delegation.WorkspaceId;
        WorkspaceSettings settings = await worker.Connection.GetWorkspaceSettings(delegated, CancellationToken.None);
        Assert.Equal((AiCapability.Strong, false, false, false, false), (settings.Capability, settings.IsSandboxed, settings.IsWebAllowed, settings.IsImageGenerationAllowed, settings.IsDelegationAllowed));
        await Assert.ThrowsAsync<RemoteException>(() => worker.Connection.Prompt(delegated, "do something else", CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => worker.Connection.SetWorkspaceSettings(delegated, settings with { IsSandboxed = true }, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => worker.Connection.GitCommit(delegated, "x", CancellationToken.None));
        Assert.False((await worker.Connection.GetGitStatus(delegated, CancellationToken.None)).IsRepo);

        await coordinator.Connection.CancelSwarmTask(running.Id, CancellationToken.None);

        await coordinator.Found(task => task.State == SwarmTaskState.Cancelled);
        await worker.Found(task => task.State == SwarmTaskState.Cancelled);
        await SwarmTestServer.Until(async () => (await worker.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Count == 0);
    }

    [Fact]
    public async Task TheOwnerOfAWorkerStopsATaskAndTheSenderIsToldItWasCancelled()
    {
        worker.Hangs = true;
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, _) = await Project();
        await coordinator.Connection.DelegateTask(workspace.Id, Request("long job"), CancellationToken.None);
        await coordinator.Found(task => task.State == SwarmTaskState.Running);
        Guid delegated = (await worker.Connection.ListWorkspaces(CancellationToken.None)).Delegations.Single().WorkspaceId;

        await worker.Connection.StopWorkspace(delegated, CancellationToken.None);

        SwarmTaskInfo ended = await coordinator.Found(task => task.State == SwarmTaskState.Cancelled);
        Assert.Contains("owner of the worker", ended.Error);
    }

    [Fact]
    public async Task ANetworkedTaskWaitsOnTheSenderUntilAWorkerAcceptsItAndAnyWorkerClaimsIt()
    {
        await PairByNetwork();
        await Permit(false);
        await SwarmTestServer.Until(async () => (await coordinator.Peer()).Capabilities.Count == 4);
        (WorkspaceInfo workspace, _) = await Project();

        await coordinator.Connection.DelegateTask(workspace.Id, Request("later"), CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(SwarmTaskState.Pending, (await coordinator.Status()).Tasks.Single().State);
        Assert.Empty(worker.Asked);

        await worker.Connection.SetSwarm((await worker.Status()).Server with { AcceptDelegatedWork = true }, CancellationToken.None);

        await coordinator.Found(task => task.State == SwarmTaskState.Done);
    }

    private void Copy(string results, string into)
    {
        foreach (string line in File.ReadAllLines(Path.Combine(results, "changes.txt")))
        {
            string path = line[(line.LastIndexOf('\t') + 1)..];
            File.Copy(Path.Combine(results, "files", path), Path.Combine(into, path), true);
        }
    }

    // An AI that brings in every result a request names, by copying the changed files, and confirms each.
    private Func<FakePromptSession, FakePrompt, IEnumerable<PromptEvent>> Bringer(List<string> confirmed) => (session, prompt) =>
    {
        foreach (System.Text.RegularExpressions.Match match in Regex.Matches(prompt.Text, @"\(integration ([0-9a-f]{12})\)\n\nTask [0-9a-f]{32}[^\n]*?Its folder is (.+?): files holds"))
        {
            string results = match.Groups[2].Value;
            Assert.Contains(session.SharedFolders, shared => results.StartsWith(shared, StringComparison.Ordinal));
            Copy(results, session.WorkspacePath!);
            string answer = Use(session, "finish_integration", new { integration_id = match.Groups[1].Value });

            lock (confirmed)
            {
                confirmed.Add(answer);
            }
        }

        return [new PromptMessageEvent { Text = "Brought in." }];
    };

    private IReadOnlyList<string> Prompts(SwarmTestServer server, string containing)
    {
        lock (server.Asked)
        {
            return [.. server.Asked.Select(asked => asked.Text).Where(text => text.Contains(containing, StringComparison.Ordinal) && !text.StartsWith("Write a", StringComparison.Ordinal))];
        }
    }

    private async Task<ActivityEntry?> Activity(Guid workspace, Func<ActivityEntry, bool> which) => (await coordinator.Connection.GetActivity(workspace, null, 50, CancellationToken.None)).Entries.FirstOrDefault(which);

    private async Task<(WorkspaceInfo Workspace, string Folder)> Ready()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        return await Project();
    }

    [Fact]
    public async Task AResultThatIsNotConfirmedTwiceStaysWithItsTaskNeedsAttentionAndTheNextTurnIsTold()
    {
        (WorkspaceInfo workspace, string folder) = await Ready();

        await coordinator.Connection.DelegateTask(workspace.Id, Request("never confirmed"), CancellationToken.None);

        SwarmTaskInfo left = await coordinator.Found(task => task.Error?.Contains("Needs attention") == true);
        Assert.Equal((SwarmTaskState.Done, false, false), (left.State, left.BringIn, left.IsBringingIn));
        Assert.True(left.CanBringIn);
        await Task.Delay(500);
        Assert.Equal(2, Prompts(coordinator, "(integration ").Count);
        Assert.NotNull(await Activity(workspace.Id, entry => entry is { Source: ActivitySource.BringIn, Outcome: ActivityOutcome.Unfinished, NeedsAttention: true }));
        Assert.Contains("done.txt", await Git(folder, "diff", "--name-only", $"{left.Branch}^", left.Branch!));
        Assert.Empty((await coordinator.Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items);

        await coordinator.Connection.Prompt(workspace.Id, "what happened", CancellationToken.None);

        await SwarmTestServer.Until(() => Task.FromResult(Prompts(coordinator, "what happened").Any(text => text.Contains("was not confirmed"))));
    }

    [Fact]
    public async Task AResultWaitsForABusyWorkspaceShowsInItsAgendaAndCanBeKeptApartBeforeItsTurn()
    {
        (WorkspaceInfo workspace, _) = await Ready();
        coordinator.Hangs = true;
        string other = temp.Combine("busy-too");
        Directory.CreateDirectory(other);
        WorkspaceInfo blocker = await coordinator.Connection.CreateWorkspace(other, AiService.Local, null, null, CancellationToken.None);
        await coordinator.Connection.Prompt(blocker.Id, "hold the local AI", CancellationToken.None);
        coordinator.Hangs = false;

        await coordinator.Connection.DelegateTask(workspace.Id, Request("wait for it"), CancellationToken.None);
        SwarmTaskInfo done = await coordinator.Found(task => task.State == SwarmTaskState.Done);

        Assert.True(done.IsBringingIn);
        // The turn waits for the local AI, which another workspace holds.
        await SwarmTestServer.Until(async () => (await coordinator.Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items.Any(item => item.Kind == AgendaKind.LocalAi));

        await coordinator.Connection.SetSwarmTaskBringIn(done.Id, false, CancellationToken.None);

        SwarmTaskInfo kept = await coordinator.Found(task => task is { BringIn: false, IsBringingIn: false });
        Assert.Null(kept.Error);
        await SwarmTestServer.Until(async () => (await coordinator.Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items.Count == 0);
        await coordinator.Connection.StopWorkspace(blocker.Id, CancellationToken.None);
        await Task.Delay(500);
        Assert.Empty(Prompts(coordinator, "(integration "));
    }

    [Fact]
    public async Task AResultOfAStoppedWorkspaceIsUpNextWithACommitSettingAndComesInOnceItIsResumed()
    {
        (WorkspaceInfo workspace, string folder) = await Ready();
        coordinator.Ai = Bringer([]);
        coordinator.FollowUps.Pause(workspace.Id);

        await coordinator.Connection.DelegateTask(workspace.Id, Request("held back"), CancellationToken.None);

        SwarmTaskInfo done = await coordinator.Found(task => task.State == SwarmTaskState.Done);
        AgendaItem item = Assert.Single((await coordinator.Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items);
        Assert.Equal((AgendaKind.BringIn, (Guid?)done.Id, "Bring in held back", (bool?)true), (item.Kind, item.Id, item.Title, item.AutoCommit));

        await coordinator.Connection.SetBringInCommit(workspace.Id, done.Id, false, CancellationToken.None);

        Assert.False(Assert.Single((await coordinator.Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items).AutoCommit);
        await Task.Delay(300);
        Assert.Empty(Prompts(coordinator, "(integration "));

        await coordinator.Connection.ResumeAutomation(workspace.Id, CancellationToken.None);

        await SwarmTestServer.Until(async () => (await coordinator.Status()).Tasks.Count == 0);
        Assert.True(File.Exists(Path.Combine(folder, "done.txt")));
    }

    [Fact]
    public async Task AFinishedTaskGoesOnInASideSessionFromItsFilesWithItsReportAndMainStaysAsItWas()
    {
        (WorkspaceInfo workspace, string folder) = await Ready();
        await coordinator.Connection.DelegateTask(workspace.Id, Request("go on here") with { BringIn = false }, CancellationToken.None);
        SwarmTaskInfo done = await coordinator.Found(task => task.State == SwarmTaskState.Done);
        string head = await Git(folder, "rev-parse", "HEAD");

        WorkspaceInfo made = await coordinator.Connection.ContinueSwarmTask(done.Id, null, CancellationToken.None);

        await SwarmTestServer.Until(async () => (await coordinator.Connection.ListWorkspaces(CancellationToken.None)).Sessions.Single().Side!.State == SideSessionState.Ready);
        WorkspaceInfo session = (await coordinator.Connection.ListWorkspaces(CancellationToken.None)).Sessions.Single();
        Assert.Equal((made.Id, "go on here", false, workspace.Id), (session.Id, session.Side!.Name, session.Side.IntegrateWhenFinished, session.Side.ParentId));
        Assert.StartsWith("done by the worker", await File.ReadAllTextAsync(Path.Combine(session.Folder, "done.txt")));
        Assert.True(File.Exists(Path.Combine(session.Folder, "uncommitted.txt")));
        Assert.Equal(head, await Git(session.Folder, "rev-parse", "HEAD"));
        Assert.Equal("done.txt", Assert.Single(await coordinator.Connection.GetSessionChanges(session.Id, CancellationToken.None)).Path);
        Assert.Contains("wrote done.txt", Assert.Single(await Chat(session.Id)).Text);
        Assert.False(File.Exists(Path.Combine(folder, "done.txt")));
        Assert.Empty((await coordinator.Status()).Tasks);
    }

    [Fact]
    public async Task WorkTheWorkersAiCannotFinishFailsWithItsReasonAndStillSendsWhatItChanged()
    {
        (WorkspaceInfo workspace, string folder) = await Ready();
        worker.Ai = (session, prompt) =>
        {
            File.WriteAllText(Path.Combine(session.WorkspacePath!, "half.txt"), "half\n");
            Use(session, "report_unfinished", new { reason = "there is no key" });
            return [new PromptMessageEvent { Text = "I wrote half of it." }];
        };

        await coordinator.Connection.DelegateTask(workspace.Id, Request("half done"), CancellationToken.None);

        SwarmTaskInfo failed = await coordinator.Found(task => task.State is SwarmTaskState.Failed or SwarmTaskState.Done);
        Assert.Equal((SwarmTaskState.Failed, "The AI could not finish it: there is no key"), (failed.State, failed.Error));
        Assert.Equal("half.txt", await Git(folder, "diff", "--name-only", $"{failed.Branch}^", failed.Branch!));
        Assert.Contains("wrote half", failed.Summary);
        Assert.False(failed.CanBringIn);
        await Task.Delay(500);
        Assert.Single(worker.Asked);
        Assert.Empty(Prompts(coordinator, "(integration "));
    }

    [Fact]
    public async Task ATurnWithDelegatedResultsThatARestartInterruptsGoesOnAndCanStillConfirmThem()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        coordinator.Hangs = true;
        (WorkspaceInfo workspace, string folder) = await Project();
        coordinator.Hangs = false;
        await coordinator.Coordinator.Delegate(workspace.Id, [new SwarmPlannedTask(Request("survive"), [])], false, CancellationToken.None);
        // A session that hangs never asks for a reply, so what it was sent is looked up on it.
        await SwarmTestServer.Until(() => Task.FromResult(coordinator.Prompter.Sessions.SelectMany(session => session.Prompts.ToList()).Count(prompt => prompt.Text.Contains("(integration ")) == 1), async () => string.Join(" | ", (await coordinator.Status()).Tasks.Select(task => $"{task.Title} {task.State} {task.Error}")));
        List<string> confirmed = [];
        coordinator.Ai = Bringer(confirmed);

        await coordinator.Restart("coordinator");

        await SwarmTestServer.Until(async () => confirmed.Count == 1 && (await coordinator.Status()).Tasks.Count == 0, async () => string.Join(" | ", Prompts(coordinator, "")) + string.Join(" | ", (await coordinator.Status()).Tasks.Select(task => $"{task.Title} {task.State} {task.Error} {task.IsBringingIn}")));
        Assert.True(File.Exists(Path.Combine(folder, "done.txt")));
        Assert.Single(confirmed);
        Assert.Contains("The server restarted", Assert.Single(Prompts(coordinator, "(integration ")));
    }

    [Fact]
    public async Task ATurnTheAiCouldNotHaveIsGivenAgainAndDoesNotCountAgainstTheResult()
    {
        (WorkspaceInfo workspace, string folder) = await Ready();
        List<string> confirmed = [];
        Func<FakePromptSession, FakePrompt, IEnumerable<PromptEvent>> bring = Bringer(confirmed);
        int asked = 0;
        coordinator.Ai = (session, prompt) => !prompt.Text.Contains("(integration ") ? [new PromptMessageEvent { Text = "ok" }] : ++asked <= 3 ? [new PromptFailureEvent { Message = "network is unreachable" }] : bring(session, prompt);
        await coordinator.Coordinator.Delegate(workspace.Id, [new SwarmPlannedTask(Request("patiently"), [])], false, CancellationToken.None);

        await SwarmTestServer.Until(async () => confirmed.Count == 1 && (await coordinator.Status()).Tasks.Count == 0, async () => $"{asked}: " + string.Join(" | ", (await coordinator.Status()).Tasks.Select(task => $"{task.Title} {task.State} {task.Error} {task.BringIn}")));

        Assert.Equal(4, asked);
        Assert.True(File.Exists(Path.Combine(folder, "done.txt")));
        // The news that the job ended is given until the AI had it.
        Assert.All(Prompts(coordinator, "(integration "), text => Assert.StartsWith("[Auto Remote] Your delegated tasks have ended", text));
    }

    [Fact]
    public async Task ATaskDiscardedWhileItsResultWaitsLeavesNoCopyBehind()
    {
        (WorkspaceInfo workspace, _) = await Ready();
        coordinator.FollowUps.Pause(workspace.Id);
        await coordinator.Connection.DelegateTask(workspace.Id, Request("thrown away"), CancellationToken.None);
        SwarmTaskInfo done = await coordinator.Found(task => task.State == SwarmTaskState.Done);

        await coordinator.Connection.DiscardSwarmTask(done.Id, CancellationToken.None);

        Assert.Empty((await coordinator.Status()).Tasks);
        Assert.Empty((await coordinator.Connection.GetAgenda(workspace.Id, CancellationToken.None)).Items);
        Assert.False(Directory.Exists(Path.Combine(coordinator.Data, "swarm", "results", done.Id.ToString("N"))));
    }

    private async Task Eventually(Func<Task<bool>> condition)
    {
        for (int attempt = 0; attempt < 400 && !await condition(); attempt++)
        {
            await Task.Delay(25);
        }

        Assert.True(await condition());
    }

    [Fact]
    public async Task InTheGitRemoteModeTheCodeTravelsThroughTheProjectsOriginAndItsBranchesAreRemovedAfterwards()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        await coordinator.Connection.SetSwarmPeer(await coordinator.Peer() with { Mode = SwarmMode.GitRemote }, CancellationToken.None);
        await worker.Connection.SetSwarmPeer(await worker.Peer() with { Mode = SwarmMode.GitRemote }, CancellationToken.None);
        (WorkspaceInfo workspace, string folder) = await Project();
        string origin = temp.Combine("origin.git");
        Directory.CreateDirectory(origin);
        await Git(origin, "init", "--quiet", "--bare");
        await Git(folder, "remote", "add", "origin", origin);
        await KnowsTheWorker();

        coordinator.Ai = Bringer([]);

        await coordinator.Connection.DelegateTask(workspace.Id, Request("use the remote") with { BringIn = false }, CancellationToken.None);

        SwarmTaskInfo done = await coordinator.Found(task => task.State is SwarmTaskState.Done or SwarmTaskState.Failed);
        Assert.Equal((SwarmTaskState.Done, null), (done.State, done.Error));
        Assert.Contains("swarm/results/", await Git(origin, "for-each-ref", "--format=%(refname)"));

        coordinator.FollowUps.Pause(workspace.Id);
        await coordinator.Connection.DelegateTask(workspace.Id, Request("build on it") with { BringIn = false, DependsOn = [done.Id] }, CancellationToken.None);
        SwarmTaskInfo second = await coordinator.Found(task => task.Title == "build on it" && task.State is SwarmTaskState.Done or SwarmTaskState.Failed);
        Assert.Equal((SwarmTaskState.Done, null), (second.State, second.Error));
        Assert.Contains($"swarm/dependencies/{second.Id:N}/{done.Id:N}", await Git(origin, "for-each-ref", "--format=%(refname)"));
        Assert.Contains("Results this task builds on", worker.Asked[^1].Text);
        await coordinator.Connection.DiscardSwarmTask(second.Id, CancellationToken.None);
        coordinator.FollowUps.Resume(workspace.Id);

        await coordinator.Connection.BringInSwarmTask(done.Id, CancellationToken.None);

        await Eventually(async () => await Git(origin, "for-each-ref", "refs/heads/swarm") == string.Empty);
        Assert.True(File.Exists(Path.Combine(folder, "done.txt")));

        // No bundle ever reached this worker, so it never made the folder for one.
        await Eventually(async () => (await worker.Status()).Tasks.All(task => task.CanDiscard));
        await worker.Connection.RevokeSwarmPeer((await worker.Peer()).Id, CancellationToken.None);

        Assert.Empty((await worker.Status()).Peers);
    }

    [Fact]
    public async Task InTheGitRemoteModeAProjectWithoutAnOriginFailsClearly()
    {
        await PairByNetwork();
        await Permit();
        await coordinator.Connection.SetSwarmPeer(await coordinator.Peer() with { Mode = SwarmMode.GitRemote }, CancellationToken.None);
        await KnowsTheWorker();
        (WorkspaceInfo workspace, _) = await Project();

        await coordinator.Connection.DelegateTask(workspace.Id, Request("no origin"), CancellationToken.None);

        SwarmTaskInfo failed = await coordinator.Found(task => task.State == SwarmTaskState.Failed);
        Assert.Contains("no origin remote", failed.Error);
    }

    [Fact]
    public async Task AWorkerThatHasNotChosenTheGitRemoteModeForTheSenderNeverUsesARemoteItNames()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        await coordinator.Connection.SetSwarmPeer(await coordinator.Peer() with { Mode = SwarmMode.GitRemote }, CancellationToken.None);
        await KnowsTheWorker();
        (WorkspaceInfo workspace, string folder) = await Project();
        string origin = temp.Combine("unchosen.git");
        Directory.CreateDirectory(origin);
        await Git(origin, "init", "--quiet", "--bare");
        await Git(folder, "remote", "add", "origin", origin);

        await coordinator.Connection.DelegateTask(workspace.Id, Request("use my remote"), CancellationToken.None);

        SwarmTaskInfo failed = await coordinator.Found(task => task.State == SwarmTaskState.Failed);
        Assert.Contains("delivery mode for you is Git remote", failed.Error);
        Assert.Empty(worker.Asked);
        Assert.DoesNotContain("swarm/results", await Git(origin, "for-each-ref", "--format=%(refname)"));
    }

    [Fact]
    public async Task AYieldingWorkerTakesNoDelegatedWorkWhileItsOwnersWorkRunsAndTakesItOnceItEnded()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, _) = await Project();
        string busy = temp.Combine("busy");
        Directory.CreateDirectory(busy);
        worker.Hangs = true;
        WorkspaceInfo own = await worker.Connection.CreateWorkspace(busy, AiService.Local, null, null, CancellationToken.None);
        await worker.Connection.Prompt(own.Id, "my own long work", CancellationToken.None);
        worker.Hangs = false;
        await SwarmTestServer.Until(async () => (await coordinator.Peer()).FreeSlots == 0);

        await coordinator.Connection.DelegateTask(workspace.Id, Request("wait your turn"), CancellationToken.None);
        await Task.Delay(500);

        Assert.Equal(SwarmTaskState.Pending, (await coordinator.Status()).Tasks.Single().State);
        Assert.DoesNotContain(worker.Asked, asked => asked.Text.Contains("wait your turn"));

        await worker.Connection.StopWorkspace(own.Id, CancellationToken.None);

        await coordinator.Found(task => task.State == SwarmTaskState.Done);
        Assert.Single(worker.Asked, asked => asked.Text.Contains("Please wait your turn."));
    }

    [Fact]
    public async Task AirGappedServersPairAndDelegateEntirelyByDrive()
    {
        string drive = temp.Combine("drive");
        Directory.CreateDirectory(drive);

        foreach (SwarmTestServer server in new[] { coordinator, worker })
        {
            await server.Connection.SetSwarm((await server.Status()).Server with { DriveFolder = drive }, CancellationToken.None);
        }

        await coordinator.Connection.WriteDriveInvitation(CancellationToken.None);
        SwarmDrivePairing invitation = Assert.Single((await worker.Status()).DrivePairings);
        Assert.Equal((SwarmPairingKind.Invitation, "coordinator", (await coordinator.Status()).Server.Fingerprint), (invitation.Kind, invitation.Name, invitation.Fingerprint));
        await worker.Connection.ConfirmDrivePairing(invitation.Id, CancellationToken.None);
        SwarmDrivePairing acceptance = Assert.Single((await coordinator.Status()).DrivePairings);
        Assert.Equal((SwarmPairingKind.Acceptance, "worker"), (acceptance.Kind, acceptance.Name));
        await coordinator.Connection.ConfirmDrivePairing(acceptance.Id, CancellationToken.None);

        Assert.Equal(SwarmMode.AirGapped, (await coordinator.Peer()).Mode);
        await Permit();
        await worker.Connection.SyncDrive(CancellationToken.None);
        await coordinator.Connection.SyncDrive(CancellationToken.None);
        await KnowsTheWorker();
        (WorkspaceInfo workspace, string folder) = await Project();
        await Assert.ThrowsAsync<RemoteException>(() => coordinator.Connection.DelegateTask(workspace.Id, Request("anyone"), CancellationToken.None));

        coordinator.Ai = Bringer([]);

        await coordinator.Connection.DelegateTask(workspace.Id, Request("by drive", (await coordinator.Peer()).Id), CancellationToken.None);

        Assert.Equal(SwarmTaskState.InTransit, (await coordinator.Status()).Tasks.Single().State);
        await coordinator.Connection.SyncDrive(CancellationToken.None);
        string carried = Assert.Single(Directory.GetFiles(Path.Combine(drive, "auto-remote", "mail", (await coordinator.Peer()).Id), "0*.msg", SearchOption.AllDirectories));
        byte[] package = await File.ReadAllBytesAsync(carried);
        Assert.DoesNotContain("Please by drive.", Encoding.Latin1.GetString(package));
        await worker.Connection.SyncDrive(CancellationToken.None);
        await File.WriteAllBytesAsync(carried, package);
        await worker.Connection.SyncDrive(CancellationToken.None);
        await worker.Found(task => task.State == SwarmTaskState.Done);
        await worker.Connection.SyncDrive(CancellationToken.None);
        await coordinator.Connection.SyncDrive(CancellationToken.None);

        SwarmTaskInfo done = await coordinator.Found(task => task.State == SwarmTaskState.Done);
        Assert.Single(worker.Asked);
        await SwarmTestServer.Until(async () => (await coordinator.Status()).Tasks.All(task => task.Id != done.Id));
        Assert.True(File.Exists(Path.Combine(folder, "done.txt")));
        await coordinator.Connection.SyncDrive(CancellationToken.None);
        await worker.Connection.SyncDrive(CancellationToken.None);
        await SwarmTestServer.Until(async () => (await worker.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Count == 0);
        Assert.Empty(Directory.GetFiles(Path.Combine(drive, "auto-remote", "mail"), "0*.msg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ATamperedPackageOnTheDriveIsRejectedAndSaidSo()
    {
        string drive = temp.Combine("drive2");
        Directory.CreateDirectory(drive);

        foreach (SwarmTestServer server in new[] { coordinator, worker })
        {
            await server.Connection.SetSwarm((await server.Status()).Server with { DriveFolder = drive }, CancellationToken.None);
        }

        await coordinator.Connection.WriteDriveInvitation(CancellationToken.None);
        await worker.Connection.ConfirmDrivePairing((await worker.Status()).DrivePairings.Single().Id, CancellationToken.None);
        await coordinator.Connection.ConfirmDrivePairing((await coordinator.Status()).DrivePairings.Single().Id, CancellationToken.None);
        await coordinator.Connection.SyncDrive(CancellationToken.None);
        string package = Directory.GetFiles(Path.Combine(drive, "auto-remote", "mail", (await coordinator.Peer()).Id), "profile.msg", SearchOption.AllDirectories).Single();
        byte[] bytes = await File.ReadAllBytesAsync(package);
        bytes[^100] ^= 1;
        await File.WriteAllBytesAsync(package, bytes);

        SwarmStatus synced = await worker.Connection.SyncDrive(CancellationToken.None);

        Assert.Contains("Problems", synced.Report);
        Assert.Contains("not signed", synced.Report);
    }

    [Fact]
    public async Task RevokingAServerFailsItsTasksAndEndsTheConnection()
    {
        worker.Hangs = true;
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, _) = await Project();
        await coordinator.Connection.DelegateTask(workspace.Id, Request("revoked"), CancellationToken.None);
        await coordinator.Found(task => task.State == SwarmTaskState.Running);

        await coordinator.Connection.RevokeSwarmPeer((await coordinator.Peer()).Id, CancellationToken.None);

        Assert.Empty((await coordinator.Status()).Peers);
        SwarmTaskInfo failed = Assert.Single((await coordinator.Status()).Tasks);
        Assert.Equal((SwarmTaskState.Failed, "The worker is no longer paired."), (failed.State, failed.Error));
        await SwarmTestServer.Until(async () => !(await worker.Peer()).IsConnected && (await worker.Peer()).Problem is not null);
    }
}
