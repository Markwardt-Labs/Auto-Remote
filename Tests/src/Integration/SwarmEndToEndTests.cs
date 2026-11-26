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
    public async Task ATaskDelegatedDirectlyRunsRestrictedOnTheWorkerAndComesBackAsABranchThatApplies()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, string folder) = await Project();

        await coordinator.Connection.DelegateTask(workspace.Id, Request("write the file"), CancellationToken.None);

        SwarmTaskInfo done = await coordinator.Found(task => task.State == SwarmTaskState.Done);
        Assert.Equal("1", Environment.GetEnvironmentVariable("OPENCODE_DISABLE_PROJECT_CONFIG"));
        Assert.Equal(("write the file", "worker", true, false), (done.Title, done.PeerName, done.IsManual, done.IsApplied));
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

        SwarmStatus applied = await coordinator.Connection.ApplySwarmTask(done.Id, CancellationToken.None);

        Assert.Contains("1 file", applied.Report);
        Assert.StartsWith("done by the worker", await File.ReadAllTextAsync(Path.Combine(folder, "done.txt")));
        await Assert.ThrowsAsync<RemoteException>(() => coordinator.Connection.ApplySwarmTask(done.Id, CancellationToken.None));

        await SwarmTestServer.Until(async () => !Directory.Exists(workerFolder) && (await worker.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Count == 0);
        await coordinator.Connection.DiscardSwarmTask(done.Id, CancellationToken.None);
        Assert.Empty((await coordinator.Status()).Tasks);
        Assert.Equal("main", await Git(folder, "branch", "--format=%(refname:short)"));
    }

    private string Use(FakePromptSession session, string tool, object arguments)
    {
        PromptTool found = Assert.Single(session.Transient.Tools, candidate => candidate.Name == tool);
        return Task.Run(() => found.Handler(JsonSerializer.SerializeToElement(arguments), CancellationToken.None)).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task TheAiDelegatesDependentTasksEndsItsTurnAndIsToldWhenTheyEndedToReviewAndApplyThem()
    {
        await PairByNetwork();
        await Permit();
        await KnowsTheWorker();
        (WorkspaceInfo workspace, string folder) = await Project();
        List<string> toolResults = [];
        List<bool> sawEarlierWork = [];
        worker.Ai = (session, prompt) =>
        {
            string title = prompt.Text.Split('\n')[0].TrimStart('#', ' ');

            lock (sawEarlierWork)
            {
                sawEarlierWork.Add(File.Exists(Path.Combine(session.WorkspacePath!, "first.txt")));
            }

            File.WriteAllText(Path.Combine(session.WorkspacePath!, $"{title}.txt"), title);
            return [new PromptMessageEvent { Text = $"Report for {title}." }];
        };
        coordinator.Ai = (session, prompt) =>
        {
            if (prompt.Text.Contains("split this up"))
            {
                toolResults.Add(Use(session, "list_workers", new { }));
                toolResults.Add(Use(session, "delegate_tasks", new { tasks = new object[] { new { title = "first", instructions = "Write first.txt.", capability = "strong" }, new { title = "second", instructions = "Write second.txt.", worker = "worker", afterTasksInThisCall = new[] { 1 } } } }));
                return [new PromptMessageEvent { Text = "Delegated; ending my turn." }];
            }

            if (prompt.Text.StartsWith("[Auto Remote]", StringComparison.Ordinal))
            {
                foreach (System.Text.RegularExpressions.Match match in Regex.Matches(prompt.Text, "Task ([0-9a-f]{32})"))
                {
                    toolResults.Add(Use(session, "review_task", new { taskId = match.Groups[1].Value }));
                    toolResults.Add(Use(session, "apply_task", new { taskId = match.Groups[1].Value }));
                }

                toolResults.Add(Use(session, "task_status", new { }));
                return [new PromptMessageEvent { Text = "All applied." }];
            }

            return [new PromptMessageEvent { Text = $"tools: {string.Join(",", session.Transient.Tools.Select(tool => tool.Name))}" }];
        };
        await coordinator.Connection.Prompt(workspace.Id, "which tools", CancellationToken.None);
        await SwarmTestServer.Until(async () => (await Chat(workspace.Id)).Any(message => message.Role == MessageRole.Assistant));
        Assert.Equal("tools: ", (await Chat(workspace.Id)).Last().Text);
        WorkspaceSettings settings = await coordinator.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        Assert.False(settings.IsDelegationAllowed);
        Assert.True((await coordinator.Connection.SetWorkspaceSettings(workspace.Id, settings with { IsDelegationAllowed = true }, CancellationToken.None)).IsDelegationAllowed);
        await SwarmTestServer.Until(async () => (await coordinator.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None)) == WorkspaceState.Ready);

        await coordinator.Connection.Prompt(workspace.Id, "split this up", CancellationToken.None);

        await SwarmTestServer.Until(async () => (await Chat(workspace.Id)).Any(message => message.Text == "All applied."), async () => string.Join(" | ", toolResults) + string.Join(" | ", (await coordinator.Status()).Tasks.Select(task => $"{task.Title} {task.State} {task.Error}")));
        Assert.Contains("worker (id ", toolResults[0]);
        Assert.Contains("Strong (large)", toolResults[0]);
        Assert.Contains("only read and edit files", toolResults[0]);
        Assert.Contains("end your turn", toolResults[1]);
        Assert.Equal([false, true], sawEarlierWork);
        Assert.Contains(toolResults, result => result.Contains("Report for first.") && result.Contains("first.txt"));
        Assert.Contains("applied", toolResults[^1]);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(folder, "first.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(folder, "second.txt")));
        Assert.All((await coordinator.Status()).Tasks, task => Assert.Equal((SwarmTaskState.Done, true, false), (task.State, task.IsApplied, task.IsManual)));
        Assert.All(worker.Asked, asked => Assert.DoesNotContain("split this up", asked.Text));
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
        Assert.Equal((AiCapability.Strong, false, false, false, false, false), (settings.Capability, settings.IsShellAllowed, settings.IsSystemAccessAllowed, settings.IsWebAllowed, settings.IsImageGenerationAllowed, settings.IsDelegationAllowed));
        await Assert.ThrowsAsync<RemoteException>(() => worker.Connection.Prompt(delegated, "do something else", CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => worker.Connection.SetWorkspaceSettings(delegated, settings with { IsShellAllowed = true }, CancellationToken.None));
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

        await coordinator.Connection.DelegateTask(workspace.Id, Request("use the remote"), CancellationToken.None);

        SwarmTaskInfo done = await coordinator.Found(task => task.State is SwarmTaskState.Done or SwarmTaskState.Failed);
        Assert.Equal((SwarmTaskState.Done, null), (done.State, done.Error));
        Assert.Contains("swarm/results/", await Git(origin, "for-each-ref", "--format=%(refname)"));

        await coordinator.Connection.ApplySwarmTask(done.Id, CancellationToken.None);

        Assert.Equal(string.Empty, await Git(origin, "for-each-ref", "refs/heads/swarm"));
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
        await coordinator.Connection.ApplySwarmTask(done.Id, CancellationToken.None);
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
