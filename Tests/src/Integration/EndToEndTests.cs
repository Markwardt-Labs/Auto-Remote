namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();

    private SemaphoreSlim? commitGate;

    // Every prompt the AI got, as it got it.
    private readonly ConcurrentQueue<string> aiGot = new();

    // How often the AI cannot run for a prompt that starts with "outage" before it answers one.
    private int outages;

    private CancellationTokenSource stopping = new();

    private Task running = Task.CompletedTask;

    private PromptService promptsOf = null!;

    private PrompterHost hostOf = null!;

    private IServerConnection connection = null!;

    private readonly List<RemoteEvent> received = [];

    private ConnectionSettings settings = null!;

    private FakePrompter prompterOf = null!;

    private Mock<IUsageService> usageOf = null!;

    private readonly Mock<IServerUpdater> updater = new();

    public Task InitializeAsync() => Open();

    // A whole new server over the same data, with the AI conversations the last one had.
    private async Task Open()
    {
        string fakeDotnet = temp.Combine("fake-dotnet");
        await File.WriteAllTextAsync(fakeDotnet, "#!/bin/sh\ncase \"$*\" in *slow.cs*) sleep 30 ;; esac\necho \"display=$DISPLAY wayland=$WAYLAND_DISPLAY other=$AUTO_REMOTE_PROBE\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ServerConfig config = new() { Port = 0, Key = "secret", DotnetCommand = fakeDotnet, DataFolder = temp.Combine("data"), SpeechCommand = "sh", SpeechArguments = ["-c", "echo said $(wc -c < \"$1\")", "speech", "{file}"] };
        FolderBrowser folders = new();
        WorkspaceStore store = new();
        EventHub hub = new();
        GitService git = new();
        WorkspaceActivity activity = new(hub);
        WorkspaceSnapshots snapshots = new(git);
        FakePrompter prompter = prompterOf ??= new() { OnCreate = session => session.ReplyWith(Answer) };
        PrompterHost host = hostOf = new(_ => prompter);
        WorkspaceManager workspaces = new(store, folders, hub, host);
        ISessionTools? tools = null;
        ISideIntegrations? integrations = null;
        ShellCommandLog shellCommands = new();
        IFollowUps? followUps = null;
        PromptService prompts = promptsOf = new(workspaces, store, host, hub, activity, snapshots, null, () => tools, shellCommands, () => followUps);
        SpeechService speech = new(Mock.Of<IWhisperEngine>());
        SavedInputs savedInputs = new(store, workspaces, hub, speech);
        WorkQueue queue = new(workspaces, prompts, host, activity, savedInputs, hub, () => followUps);
        WorkspaceVersioning versioning = new(git, prompts, hub, activity, queue);
        Mock<IUsageService> usage = usageOf = new();
        WindowGuard guard = new(workspaces, prompts, usage.Object, host, hub) { CheckEvery = TimeSpan.FromMilliseconds(50), RecheckEvery = TimeSpan.FromMilliseconds(1200) };
        WorkspaceLoops loops = new(workspaces, prompts, activity, usage.Object, hub, TimeSpan.FromMilliseconds(20), null, TimeSpan.FromMilliseconds(200), guard, queue, id => integrations?.IsDelivering(id) == true, () => followUps);
        ScriptRunner scripts = new(hub);
        tools = new CombinedSessionTools([new SideSessionTools(() => integrations!, _ => false), new OutcomeTools(() => followUps!)]);
        WorkspaceWatcher watcher = new(hub, Mock.Of<ILog>(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300));
        SideSessionRepository sideRepository = new(git);
        SideSessions sideSessions = new(workspaces, sideRepository, store, prompts, queue, loops, scripts, guard, snapshots, watcher, hub, Mock.Of<ILog>(), () => followUps);
        ActivityLog journal = new(hub, Mock.Of<ILog>());
        integrations = new SideIntegrations(workspaces, sideSessions, sideRepository, prompts, queue, activity, loops, scripts, guard, snapshots, hub, Mock.Of<ILog>(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), () => followUps, journal);
        followUps = new FollowUps(workspaces, savedInputs, prompts, queue, activity, versioning, git, loops, guard, hub, journal, new AutomationStore(Mock.Of<ILog>()), new FailureKinds([TimeSpan.FromMilliseconds(200)]), _ => Task.FromResult(false), id => integrations.IsDelivering(id), TimeSpan.FromMilliseconds(30), integrations.Reopen);
        AgendaService agenda = new(workspaces, followUps, savedInputs, loops, queue, guard, activity, hub, id => integrations.Pending(id), TimeSpan.FromMilliseconds(50));
        RequestHandler handler = new(workspaces, folders, new WorkspaceFileBrowser(), git, versioning, savedInputs, new ErrorHistory(store, workspaces, hub), guard, prompts, new DiagramService(), scripts, usage.Object, activity, new WorkspaceControl(activity, prompts, versioning, snapshots), speech, host, loops, watcher, updater.Object, Mock.Of<ILog>(), queue, Mock.Of<ISwarmService>(swarm => swarm.Delegations == Array.Empty<WorkspaceDelegation>()), sideSessions, integrations, sideRepository, new ResourceMonitor(new ProcessSource(), shellCommands, workspaces, activity, scripts), followUps, journal, agenda);
        await handler.Open(config, stopping.Token);
        ConnectionServer server = new(new Handshake(Mock.Of<IAppInfo>(info => info.Version == "1.2.3")), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), handler, hub, Mock.Of<ILog>());
        TaskCompletionSource<int> listening = new();
        server.Listening += listening.SetResult;
        running = server.Run(config, stopping.Token);
        int port = await listening.Task;

        settings = new ConnectionSettings { Host = "127.0.0.1", Port = port, AccessKey = "secret" };
        connection = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(settings, CancellationToken.None);
        connection.EventReceived += remoteEvent =>
        {
            lock (received)
            {
                received.Add(remoteEvent);
            }
        };
    }

    // What was asked, without what the server adds for the AI.
    private string Asked(string text) => text.Split("\n\n[Auto Remote]\n")[0];

    private IEnumerable<PromptEvent> Answer(FakePrompt prompt)
    {
        string asked = Asked(prompt.Text);
        aiGot.Enqueue(prompt.Text);

        if (prompt.Text.StartsWith("Write a git commit message", StringComparison.Ordinal))
        {
            commitGate?.Wait(TimeSpan.FromSeconds(10));
            return [new PromptMessageEvent { Text = $"Add {string.Join(' ', Regex.Matches(prompt.Text, @"gated-[a-z]+").Select(match => match.Value).Distinct())}" }];
        }

        if (asked.StartsWith("gate", StringComparison.Ordinal))
        {
            return Gated(asked);
        }

        if (asked.StartsWith("fail", StringComparison.Ordinal))
        {
            return [new PromptFailureEvent { Message = "boom: " + asked }];
        }

        if (asked.StartsWith("outage", StringComparison.Ordinal) && Interlocked.Decrement(ref outages) >= 0)
        {
            return [new PromptFailureEvent { Message = "The service is overloaded." }];
        }

        if (asked.StartsWith("stream", StringComparison.Ordinal))
        {
            return Writing(asked.EndsWith("aloud", StringComparison.Ordinal));
        }

        if (asked.StartsWith("work", StringComparison.Ordinal))
        {
            return [new PromptToolCallEvent { Id = "t1", Name = "shell", Input = "{\"command\":\"ls\"}" }, new PromptToolResultEvent { Id = "t1", Output = "a.txt" }, new PromptMessageEvent { Text = "reply to " + asked }];
        }

        if (asked == "long")
        {
            Thread.Sleep(600);
        }

        return [new PromptMessageEvent { Text = "reply to " + asked }];
    }

    // Work that goes on until the test lets it end, and then fails when told to.
    private IEnumerable<PromptEvent> Gated(string text)
    {
        Assert.True(Gate(text).Wait(TimeSpan.FromSeconds(20)), $"Nothing let '{text}' end.");
        yield return text.EndsWith("badly", StringComparison.Ordinal) ? new PromptFailureEvent { Message = "boom" } : new PromptMessageEvent { Text = "reply to " + text };
    }

    private SemaphoreSlim Gate(string text) => gates.GetOrAdd(text, _ => new SemaphoreSlim(0));

    private IEnumerable<PromptEvent> Writing(bool shown)
    {
        foreach (string piece in (string[])["The ", "answer ", "is ", "streamed."])
        {
            yield return new PromptDeltaEvent { Text = piece };

            if (!shown)
            {
                Thread.Sleep(150);
                continue;
            }

            // A piece still waiting when the finished message arrives is dropped, so the AI goes on only once the client has its piece.
            for (int attempt = 0; attempt < 1000 && !Streamed().EndsWith(piece, StringComparison.Ordinal); attempt++)
            {
                Thread.Sleep(10);
            }
        }

        yield return new PromptMessageEvent { Text = "The answer is streamed." };
    }

    private string Streamed()
    {
        lock (received)
        {
            return string.Concat(received.Where(item => item.Kind == RemoteEventKind.MessageDelta).Select(item => item.Text));
        }
    }

    // The activity and the agenda follow the work, so they may be told after it.
    private RemoteEvent Latest(List<RemoteEvent> events) => events.Last(item => item.Kind is not (RemoteEventKind.ActivityChanged or RemoteEventKind.AgendaChanged));

    private async Task<List<RemoteEvent>> Received(Func<List<RemoteEvent>, bool> done)
    {
        for (int attempt = 0; attempt < 750; attempt++)
        {
            lock (received)
            {
                if (done(received))
                {
                    return [.. received];
                }
            }

            await Task.Delay(20);
        }

        lock (received)
        {
            Assert.Fail("Timed out waiting for events: " + string.Join(", ", received.Select(remoteEvent => $"{remoteEvent.Kind}:{remoteEvent.Message?.Text}{remoteEvent.Working}{remoteEvent.Path}{remoteEvent.Error}")));
            return [];
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Nothing is left blocked on a gate once the test is over.
        foreach (SemaphoreSlim gate in gates.Values)
        {
            gate.Release(10);
        }

        await Shut();
        temp.Dispose();
    }

    private async Task Shut(Action? ending = null)
    {
        await connection.DisposeAsync();
        await stopping.CancelAsync();
        // What the AI of the server that is ending still does.
        ending?.Invoke();
        await running;
        await promptsOf.Close();
        await hostOf.DisposeAsync();
        // What the server writes as it goes is on its way to the disk.
        await Task.Delay(100);
        stopping.Dispose();
    }

    [Theory]
    [InlineData("wss://myserver")]
    [InlineData("192.168.1.5:52679")]
    [InlineData("my server")]
    public async Task Connect_ToAHostThatCannotBeOneFailsLikeAnyServerThatCannotBeReached(string host)
        => await Assert.ThrowsAsync<IOException>(() => new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(new ConnectionSettings { Host = host, Port = 5, AccessKey = "k" }, CancellationToken.None));

    private async Task<(WorkspaceInfo Workspace, string Folder)> Repository(string name)
    {
        FolderListing folder = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, name, CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        await connection.GitSetRemote(workspace.Id, "/nonexistent/app.git", CancellationToken.None);
        File.WriteAllText(Path.Combine(folder.Path, "first.txt"), "first\n");
        await connection.GitCommit(workspace.Id, "first", CancellationToken.None);
        return (workspace, folder.Path);
    }

    private async Task<string[]> Commits(Guid id) => [.. (await connection.GetGitHistory(id, 0, 20, CancellationToken.None)).Commits.Select(commit => commit.Subject)];

    private async Task Eventually(Func<Task<bool>> condition, string what)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        lock (received)
        {
            Assert.Fail($"{what} never happened: " + string.Join(", ", received.Select(remoteEvent => $"{remoteEvent.Kind}:{remoteEvent.Message?.Text}{remoteEvent.State}{remoteEvent.Working}{remoteEvent.AutoCommit}{remoteEvent.Error}")));
        }
    }

    private Task Working(Guid id) => Eventually(async () => await connection.GetWorkspaceState(id, CancellationToken.None) == WorkspaceState.Working, "The work");

    private Task Idle(Guid id) => Eventually(async () => await connection.GetWorkspaceState(id, CancellationToken.None) == WorkspaceState.Ready && (await connection.GetHistory(id, null, null, null, CancellationToken.None)) is { Working: false, AutoCommit: AutoCommitState.Off }, "The end of the work");

    private async Task<string[]> Said(Guid id) => [.. (await connection.GetHistory(id, null, 100, null, CancellationToken.None)).Messages.Where(message => message.Role == MessageRole.User).Select(message => message.Text)];

    [Fact]
    public async Task AutoCommit_CommitsTheChangesOnceTheWorkEndsWithNoReadyMomentInBetweenAndOnlyForThatWork()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("committed");
        await Assert.ThrowsAsync<RemoteException>(() => connection.SetAutoCommit(workspace.Id, true, CancellationToken.None));
        await connection.Prompt(workspace.Id, "gate one", CancellationToken.None);
        await Working(workspace.Id);
        Assert.Equal(AutoCommitState.Off, (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit);
        await connection.SetAutoCommit(workspace.Id, true, CancellationToken.None);
        Assert.Equal(AutoCommitState.On, (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit);
        File.WriteAllText(Path.Combine(folder, "gated-one.txt"), "one\n");
        commitGate = new SemaphoreSlim(0);
        lock (received)
        {
            received.Clear();
        }

        Gate("gate one").Release();
        await Eventually(async () => (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit == AutoCommitState.Committing, "The commit");

        // Nothing else starts before the commit is done.
        Assert.Equal(WorkspaceState.Managing, await connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => connection.Prompt(workspace.Id, "too soon", CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => connection.SetAutoCommit(workspace.Id, false, CancellationToken.None));

        commitGate.Release();
        await Idle(workspace.Id);

        Assert.Equal(["Add gated-one", "first"], await Commits(workspace.Id));
        List<RemoteEvent> events = await Received(items => items.Any(item => item.Kind == RemoteEventKind.AutoCommitChanged && item.AutoCommit == AutoCommitState.Off));
        int committing = events.FindIndex(item => item.AutoCommit == AutoCommitState.Committing);
        int stopped = events.FindIndex(item => item.Kind == RemoteEventKind.WorkingChanged && item.Working == false);
        int ready = events.FindIndex(item => item.Kind == RemoteEventKind.StateChanged && item.State == WorkspaceState.Ready);
        Assert.InRange(committing, 0, stopped - 1);
        Assert.True(ready > stopped, "The workspace was ready between the work and the commit.");

        commitGate = null;
        await connection.Prompt(workspace.Id, "gate two", CancellationToken.None);
        await Working(workspace.Id);
        File.WriteAllText(Path.Combine(folder, "gated-two.txt"), "two\n");
        Gate("gate two").Release();
        await Idle(workspace.Id);

        Assert.Equal(["Add gated-one", "first"], await Commits(workspace.Id));
    }

    [Fact]
    public async Task QueuedInputs_RunInOrderAndQueuingToCommitDuringASentMessageCommitsItFirstSoEachGetsACommitOfItsOwn()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("queued");
        await connection.Prompt(workspace.Id, "gate current", CancellationToken.None);
        await Working(workspace.Id);
        await connection.SaveInput(workspace.Id, "kept for later", true, CancellationToken.None);
        // Neither sending a message nor keeping a draft turns the commit on.
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));
        await connection.QueueInput(workspace.Id, "gate next", true, CancellationToken.None);
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        IReadOnlyList<SavedInput> saved = await connection.QueueInput(workspace.Id, "gate last", true, CancellationToken.None);

        Assert.Equal([("gate next", true), ("gate last", true), ("kept for later", false)], saved.Select(input => (input.Text, input.IsQueued)));
        File.WriteAllText(Path.Combine(folder, "gated-current.txt"), "current\n");
        Gate("gate current").Release();

        await Sent(workspace.Id, "gate next");
        Assert.Equal(["Add gated-current", "first"], await Commits(workspace.Id));
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        Assert.Equal(["gate last", "kept for later"], (await connection.GetSavedInputs(workspace.Id, CancellationToken.None)).Select(input => input.Text));
        File.WriteAllText(Path.Combine(folder, "gated-next.txt"), "next\n");
        Gate("gate next").Release();

        await Sent(workspace.Id, "gate last");
        Assert.Equal(["Add gated-next", "Add gated-current", "first"], await Commits(workspace.Id));
        File.WriteAllText(Path.Combine(folder, "gated-last.txt"), "last\n");
        Gate("gate last").Release();
        await Idle(workspace.Id);

        Assert.Equal(["Add gated-last", "Add gated-next", "Add gated-current", "first"], await Commits(workspace.Id));
        Assert.Equal(["gate current", "gate next", "gate last"], await Said(workspace.Id));
        // Only work nobody may be there for is told so, in an addition the chat does not show.
        Assert.Equal("gate current", Assert.Single(AiGot("gate current")));
        Assert.Contains("This work runs unattended", Assert.Single(AiGot("gate next")));
    }

    [Fact]
    public async Task ACommitThatOnlyQueuingTurnedOn_GoesOffAgainWithTheLastQueuedInputThatWantsItAndTheUsersChoiceStays()
    {
        (WorkspaceInfo workspace, _) = await Repository("rethought");
        await connection.Prompt(workspace.Id, "gate thinking", CancellationToken.None);
        await Working(workspace.Id);

        Guid toggled = (await connection.QueueInput(workspace.Id, "gate toggled", true, CancellationToken.None))[0].Id;
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        await connection.SetSavedInputAutoCommit(workspace.Id, toggled, false, CancellationToken.None);
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));
        Guid deleted = (await connection.QueueInput(workspace.Id, "gate deleted", true, CancellationToken.None))[1].Id;
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        await connection.DiscardSavedInput(workspace.Id, deleted, CancellationToken.None);
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));
        Guid loaded = (await connection.QueueInput(workspace.Id, "gate loaded", true, CancellationToken.None))[1].Id;
        await connection.TakeSavedInput(workspace.Id, loaded, CancellationToken.None);
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));

        await connection.SetAutoCommit(workspace.Id, true, CancellationToken.None);
        Guid own = (await connection.QueueInput(workspace.Id, "gate own", true, CancellationToken.None))[1].Id;
        await connection.DiscardSavedInput(workspace.Id, own, CancellationToken.None);
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        await connection.SetAutoCommit(workspace.Id, false, CancellationToken.None);
        Guid refused = (await connection.QueueInput(workspace.Id, "gate refused", true, CancellationToken.None))[1].Id;
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));

        await connection.DiscardSavedInput(workspace.Id, refused, CancellationToken.None);
        await connection.DiscardSavedInput(workspace.Id, toggled, CancellationToken.None);
        Gate("gate thinking").Release();
        await Idle(workspace.Id);
        Assert.Equal(["first"], await Commits(workspace.Id));
    }

    [Fact]
    public async Task QueuedInput_RunsAtOnceWhenNothingIsWorkingAndItsCommitCanBeTurnedOffWhileItWorks()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("immediate");

        await connection.QueueInput(workspace.Id, "gate now", true, CancellationToken.None);

        await Working(workspace.Id);
        await Eventually(async () => (await connection.GetSavedInputs(workspace.Id, CancellationToken.None)).Count == 0, "The removal from the queue");
        await connection.SetAutoCommit(workspace.Id, false, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "gated-now.txt"), "now\n");
        Gate("gate now").Release();
        await Idle(workspace.Id);
        await Task.Delay(200);

        Assert.Equal(["first"], await Commits(workspace.Id));
        Assert.True((await connection.GetGitStatus(workspace.Id, CancellationToken.None)).HasChanges);
    }

    [Fact]
    public async Task AFailedSentMessage_IsNotCommittedHoldsNothingUpAndTheAiIsToldWithItsNextTurn()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("failing");
        await connection.Prompt(workspace.Id, "gate goes badly", CancellationToken.None);
        await Working(workspace.Id);
        await connection.QueueInput(workspace.Id, "gate after", true, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "gated-broken.txt"), "broken\n");
        Gate("gate goes badly").Release();

        await Sent(workspace.Id, "gate after");

        Assert.Equal(["first"], await Commits(workspace.Id));
        Assert.False((await connection.GetAgenda(workspace.Id, CancellationToken.None)).IsStopped);
        string told = Assert.Single(AiGot("gate after"));
        Assert.Contains("Since your last turn:", told);
        Assert.Contains("\"gate goes badly\" failed: boom", told);
        Assert.Equal(["gate goes badly", "gate after"], await Said(workspace.Id));
        Gate("gate after").Release();
        await Idle(workspace.Id);
        // What the failed work left behind goes along with the next commit.
        Assert.Equal(["Add gated-broken", "first"], await Commits(workspace.Id));
        Assert.Empty(await connection.GetSavedInputs(workspace.Id, CancellationToken.None));
        ActivityEntry failed = Assert.Single(await Activity(workspace.Id), entry => entry.Title == "gate goes badly");
        Assert.Equal((ActivitySource.Send, ActivityOutcome.Failed, "boom", null), (failed.Source, failed.Outcome, failed.Reason, failed.Commit));
    }

    [Fact]
    public async Task AQueuedInputThatFails_IsTriedThreeTimesThenGivenUpAndFlaggedWhileTheQueueGoesOn()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("retried");
        await connection.QueueInput(workspace.Id, "gate broken badly", true, CancellationToken.None);
        await connection.QueueInput(workspace.Id, "gate following", true, CancellationToken.None);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await Sent(workspace.Id, "gate broken badly", attempt);
            File.WriteAllText(Path.Combine(folder, "gated-broken.txt"), $"attempt {attempt}\n");
            Gate("gate broken badly").Release();
        }

        await Sent(workspace.Id, "gate following");

        Assert.Equal(["gate broken badly", "gate broken badly", "gate broken badly", "gate following"], await Said(workspace.Id));
        Assert.Equal(["first"], await Commits(workspace.Id));
        string[] attempts = AiGot("gate broken badly");
        Assert.DoesNotContain("The previous attempt", attempts[0]);
        Assert.All(attempts.Skip(1), text => Assert.Contains("The previous attempt at this ended with an error: boom Check what was already done", text));
        Assert.Contains("failed 3 times and was given up", Assert.Single(AiGot("gate following")));
        ActivityEntry given = Assert.Single(await Activity(workspace.Id), entry => entry.Title == "gate broken badly");
        Assert.Equal((ActivityOutcome.Failed, true, null), (given.Outcome, given.NeedsAttention, given.Commit));
        Gate("gate following").Release();
        await Idle(workspace.Id);
        Assert.Equal(["Add gated-broken", "first"], await Commits(workspace.Id));
    }

    [Fact]
    public async Task WhileTheAiCannotRun_TheQueuedInputIsKeptAndTriedAgainWithoutLimitSoTheQueueIsNeverUsedUp()
    {
        (WorkspaceInfo workspace, _) = await Repository("outage");
        outages = 5;

        await connection.QueueInput(workspace.Id, "outage one", true, CancellationToken.None);
        await connection.QueueInput(workspace.Id, "two", true, CancellationToken.None);

        await Received(items => items.Any(item => item.Kind == RemoteEventKind.AgendaChanged && item.Agenda!.Items.Any(entry => entry.Kind == AgendaKind.Retry && entry.At is not null) && item.Agenda.Status?.StartsWith("Retrying shortly: The service is overloaded.", StringComparison.Ordinal) == true));
        await Eventually(async () => (await Said(workspace.Id)).Contains("two"), "The second input");
        await Idle(workspace.Id);
        Assert.Equal(["outage one", "outage one", "outage one", "outage one", "outage one", "outage one", "two"], await Said(workspace.Id));
        // Nothing of the work was done, so there is nothing for the AI to check.
        Assert.All(AiGot("outage one"), text => Assert.DoesNotContain("The previous attempt", text));
        ActivityEntry waited = Assert.Single(await Activity(workspace.Id), entry => entry.Title == "outage one");
        Assert.Equal((ActivityOutcome.Done, false), (waited.Outcome, waited.NeedsAttention));
        Assert.Contains(waited.Notes, note => note.Contains("The AI could not run"));
    }

    [Fact]
    public async Task StoppedWork_IsNotCommittedAndLeavesTheChatStoppedWhichNeitherQueuingNorSendingResumes()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("stopped");
        await connection.Prompt(workspace.Id, "gate stopped", CancellationToken.None);
        await Working(workspace.Id);
        await connection.QueueInput(workspace.Id, "gate taken", true, CancellationToken.None);
        await connection.QueueInput(workspace.Id, "gate deleted", true, CancellationToken.None);
        await connection.QueueInput(workspace.Id, "gate kept", true, CancellationToken.None);
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        File.WriteAllText(Path.Combine(folder, "gated-half.txt"), "half\n");

        Task stopping = connection.StopWorkspace(workspace.Id, CancellationToken.None);
        // Only once the stop has the work, or the AI could end it before the stop arrives.
        await Halting(workspace.Id);
        Gate("gate stopped").Release();
        await stopping;
        await Idle(workspace.Id);
        await Task.Delay(300);

        Assert.Equal(["first"], await Commits(workspace.Id));
        Agenda held = await connection.GetAgenda(workspace.Id, CancellationToken.None);
        Assert.Equal((true, false, "Stopped · 3 up next"), (held.IsStopped, held.IsPending, held.Status));
        IReadOnlyList<SavedInput> waiting = await connection.GetSavedInputs(workspace.Id, CancellationToken.None);
        Assert.All(waiting, input => Assert.True(input.IsQueued));
        SavedInput taken = await connection.TakeSavedInput(workspace.Id, waiting[0].Id, CancellationToken.None);
        Assert.Equal(("gate taken", false), (taken.Text, taken.IsQueued));
        await connection.DiscardSavedInput(workspace.Id, waiting[1].Id, CancellationToken.None);

        await connection.QueueInput(workspace.Id, "gate new", true, CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(["gate stopped"], await Said(workspace.Id));

        await connection.Prompt(workspace.Id, "gate manual", CancellationToken.None);
        await Working(workspace.Id);
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));
        Gate("gate manual").Release();
        await Idle(workspace.Id);
        await Task.Delay(300);
        Assert.Equal(["gate stopped", "gate manual"], await Said(workspace.Id));
        Assert.True((await connection.GetAgenda(workspace.Id, CancellationToken.None)).IsStopped);
        Assert.Equal(["first"], await Commits(workspace.Id));

        await connection.ResumeAutomation(workspace.Id, CancellationToken.None);

        await Sent(workspace.Id, "gate kept");
        Assert.Equal(["gate new"], (await connection.GetSavedInputs(workspace.Id, CancellationToken.None)).Select(input => input.Text));
        Gate("gate kept").Release();
        await Sent(workspace.Id, "gate new");
        Gate("gate new").Release();
        await Idle(workspace.Id);
        Assert.Equal(["gate stopped", "gate manual", "gate kept", "gate new"], await Said(workspace.Id));
        Assert.Equal("Add gated-half", (await Commits(workspace.Id))[0]);
    }

    [Fact]
    public async Task ResumeAutomation_SendsWhatAStopHeldAndTheStopAndTheResumeAreToldToEveryClient()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("resumed");
        await connection.Prompt(workspace.Id, "gate given up", CancellationToken.None);
        await Working(workspace.Id);
        await connection.QueueInput(workspace.Id, "gate resumed", true, CancellationToken.None);
        Task stopping = connection.StopWorkspace(workspace.Id, CancellationToken.None);
        // Only once the stop has the work, or the AI could end it before the stop arrives.
        await Halting(workspace.Id);
        Gate("gate given up").Release();
        await stopping;
        await Idle(workspace.Id);
        await Received(items => items.Any(item => item.Kind == RemoteEventKind.AgendaChanged && item.Agenda is { IsStopped: true, Status: "Stopped · 1 up next" }));
        await Task.Delay(300);
        Assert.Equal(["gate given up"], await Said(workspace.Id));
        ActivityEntry stopped = Assert.Single(await Activity(workspace.Id));
        Assert.Equal((ActivityOutcome.Stopped, false), (stopped.Outcome, stopped.NeedsAttention));
        int mark = Mark();

        await connection.ResumeAutomation(workspace.Id, CancellationToken.None);

        await Sent(workspace.Id, "gate resumed");
        await Received(items => items.Skip(mark).Any(item => item.Kind == RemoteEventKind.AgendaChanged && item.Agenda is { IsStopped: false }));
        File.WriteAllText(Path.Combine(folder, "gated-resumed.txt"), "resumed\n");
        Gate("gate resumed").Release();
        await Idle(workspace.Id);
        Assert.Equal("Add gated-resumed", (await Commits(workspace.Id))[0]);
        Assert.Empty(await connection.GetSavedInputs(workspace.Id, CancellationToken.None));
    }

    [Fact]
    public async Task StoppingARunOfALoop_HoldsTheLoopUntilItIsResumedInsteadOfEndingIt()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("held");
        await connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "gate held loop", AutoCommit = true }, CancellationToken.None);
        await Sent(workspace.Id, "gate held loop");
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        File.WriteAllText(Path.Combine(folder, "gated-held.txt"), "held\n");
        Task stopping = connection.StopWorkspace(workspace.Id, CancellationToken.None);
        await Halting(workspace.Id);
        Gate("gate held loop").Release();
        await stopping;
        await Idle(workspace.Id);
        await Task.Delay(300);

        Assert.NotNull(await Loop(workspace.Id));
        Assert.True((await connection.GetAgenda(workspace.Id, CancellationToken.None)).IsStopped);
        Assert.Equal(1, await SaidTimes(workspace.Id, "gate held loop"));
        Assert.Equal(["first"], await Commits(workspace.Id));

        await connection.ResumeAutomation(workspace.Id, CancellationToken.None);

        await Sent(workspace.Id, "gate held loop", 2);
        Gate("gate held loop").Release();
        // A loop set to commit has each of its runs committed.
        await Sent(workspace.Id, "gate held loop", 3);
        Assert.Equal(["Add gated-held", "first"], await Commits(workspace.Id));
        await connection.CancelLoop(workspace.Id, CancellationToken.None);
        Gate("gate held loop").Release();
        await Idle(workspace.Id);
    }

    [Fact]
    public async Task AFailedRunNeverEndsALoopAndEachOneIsFlagged()
    {
        (WorkspaceInfo workspace, _) = await Repository("unlucky");

        await connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "fail every run", Condition = LoopCondition.Runs, Runs = 3 }, CancellationToken.None);

        List<RemoteEvent> events = await Received(items => items.Any(item => item.Kind == RemoteEventKind.LoopChanged && item.Loop is null));
        Assert.Equal("The loop made all its runs.", events.Last(item => item.Kind == RemoteEventKind.LoopChanged).Text);
        await Idle(workspace.Id);
        Assert.Equal(3, await SaidTimes(workspace.Id, "fail every run"));
        Assert.False((await connection.GetAgenda(workspace.Id, CancellationToken.None)).IsStopped);
        await Eventually(async () => (await Activity(workspace.Id)).Count(entry => entry is { Source: ActivitySource.Loop, Outcome: ActivityOutcome.Failed, NeedsAttention: true }) == 3, "The three flagged runs");
    }

    [Fact]
    public async Task ARestartOfTheServer_TakesInterruptedWorkUpAgainWithANoteAboutItAndRestoresTheLoop()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("restarted");
        await connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "gate restored", IntervalSeconds = 600 }, CancellationToken.None);
        await Sent(workspace.Id, "gate restored");
        Gate("gate restored").Release();
        await Eventually(async () => await Loop(workspace.Id) is { IsRunning: false }, "The end of the run");
        await Idle(workspace.Id);
        await connection.QueueInput(workspace.Id, "gate interrupted", true, CancellationToken.None);
        await Sent(workspace.Id, "gate interrupted");
        await connection.QueueInput(workspace.Id, "gate waiting", true, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "gated-interrupted.txt"), "interrupted\n");

        await Restart("gate interrupted");

        await Sent(workspace.Id, "gate interrupted", 2);
        Assert.DoesNotContain("The server restarted", AiGot("gate interrupted")[0]);
        Assert.Contains("The server restarted while you were working on this. Check what was already done", AiGot("gate interrupted")[1]);
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        LoopInfo restored = (await Loop(workspace.Id))!;
        Assert.Equal(("gate restored", 600, 1), (restored.Settings.Prompt, restored.Settings.IntervalSeconds, restored.Runs));
        Assert.Equal(["gate waiting"], (await connection.GetSavedInputs(workspace.Id, CancellationToken.None)).Select(input => input.Text));
        Gate("gate interrupted").Release();
        await Sent(workspace.Id, "gate waiting");
        Assert.Equal(["Add gated-interrupted", "first"], await Commits(workspace.Id));
        ActivityEntry resumed = Assert.Single(await Activity(workspace.Id), entry => entry.Title == "gate interrupted");
        Assert.Equal((ActivityOutcome.Done, "Add gated-interrupted"), (resumed.Outcome, resumed.Commit));
        Assert.Contains(resumed.Notes, note => note.Contains("The server restarted"));
        Gate("gate waiting").Release();
        await Idle(workspace.Id);
        Assert.NotNull(await Loop(workspace.Id));
        Assert.Equal(1, await SaidTimes(workspace.Id, "gate restored"));
        await connection.CancelLoop(workspace.Id, CancellationToken.None);
    }

    [Fact]
    public async Task ACommitThatTheServerStoppedDuringIsMadeAfterTheRestart()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("interrupted");
        await Pushable(workspace.Id);
        await connection.QueueInput(workspace.Id, "gate finished", true, CancellationToken.None);
        await Sent(workspace.Id, "gate finished");
        File.WriteAllText(Path.Combine(folder, "gated-finished.txt"), "finished\n");
        SemaphoreSlim writing = commitGate = new SemaphoreSlim(0);
        Gate("gate finished").Release();
        await Eventually(() => Task.FromResult(aiGot.Any(text => text.StartsWith("Write a git commit message", StringComparison.Ordinal))), "The request for a commit message");

        await Restart(ending: () =>
        {
            commitGate = null;
            writing.Release(10);
        });

        await Eventually(async () => (await Commits(workspace.Id))[0] == "Add gated-finished", "The commit after the restart");
        await Idle(workspace.Id);
        Assert.Equal(["gate finished"], await Said(workspace.Id));
        await Eventually(async () => (await Activity(workspace.Id)) is [{ Outcome: ActivityOutcome.Done, Commit: "Add gated-finished", Pushed: true, CommitError: null, NeedsAttention: false }], "The record of the commit");
        await connection.Prompt(workspace.Id, "anything left", CancellationToken.None);
        await Idle(workspace.Id);
        Assert.DoesNotContain("could not be committed", Assert.Single(AiGot("anything left")));
    }

    [Fact]
    public async Task AStoppedChat_StaysStoppedThroughARestartUntilItIsResumed()
    {
        (WorkspaceInfo workspace, _) = await Repository("stayed");
        await connection.Prompt(workspace.Id, "gate before", CancellationToken.None);
        await Working(workspace.Id);
        await connection.QueueInput(workspace.Id, "gate held back", true, CancellationToken.None);
        Task stopping = connection.StopWorkspace(workspace.Id, CancellationToken.None);
        await Halting(workspace.Id);
        Gate("gate before").Release();
        await stopping;
        await Idle(workspace.Id);

        await Restart("gate before");
        await Task.Delay(400);

        Assert.True((await connection.GetAgenda(workspace.Id, CancellationToken.None)).IsStopped);
        Assert.Equal(["gate before"], await Said(workspace.Id));
        await connection.ResumeAutomation(workspace.Id, CancellationToken.None);
        await Sent(workspace.Id, "gate held back");
        Gate("gate held back").Release();
        await Idle(workspace.Id);
    }

    [Fact]
    public async Task TheActivityAndTheAgenda_AreReadOverTheProtocolAndEveryChangeOfThemIsPushed()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("observed");
        await Pushable(workspace.Id);
        Assert.Equal((0, null, false), Summary(await connection.GetAgenda(workspace.Id, CancellationToken.None)));
        await connection.Prompt(workspace.Id, "gate observed", CancellationToken.None);
        await Working(workspace.Id);
        Guid planned = (await connection.QueueInput(workspace.Id, "gate planned\nand more", true, CancellationToken.None))[0].Id;

        Agenda agenda = await connection.GetAgenda(workspace.Id, CancellationToken.None);

        AgendaItem item = Assert.Single(agenda.Items);
        Assert.Equal((AgendaKind.Queued, planned, "gate planned", true, null), (item.Kind, item.Id, item.Title, item.AutoCommit, item.Waiting));
        Assert.Equal((false, true, "1 up next"), (agenda.IsStopped, agenda.IsPending, agenda.Status));
        await Received(items => items.Any(pushed => pushed.Kind == RemoteEventKind.AgendaChanged && pushed.WorkspaceId == workspace.Id && pushed.Agenda!.Items.Any(entry => entry.Id == planned)));
        await Received(items => items.Any(pushed => pushed.Kind == RemoteEventKind.ActivityChanged && pushed.ActivityEntry is { Title: "gate observed", Outcome: ActivityOutcome.Running }));
        ActivityEntry running = Assert.Single(await Activity(workspace.Id));
        Assert.Equal((ActivitySource.Send, ActivityOutcome.Running, null), (running.Source, running.Outcome, running.EndedAt));

        File.WriteAllText(Path.Combine(folder, "gated-observed.txt"), "observed\n");
        Gate("gate observed").Release();
        await Sent(workspace.Id, "gate planned\nand more");
        Gate("gate planned\nand more").Release();
        await Idle(workspace.Id);

        await Received(items => items.Any(pushed => pushed.Kind == RemoteEventKind.ActivityChanged && pushed.ActivityEntry is { Title: "gate observed", Outcome: ActivityOutcome.Done, Commit: "Add gated-observed" }));
        await Received(items => items.Last(pushed => pushed.Kind == RemoteEventKind.AgendaChanged).Agenda is { Items.Count: 0, Status: null });
        ActivityPage newest = await connection.GetActivity(workspace.Id, null, 1, CancellationToken.None);
        ActivityPage older = await connection.GetActivity(workspace.Id, newest.Older, 1, CancellationToken.None);
        Assert.Equal((ActivitySource.Queue, ActivityOutcome.Done, "gate planned"), (newest.Entries.Single().Source, newest.Entries.Single().Outcome, newest.Entries.Single().Title));
        ActivityEntry first = Assert.Single(older.Entries);
        Assert.Null(older.Older);
        Assert.Equal(("gate observed", "reply to gate observed", "Add gated-observed", true, null, false), (first.Title, first.Report, first.Commit, first.Pushed, first.CommitError, first.NeedsAttention));
        Assert.NotNull(first.EndedAt);

        // A push that fails is flagged, and the AI hears of it with its next turn.
        await connection.GitSetRemote(workspace.Id, "/nonexistent/app.git", CancellationToken.None);
        await connection.QueueInput(workspace.Id, "gate unpushed", true, CancellationToken.None);
        await Sent(workspace.Id, "gate unpushed");
        File.WriteAllText(Path.Combine(folder, "gated-unpushed.txt"), "unpushed\n");
        Gate("gate unpushed").Release();
        await Idle(workspace.Id);
        await Eventually(async () => (await Activity(workspace.Id))[0] is { Commit: "Add gated-unpushed", Pushed: false, CommitError: not null, NeedsAttention: true }, "The flagged push");
        await connection.Prompt(workspace.Id, "hello again", CancellationToken.None);
        await Idle(workspace.Id);
        Assert.Contains("could not be pushed", Assert.Single(AiGot("hello again")));
        await Assert.ThrowsAsync<RemoteException>(() => connection.GetActivity(Guid.NewGuid(), null, 10, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => connection.GetAgenda(Guid.NewGuid(), CancellationToken.None));
    }

    // Gives a workspace a remote its commits can be pushed to.
    private async Task Pushable(Guid id)
    {
        string remote = temp.Combine($"{id:N}.git");
        Git(temp.Path, "init", "--bare", "--quiet", remote);
        await connection.GitSetRemote(id, remote, CancellationToken.None);
    }

    private (int, string?, bool) Summary(Agenda agenda) => (agenda.Items.Count, agenda.Status, agenda.IsStopped);

    private async Task<AutoCommitState> AutoCommit(Guid id) => (await connection.GetHistory(id, null, null, null, CancellationToken.None)).AutoCommit;

    private async Task<IReadOnlyList<ActivityEntry>> Activity(Guid id) => (await connection.GetActivity(id, null, 100, CancellationToken.None)).Entries;

    private string[] AiGot(string asked) => [.. aiGot.Where(text => Asked(text) == asked)];

    private int Mark()
    {
        lock (received)
        {
            return received.Count;
        }
    }

    // The AI was still working on the gated prompt when the server went; what it does after that reaches nobody.
    private async Task Restart(string? underWay = null, Action? ending = null)
    {
        await Shut(ending);

        if (underWay is not null && gates.TryRemove(underWay, out SemaphoreSlim? old))
        {
            old.Release(10);
        }

        lock (received)
        {
            received.Clear();
        }

        stopping = new CancellationTokenSource();
        await Open();
    }

    private Task Halting(Guid id) => Eventually(() =>
    {
        lock (received)
        {
            return Task.FromResult(received.Any(item => item.Kind == RemoteEventKind.StateChanged && item.WorkspaceId == id && item.State == WorkspaceState.Stopping));
        }
    }, "The stop");

    private async Task<LoopInfo?> Loop(Guid id) => await connection.GetLoop(id, CancellationToken.None);

    private async Task<int> SaidTimes(Guid id, string said) => (await Said(id)).Count(text => text == said);

    // Waits until a prompt was sent as often as asked and its work is under way.
    private async Task Sent(Guid id, string text, int times = 1)
    {
        await Eventually(async () => await SaidTimes(id, text) == times, $"Sending '{text}' {times} times");
        await Working(id);
    }

    [Fact]
    public async Task QueuedInputs_GoAheadOfTheNextRunOfALoopWhichGoesOnAfterThem()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("looped");
        await connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "gate looped" }, CancellationToken.None);
        await Sent(workspace.Id, "gate looped");
        await Eventually(async () => (await Loop(workspace.Id))?.IsRunning == true, "The run counting as under way");
        // A run of the loop is not committed unless the loop says so, but the user may turn that on for the run under way.
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));
        await connection.SetAutoCommit(workspace.Id, true, CancellationToken.None);
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        await connection.QueueInput(workspace.Id, "gate first", true, CancellationToken.None);
        await connection.QueueInput(workspace.Id, "gate second", true, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "gated-run.txt"), "run\n");
        await Task.Delay(200);
        Assert.Equal(["gate looped"], await Said(workspace.Id));

        Gate("gate looped").Release();

        await Sent(workspace.Id, "gate first");
        Assert.False((await Loop(workspace.Id))!.IsRunning);
        Assert.Equal(AutoCommitState.On, (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit);
        File.WriteAllText(Path.Combine(folder, "gated-first.txt"), "first\n");
        Gate("gate first").Release();
        await Sent(workspace.Id, "gate second");
        Assert.Equal(["Add gated-first", "Add gated-run", "first"], await Commits(workspace.Id));
        File.WriteAllText(Path.Combine(folder, "gated-second.txt"), "second\n");
        Gate("gate second").Release();

        await Sent(workspace.Id, "gate looped", 2);
        Assert.Equal(["gate looped", "gate first", "gate second", "gate looped"], await Said(workspace.Id));
        Assert.Equal(["Add gated-second", "Add gated-first", "Add gated-run", "first"], await Commits(workspace.Id));

        // One queued between two runs does not wait for the loop's turn to come round.
        await connection.CancelLoop(workspace.Id, CancellationToken.None);
        Gate("gate looped").Release();
        await Idle(workspace.Id);
        await connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "gate resting", IntervalSeconds = 600 }, CancellationToken.None);
        await Sent(workspace.Id, "gate resting");
        Gate("gate resting").Release();
        await Eventually(async () => (await Loop(workspace.Id)) is { IsRunning: false }, "The end of the run");

        await connection.QueueInput(workspace.Id, "gate between", true, CancellationToken.None);

        await Sent(workspace.Id, "gate between");
        await connection.SetAutoCommit(workspace.Id, false, CancellationToken.None);
        await connection.SetAutoCommit(workspace.Id, true, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "gated-between.txt"), "between\n");
        Gate("gate between").Release();
        await Idle(workspace.Id);
        Assert.Equal("Add gated-between", (await Commits(workspace.Id))[0]);
        Assert.NotNull(await Loop(workspace.Id));
        await connection.CancelLoop(workspace.Id, CancellationToken.None);
    }

    [Fact]
    public async Task ALoopStartedWhileQueuedInputsRunMakesItsFirstRunOnceTheyAreDoneEvenWhenStartedDuringACommit()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("started");
        await connection.QueueInput(workspace.Id, "gate one", true, CancellationToken.None);
        await Sent(workspace.Id, "gate one");
        await connection.QueueInput(workspace.Id, "gate two", true, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "gated-one.txt"), "one\n");
        commitGate = new SemaphoreSlim(0);
        Gate("gate one").Release();
        await Eventually(async () => (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit == AutoCommitState.Committing, "The commit");

        await connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "gate loop", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);

        await Task.Delay(200);
        Assert.Equal(["gate one"], await Said(workspace.Id));
        commitGate.Release(10);
        await Sent(workspace.Id, "gate two");
        // Starting a loop leaves the queued work to be committed.
        Assert.Equal(AutoCommitState.On, (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit);
        await Task.Delay(200);
        Assert.Equal(["gate one", "gate two"], await Said(workspace.Id));
        File.WriteAllText(Path.Combine(folder, "gated-two.txt"), "two\n");
        Gate("gate two").Release();

        await Sent(workspace.Id, "gate loop");
        Assert.Equal(["Add gated-two", "Add gated-one", "first"], await Commits(workspace.Id));
        File.WriteAllText(Path.Combine(folder, "gated-loop.txt"), "loop\n");
        Gate("gate loop").Release();
        await Eventually(async () => await Loop(workspace.Id) is null, "The end of the loop");
        await Idle(workspace.Id);
        Assert.Equal(3, (await Commits(workspace.Id)).Length);
    }

    [Fact]
    public async Task SavedInputs_KeepTheirOwnAutoCommitWhichQueuedAndSentWorkGoesBy()
    {
        (WorkspaceInfo workspace, string folder) = await Repository("choosing");
        SavedInput plain = Assert.Single(await connection.SaveInput(workspace.Id, "saved", true, CancellationToken.None));
        Assert.True(plain.AutoCommit);

        Assert.False(Assert.Single(await connection.SetSavedInputAutoCommit(workspace.Id, plain.Id, false, CancellationToken.None)).AutoCommit);
        Assert.False((await connection.TakeSavedInput(workspace.Id, plain.Id, CancellationToken.None)).AutoCommit);
        await Assert.ThrowsAsync<RemoteException>(() => connection.SetSavedInputAutoCommit(workspace.Id, plain.Id, true, CancellationToken.None));

        await connection.Prompt(workspace.Id, "gate current", CancellationToken.None);
        await Working(workspace.Id);
        await connection.QueueInput(workspace.Id, "gate quiet", false, CancellationToken.None);
        SavedInput later = (await connection.QueueInput(workspace.Id, "gate loud", false, CancellationToken.None))[1];
        await connection.SetSavedInputAutoCommit(workspace.Id, later.Id, true, CancellationToken.None);
        // The sent work under way is to be committed before the queued input that now is, as if it had been queued so.
        Assert.Equal(AutoCommitState.On, await AutoCommit(workspace.Id));
        Gate("gate current").Release();

        await Sent(workspace.Id, "gate quiet");
        Assert.Equal(AutoCommitState.Off, (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit);
        // A queued input kept uncommitted goes with the next one, whatever is queued behind it.
        await connection.QueueInput(workspace.Id, "gate third", true, CancellationToken.None);
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));
        File.WriteAllText(Path.Combine(folder, "gated-quiet.txt"), "quiet\n");
        Gate("gate quiet").Release();
        await Sent(workspace.Id, "gate loud");
        Assert.Equal(["first"], await Commits(workspace.Id));
        Assert.Equal(AutoCommitState.On, (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).AutoCommit);
        File.WriteAllText(Path.Combine(folder, "gated-loud.txt"), "loud\n");
        Gate("gate loud").Release();
        await Sent(workspace.Id, "gate third");
        Assert.Equal(["Add gated-loud gated-quiet", "first"], await Commits(workspace.Id));
        Gate("gate third").Release();
        await Idle(workspace.Id);

        await connection.Prompt(workspace.Id, "gate sent", CancellationToken.None);

        await Working(workspace.Id);
        Assert.Equal(AutoCommitState.Off, await AutoCommit(workspace.Id));
        await connection.SetAutoCommit(workspace.Id, true, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "gated-sent.txt"), "sent\n");
        Gate("gate sent").Release();
        await Idle(workspace.Id);
        Assert.Equal("Add gated-sent", (await Commits(workspace.Id))[0]);
    }

    [Fact]
    public async Task Git_AllActionsRunOnTheServerThroughProtocolMessages()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        FolderListing app = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "gitapp", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None);

        Assert.False((await connection.GetGitStatus(workspace.Id, CancellationToken.None)).IsRepo);
        await connection.GitSetRemote(workspace.Id, "/nonexistent/app.git", CancellationToken.None);
        File.WriteAllText(Path.Combine(app.Path, "a.txt"), "one\ntwo\n");
        GitStatus fresh = await connection.GetGitStatus(workspace.Id, CancellationToken.None);
        Assert.True(fresh.IsRepo);
        Assert.True(fresh.HasChanges);
        Assert.Equal("/nonexistent/app.git", fresh.Remote);
        Assert.Equal([("a.txt", GitChangeStatus.Added)], (await connection.GetGitChanges(workspace.Id, CancellationToken.None)).Select(change => (change.Path, change.Status)));

        await connection.GitCommit(workspace.Id, "first", CancellationToken.None);
        File.WriteAllText(Path.Combine(app.Path, "a.txt"), "one\nTWO\n");
        Assert.Equal(GitChangeStatus.Modified, Assert.Single(await connection.GetGitChanges(workspace.Id, CancellationToken.None)).Status);
        GitDiff diff = await connection.GetGitDiff(workspace.Id, "a.txt", null, CancellationToken.None);
        Assert.Equal([GitDiffKind.Unchanged, GitDiffKind.Removed, GitDiffKind.Added], diff.Lines.Select(line => line.Kind));

        await connection.GitReset(workspace.Id, CancellationToken.None);
        Assert.False((await connection.GetGitStatus(workspace.Id, CancellationToken.None)).HasChanges);
        await connection.GitCreateBranch(workspace.Id, "feature", CancellationToken.None);
        await connection.GitCreateTag(workspace.Id, "v1", CancellationToken.None);
        GitStatus tagged = await connection.GetGitStatus(workspace.Id, CancellationToken.None);
        Assert.Equal(("feature", "v1", "first"), (tagged.Branch, tagged.Tag, tagged.Message));

        GitHistory history = await connection.GetGitHistory(workspace.Id, 0, 10, CancellationToken.None);
        GitCommit commit = Assert.Single(history.Commits);
        Assert.Contains(commit.Refs, item => item.Name == "v1" && item.Kind == GitRefKind.Tag);
        Assert.Equal([("a.txt", GitChangeStatus.Added)], (await connection.GetGitCommitChanges(workspace.Id, commit.Hash, CancellationToken.None)).Select(change => (change.Path, change.Status)));
        Assert.Equal(GitDiffKind.Added, Assert.Single((await connection.GetGitDiff(workspace.Id, "a.txt", commit.Hash, CancellationToken.None)).Lines, line => line.Text == "two").Kind);
        await Assert.ThrowsAsync<RemoteException>(() => connection.GetGitDiff(workspace.Id, "../x", null, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => connection.GitCreateBranch(workspace.Id, "feature", CancellationToken.None));
    }

    [Fact]
    public async Task CloneWorkspace_ClonesOnTheServerAndCreatesTheWorkspace()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        FolderListing source = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "origin", CancellationToken.None);
        WorkspaceInfo original = await connection.CreateWorkspace(source.Path, null, null, null, CancellationToken.None);
        await connection.GitSetRemote(original.Id, "/nonexistent/app.git", CancellationToken.None);
        File.WriteAllText(Path.Combine(source.Path, "a.txt"), "hello");
        await connection.GitCommit(original.Id, "first", CancellationToken.None);
        FolderListing parent = await connection.CreateFolder(temp.Path, "clones", CancellationToken.None);

        WorkspaceInfo cloned = await connection.CloneWorkspace(source.Path, parent.Path, "copy", null, null, null, CancellationToken.None);

        Assert.Equal("copy", cloned.Name);
        Assert.Equal("hello", (await connection.ReadFile(cloned.Id, "a.txt", CancellationToken.None)).Text);
        Assert.Equal("first", (await connection.GetGitStatus(cloned.Id, CancellationToken.None)).Message);
        await Assert.ThrowsAsync<RemoteException>(() => connection.CloneWorkspace(source.Path, parent.Path, "copy", null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task SavedInputs_AreSharedByEveryClientOfTheServerAndTakenByOnlyOne()
    {
        FolderListing app = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "saved", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None);
        await using IServerConnection other = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(settings, CancellationToken.None);
        List<RemoteEvent> seen = [];
        other.EventReceived += remoteEvent =>
        {
            lock (seen)
            {
                seen.Add(remoteEvent);
            }
        };

        await connection.SaveInput(workspace.Id, "first", true, CancellationToken.None);
        IReadOnlyList<SavedInput> saved = await connection.SaveInput(workspace.Id, "second\nline", true, CancellationToken.None);

        Assert.Equal(["second\nline", "first"], saved.Select(input => input.Text).ToList());
        Assert.Equal(["second\nline", "first"], (await other.GetSavedInputs(workspace.Id, CancellationToken.None)).Select(input => input.Text).ToList());
        SavedInput taken = await other.TakeSavedInput(workspace.Id, saved[0].Id, CancellationToken.None);
        Assert.Equal("second\nline", taken.Text);
        await Assert.ThrowsAsync<RemoteException>(() => connection.TakeSavedInput(workspace.Id, saved[0].Id, CancellationToken.None));
        Assert.Equal(["first"], (await connection.GetSavedInputs(workspace.Id, CancellationToken.None)).Select(input => input.Text).ToList());
        await Assert.ThrowsAsync<RemoteException>(() => connection.SaveInput(workspace.Id, "  ", true, CancellationToken.None));

        for (int attempt = 0; attempt < 100; attempt++)
        {
            lock (seen)
            {
                if (seen.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.SavedInputsChanged) >= 3)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        lock (seen)
        {
            Assert.Equal(["second\nline,first", "first"], seen.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.SavedInputsChanged).Skip(1).Select(remoteEvent => string.Join(',', remoteEvent.SavedInputs!.Select(input => input.Text))).ToList());
        }

        await connection.DeleteWorkspace(workspace.Id, true, CancellationToken.None);
    }

    [Fact]
    public async Task TranscribeSavedInput_AddsAnEntryThatConvertsInTheBackgroundForEveryClientAndBehavesLikeAnyOtherAfterwards()
    {
        FolderListing app = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "voice", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None);
        List<IReadOnlyList<SavedInput>> seen = [];
        connection.EventReceived += remoteEvent =>
        {
            if (remoteEvent.Kind == RemoteEventKind.SavedInputsChanged && remoteEvent.SavedInputs is { } inputs)
            {
                lock (seen)
                {
                    seen.Add(inputs);
                }
            }
        };

        IReadOnlyList<SavedInput> started = await connection.TranscribeSavedInput(workspace.Id, new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.True(started.Single().IsConverting || started.Single().Text == "said 3");

        SavedInput? done = null;

        for (int attempt = 0; attempt < 200 && done is null; attempt++)
        {
            done = (await connection.GetSavedInputs(workspace.Id, CancellationToken.None)).FirstOrDefault(input => input.IsReady);
            await Task.Delay(25);
        }

        Assert.Equal("said 3", done?.Text);
        Assert.Equal(started.Single().Id, done!.Id);
        SavedInput taken = await connection.TakeSavedInput(workspace.Id, done.Id, CancellationToken.None);
        Assert.Equal("said 3", taken.Text);
        lock (seen)
        {
            Assert.Contains(seen, inputs => inputs.Any(input => input.Id == done.Id && input.IsReady));
        }
    }

    [Fact]
    public async Task DiscardSavedInput_RemovesAnEntryWithoutTakingItAndCarriesTheRefusalOfAnEmptyRecording()
    {
        FolderListing app = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "discard", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None);
        IReadOnlyList<SavedInput> saved = await connection.SaveInput(workspace.Id, "keep me", true, CancellationToken.None);

        IReadOnlyList<SavedInput> left = await connection.DiscardSavedInput(workspace.Id, saved[0].Id, CancellationToken.None);

        Assert.Empty(left);
        Assert.Empty(await connection.DiscardSavedInput(workspace.Id, saved[0].Id, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => connection.TranscribeSavedInput(workspace.Id, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateServer_RunsTheUpdateOfTheServerOrCarriesItsRefusal()
    {
        await connection.UpdateServer(CancellationToken.None);

        updater.Verify(x => x.Update(It.IsAny<CancellationToken>()), Times.Once);

        updater.Setup(x => x.Update(It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The server is up to date at 1.2.3."));

        RemoteException refusal = await Assert.ThrowsAsync<RemoteException>(() => connection.UpdateServer(CancellationToken.None));
        Assert.Equal("The server is up to date at 1.2.3.", refusal.Message);
    }

    [Fact]
    public void ServerVersion_IsTheOneTheServerSaysInItsHandshake() => Assert.Equal("1.2.3", connection.ServerVersion);

    [Fact]
    public async Task ReviewAndExpandPrompts_GlobalOnesComeWithTheWorkspaceSettingsAndAWorkspaceCanOverrideAndClearThem()
    {
        FolderListing app = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "prompts", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None);
        AiEnvironment environment = await connection.GetAiEnvironment(CancellationToken.None);

        WorkspaceSettings first = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        await connection.SetAiEnvironment(environment with { ReviewPrompt = "global review" }, CancellationToken.None);
        WorkspaceSettings own = await connection.SetWorkspaceSettings(workspace.Id, first with { ExpandPrompt = " my expand " }, CancellationToken.None);
        WorkspaceInfo listed = Assert.Single((await connection.ListWorkspaces(CancellationToken.None)).Workspaces);
        WorkspaceSettings cleared = await connection.SetWorkspaceSettings(workspace.Id, own with { ExpandPrompt = null }, CancellationToken.None);

        Assert.Equal((null, null, "review workspace and fix any problems or inconsistencies", "expand upon content in workspace in areas that make sense"), (first.ReviewPrompt, first.ExpandPrompt, first.GlobalReviewPrompt, first.GlobalExpandPrompt));
        Assert.Equal((null, "my expand", "global review"), (own.ReviewPrompt, own.ExpandPrompt, own.GlobalReviewPrompt));
        Assert.Equal("my expand", listed.ExpandPrompt);
        Assert.Null(cleared.ExpandPrompt);
    }

    [Fact]
    public async Task WrongKeyIsRejected()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(settings with { AccessKey = "wrong" }, CancellationToken.None));
    }

    [Fact]
    public async Task WorkspaceLifecycle_CreateBrowseRenameDelete()
    {
        FolderListing parent = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "projects", CancellationToken.None);
        FolderListing app = await connection.CreateFolder(parent.Path, "app", CancellationToken.None);

        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None);
        Assert.Equal("app", workspace.Name);
        Assert.Equal([workspace], (await connection.ListWorkspaces(CancellationToken.None)).Workspaces);
        await Assert.ThrowsAsync<RemoteException>(() => connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None));

        File.WriteAllText(Path.Combine(app.Path, "notes.txt"), "remember");
        WorkspaceFiles files = await connection.ListFiles(workspace.Id, string.Empty, false, CancellationToken.None);
        Assert.Equal("notes.txt", Assert.Single(files.Entries).Path);
        Assert.Equal("remember", (await connection.ReadFile(workspace.Id, "notes.txt", CancellationToken.None)).Text);
        await Assert.ThrowsAsync<RemoteException>(() => connection.ListFiles(workspace.Id, "..", false, CancellationToken.None));

        WorkspaceInfo renamed = await connection.RenameWorkspace(workspace.Id, "app2", CancellationToken.None);
        Assert.Equal("app2", renamed.Name);
        Assert.True(File.Exists(Path.Combine(parent.Path, "app2", "notes.txt")));
        Assert.Equal(renamed, Assert.Single((await connection.ListWorkspaces(CancellationToken.None)).Workspaces));

        await connection.DeleteWorkspace(workspace.Id, false, CancellationToken.None);
        Assert.Empty((await connection.ListWorkspaces(CancellationToken.None)).Workspaces);
        Assert.True(Directory.Exists(renamed.Folder));

        WorkspaceInfo again = await connection.CreateWorkspace(renamed.Folder, null, null, null, CancellationToken.None);
        await connection.DeleteWorkspace(again.Id, true, CancellationToken.None);
        Assert.False(Directory.Exists(renamed.Folder));
    }

    [Fact]
    public async Task Prompt_StreamsTheConversationAsEventsAndResumesItsAiSessionAfterTheSessionWasClosed()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "work", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        await connection.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && Latest(events).Kind == RemoteEventKind.WorkingChanged);
        await connection.SetWorkspaceSettings(workspace.Id, new WorkspaceSettings { Capability = AiCapability.Fast, Effort = AiEffort.Low }, CancellationToken.None);
        await connection.Prompt(workspace.Id, "again", CancellationToken.None);
        List<RemoteEvent> events = await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && Latest(events).Kind == RemoteEventKind.WorkingChanged);

        Assert.Equal(["hello", "reply to hello", "again", "reply to again"], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
        ChatHistory history = await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None);
        Assert.Equal(["hello", "reply to hello", "again", "reply to again"], history.Messages.Select(message => message.Text));
        Assert.False(history.Working);
    }

    [Fact]
    public async Task Prompt_SentWhileTheAiWorksWaitsAndIsAnsweredRightAfterInTheSameWorkingSpan()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "slow", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        // The work goes on until the test lets it end, however long the steps in between take.
        await connection.Prompt(workspace.Id, "gate long", CancellationToken.None);
        await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged && remoteEvent.Working == true));
        Assert.True((await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).Working);
        await connection.Prompt(workspace.Id, "short", CancellationToken.None);
        Gate("gate long").Release();
        List<RemoteEvent> events = await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && Latest(events).Working == false);

        Assert.Equal(["gate long", "short", "reply to gate long", "reply to short"], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
        Assert.Equal([true, false], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged).Select(remoteEvent => remoteEvent.Working == true));
    }

    [Fact]
    public async Task AUsageWindowAtTheMaximumPausesTheAiWhileItWorksAndEveryoneSeesItUntilTheWindowIsBelowItAndTheAiIsToldToResume()
    {
        UsageStatus Status(double percent) => new() { Service = AiService.ClaudeCode, Windows = [new UsagePeriod { Name = "5 hours", PercentUsed = percent, WindowMinutes = 300 }] };
        usageOf.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(50));
        usageOf.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(97));
        FolderListing folder = await connection.CreateFolder(temp.Path, "limited", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        await connection.Prompt(workspace.Id, "long", CancellationToken.None);
        usageOf.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(97));
        List<RemoteEvent> paused = await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.LimitPauseChanged && remoteEvent.LimitPause is not null));

        LimitPause pause = paused.First(remoteEvent => remoteEvent.Kind == RemoteEventKind.LimitPauseChanged).LimitPause!;
        Assert.Equal(("5 hours", 97d, 95d), (pause.Window, pause.PercentUsed, pause.MaxPercent));
        Assert.Equal(pause.Window, (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).LimitPause?.Window);

        usageOf.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(40));
        usageOf.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(40));
        List<RemoteEvent> events = await Received(items => items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.LimitPauseChanged && remoteEvent.LimitPause is null) && items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message?.Role == MessageRole.Assistant) >= 3);

        string[] said = [.. events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message?.Role == MessageRole.User).Select(remoteEvent => remoteEvent.Message!.Text)];
        Assert.Equal(["long", "pause current work", "resume unfinished work"], said);
        Assert.Null((await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).LimitPause);
    }

    [Fact]
    public async Task APromptOverTheMaximumIsRefusedUntilTheUserConfirmsAndThenTheWorkspaceIgnoresTheMaximumForThatWork()
    {
        UsageStatus Status(double percent) => new() { Service = AiService.ClaudeCode, Windows = [new UsagePeriod { Name = "5 hours", PercentUsed = percent, WindowMinutes = 300 }] };
        usageOf.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(97));
        FolderListing folder = await connection.CreateFolder(temp.Path, "confirmed", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        RemoteException refused = await Assert.ThrowsAsync<RemoteException>(() => connection.Prompt(workspace.Id, "long", CancellationToken.None));

        Assert.Equal(("5 hours", 97d, 95d), (refused.OverLimit?.Window, refused.OverLimit?.PercentUsed, refused.OverLimit?.MaxPercent));
        lock (received)
        {
            Assert.DoesNotContain(received, remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded);
        }

        await connection.PromptOverLimit(workspace.Id, "long", CancellationToken.None);
        List<RemoteEvent> events = await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && Latest(items).Working == false);

        Assert.Equal(["long", "reply to long"], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
        Assert.DoesNotContain(events, remoteEvent => remoteEvent.Kind == RemoteEventKind.LimitPauseChanged);

        await connection.CancelLimitPause(workspace.Id, CancellationToken.None);
        await Assert.ThrowsAsync<RemoteException>(() => connection.Prompt(workspace.Id, "again", CancellationToken.None));
    }

    [Fact]
    public async Task RunScript_GivesAScriptTheDisplayOfAClientOnTheSameMachineAndNothingElseOfItsEnvironment()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "scripted", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "a.cs"), "// not run by the fake");
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        Dictionary<string, string> client = new() { ["DISPLAY"] = ":7", ["WAYLAND_DISPLAY"] = "wayland-9", ["AUTO_REMOTE_PROBE"] = "leaked" };
        await using IServerConnection graphical = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>(), name => client.GetValueOrDefault(name)).Connect(settings, CancellationToken.None);

        await graphical.RunScript(workspace.Id, "a.cs", CancellationToken.None);
        List<RemoteEvent> events = await Received(items => items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.ScriptOutput && remoteEvent.Text!.Contains("display=", StringComparison.Ordinal)));

        Assert.Equal("display=:7 wayland=wayland-9 other=", string.Concat(events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.ScriptOutput).Select(remoteEvent => remoteEvent.Text)).Trim());
    }

    [Fact]
    public async Task GetResources_ShowsWhatTheProcessesOfARunningScriptTakeUnderItsWorkspace()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        FolderListing folder = await connection.CreateFolder(temp.Path, "measured", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "slow.cs"), "// not run by the fake");
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        await connection.RunScript(workspace.Id, "slow.cs", CancellationToken.None);
        ResourceReport report = await connection.GetResources(CancellationToken.None);

        for (int attempt = 0; attempt < 100 && report.Workspaces.FirstOrDefault(item => item.Id == workspace.Id)?.Total.Processes is not 2; attempt++)
        {
            await Task.Delay(100);
            report = await connection.GetResources(CancellationToken.None);
        }

        await connection.StopScript(workspace.Id, "slow.cs", CancellationToken.None);

        Assert.True(report.Supported);
        Assert.True(report.Machine!.TotalMemory > report.Machine.AvailableMemory);
        Assert.True(report.Server.Memory > 0);
        WorkspaceResources measured = report.Workspaces.Single(item => item.Id == workspace.Id);
        Assert.True(measured.IsWorking);
        Assert.Equal(("measured", 2), (measured.Name, measured.Total.Processes));
        Assert.True(measured.Total.Memory > 0);
        Assert.Contains(measured.Total.Heaviest, process => process.Command.StartsWith("sleep 30", StringComparison.Ordinal));
        Assert.Equal("Main", Assert.Single(measured.Sessions).Name);
    }

    [Fact]
    public async Task RunScript_WhileTheAiWorksRunsBesideItAndNeverChangesTheStateOfTheWorkspace()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "beside", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "a.cs"), "// not run by the fake");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "b.cs"), "// not run by the fake");
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        // The AI works until both scripts are done, however long they take.
        await connection.Prompt(workspace.Id, "gate beside", CancellationToken.None);
        await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged && remoteEvent.Working == true));
        await connection.RunScript(workspace.Id, "a.cs", CancellationToken.None);
        await connection.RunScript(workspace.Id, "b.cs", CancellationToken.None);
        WorkspaceState during = await connection.GetWorkspaceState(workspace.Id, CancellationToken.None);
        await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.ScriptChanged && remoteEvent.Script?.State == ScriptState.Succeeded) == 2);
        Gate("gate beside").Release();
        List<RemoteEvent> events = await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.ScriptChanged && remoteEvent.Script?.State == ScriptState.Succeeded) == 2 && Latest(items).Working == false);

        Assert.Equal(WorkspaceState.Working, during);
        Assert.DoesNotContain(events, remoteEvent => remoteEvent.Kind == RemoteEventKind.StateChanged && remoteEvent.State is not (WorkspaceState.Ready or WorkspaceState.Working));
        Assert.Equal(WorkspaceState.Ready, await connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AFailureOfTheAi_IsRecordedWithItsTimeForItsWorkspaceOnlyAndShownToNoOther()
    {
        FolderListing folderOne = await connection.CreateFolder(temp.Path, "failing", CancellationToken.None);
        FolderListing folderTwo = await connection.CreateFolder(temp.Path, "unrelated", CancellationToken.None);
        WorkspaceInfo failing = await connection.CreateWorkspace(folderOne.Path, null, null, null, CancellationToken.None);
        WorkspaceInfo unrelated = await connection.CreateWorkspace(folderTwo.Path, null, null, null, CancellationToken.None);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await connection.Prompt(failing.Id, "fail now", CancellationToken.None);
        List<RemoteEvent> events = await Received(items => items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.ErrorsChanged));
        await connection.Prompt(unrelated.Id, "hello", CancellationToken.None);
        events = await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.WorkspaceId == unrelated.Id) == 2);

        IReadOnlyList<ErrorEntry> recorded = await connection.GetErrors(failing.Id, CancellationToken.None);
        IReadOnlyList<ErrorEntry> none = await connection.GetErrors(unrelated.Id, CancellationToken.None);

        ErrorEntry entry = Assert.Single(recorded);
        Assert.Equal("boom: fail now", entry.Message);
        Assert.InRange(entry.Time, before - TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));
        Assert.Empty(none);
        Assert.Equal(entry.Time, events.First(remoteEvent => remoteEvent.Kind == RemoteEventKind.Failed).Time);
        Assert.All(events.Where(remoteEvent => remoteEvent.Kind is RemoteEventKind.Failed or RemoteEventKind.ErrorsChanged), remoteEvent => Assert.Equal(failing.Id, remoteEvent.WorkspaceId));
        Assert.Equal(recorded, events.Single(remoteEvent => remoteEvent.Kind == RemoteEventKind.ErrorsChanged).Errors);
        Assert.True(File.Exists(Path.Combine(temp.Combine("data"), "errors", $"{failing.Id:N}.json")));
        Assert.False(File.Exists(Path.Combine(temp.Combine("data"), "errors", $"{unrelated.Id:N}.json")));

        await connection.DeleteWorkspace(failing.Id, false, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(temp.Combine("data"), "errors", $"{failing.Id:N}.json")));
    }

    [Fact]
    public async Task TheWorkOfTheAi_IsKeptAlwaysShownInTheHistoryOnlyWhenTheWorkspaceShowsItAndChangesForEveryClientAtOnce()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "working", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        await using IServerConnection other = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(settings, CancellationToken.None);
        TaskCompletionSource changed = new();
        other.EventReceived += remoteEvent =>
        {
            if (remoteEvent.Kind == RemoteEventKind.ShowWorkChanged && remoteEvent.WorkspaceId == workspace.Id)
            {
                changed.TrySetResult();
            }
        };

        await connection.Prompt(workspace.Id, "work please", CancellationToken.None);
        await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && Latest(events).Working == false);
        ChatHistory hidden = await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None);

        await connection.SetShowWork(workspace.Id, true, CancellationToken.None);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ChatHistory shown = await other.GetHistory(workspace.Id, null, null, null, CancellationToken.None);

        Assert.False(hidden.ShowWork);
        Assert.Equal(["work please", "reply to work please"], hidden.Messages.Select(message => message.Text));
        Assert.True(shown.ShowWork);
        Assert.Equal(["work please", "Run ls", "→ a.txt", "reply to work please"], shown.Messages.Select(message => message.Text));
        Assert.Equal([false, true, true, false], shown.Messages.Select(message => message.IsWork));
        Assert.Contains(workspace.Id, (await connection.ListWorkspaces(CancellationToken.None)).Workspaces.Where(item => item.ShowWork).Select(item => item.Id));

        await connection.SetShowWork(workspace.Id, false, CancellationToken.None);

        Assert.Equal(["work please", "reply to work please"], (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task TheAnswerOfTheAi_IsStreamedOnlyWhileTheWorkspaceShowsTheWorkAndEndsAsTheFinishedMessage()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "streaming", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        await connection.Prompt(workspace.Id, "stream quietly", CancellationToken.None);
        List<RemoteEvent> quiet = await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && Latest(items).Working == false);

        Assert.DoesNotContain(quiet, remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta);

        await connection.SetShowWork(workspace.Id, true, CancellationToken.None);
        await Received(items => items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.ShowWorkChanged));
        int before;

        lock (received)
        {
            before = received.Count;
        }

        await connection.Prompt(workspace.Id, "stream aloud", CancellationToken.None);
        List<RemoteEvent> loud = await Received(items => items.Skip(before).Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && Latest(items).Working == false);
        List<RemoteEvent> events = [.. loud.Skip(before)];
        string[] pieces = [.. events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta).Select(remoteEvent => remoteEvent.Text!)];
        int final = events.FindIndex(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message!.Role == MessageRole.Assistant);

        Assert.True(pieces.Length >= 2);
        Assert.StartsWith(string.Concat(pieces), "The answer is streamed.", StringComparison.Ordinal);
        Assert.DoesNotContain(events.Skip(final + 1), remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta);
        Assert.Equal("The answer is streamed.", events[final].Message!.Text);
        Assert.Equal(["stream quietly", "The answer is streamed.", "stream aloud", "The answer is streamed."], (await connection.GetHistory(workspace.Id, null, null, null, CancellationToken.None)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task History_IsReadInChunksFromTheNewestBackToTheBeginning()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "chunks", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        for (int index = 0; index < 3; index++)
        {
            await connection.Prompt(workspace.Id, $"q{index}", CancellationToken.None);
            await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == (index + 1) * 2 && Latest(events).Working == false);
        }

        ChatHistory newest = await connection.GetHistory(workspace.Id, null, 4, null, CancellationToken.None);
        ChatHistory older = await connection.GetHistory(workspace.Id, newest.Older, 4, null, CancellationToken.None);

        Assert.NotNull(newest.Older);
        Assert.Equal(["q1", "reply to q1", "q2", "reply to q2"], newest.Messages.Select(message => message.Text));
        Assert.Null(older.Older);
        Assert.Equal(["q0", "reply to q0"], older.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task WatchWorkspace_PushesEventsForChangesAnywhereInTheFolderWhoeverMakesThem()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "watched", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(folder.Path, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(folder.Path, ".git", "refs", "heads"));
        await connection.WatchWorkspace(workspace.Id, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(folder.Path, "src", "deep", "new.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "top.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, ".git", "refs", "heads", "main"), "abc");

        List<RemoteEvent> events = await Received(items => items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged && remoteEvent.Path == "src/deep") && items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged && remoteEvent.Path == string.Empty) && items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.GitChanged));
        Assert.All(events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged), remoteEvent => Assert.Equal(workspace.Id, remoteEvent.WorkspaceId));
        Assert.DoesNotContain(events, remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged && remoteEvent.Path!.StartsWith(".git", StringComparison.Ordinal));
        await Assert.ThrowsAsync<RemoteException>(() => connection.WatchWorkspace(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task WatchWorkspace_GoesQuietAgainWhenClientsReadTheGitStatusInResponseToEveryEvent()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "quiet", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "a.txt"), "one");
        Git(folder.Path, "init", "-b", "main");
        Git(folder.Path, "add", "-A");
        Git(folder.Path, "-c", "user.email=t@example.com", "-c", "user.name=T", "commit", "-m", "base");
        await connection.WatchWorkspace(workspace.Id, CancellationToken.None);
        connection.EventReceived += remoteEvent =>
        {
            if (remoteEvent.Kind is RemoteEventKind.FilesChanged or RemoteEventKind.GitChanged)
            {
                _ = connection.GetGitStatus(workspace.Id, CancellationToken.None);
                _ = connection.GetGitChanges(workspace.Id, CancellationToken.None);
            }
        };

        await File.WriteAllTextAsync(Path.Combine(folder.Path, "a.txt"), "two");
        await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged));
        await Task.Delay(1500);
        int settled;

        lock (received)
        {
            settled = received.Count(remoteEvent => remoteEvent.Kind is RemoteEventKind.FilesChanged or RemoteEventKind.GitChanged);
        }

        await Task.Delay(1500);

        lock (received)
        {
            Assert.Equal(settled, received.Count(remoteEvent => remoteEvent.Kind is RemoteEventKind.FilesChanged or RemoteEventKind.GitChanged));
        }
    }

    private void Git(string folder, params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Groups_OrganizeWorkspacesAndSettingsPersist()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "organized", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        WorkspaceGroup group = await connection.CreateGroup("Clients", null, CancellationToken.None);

        await connection.MoveWorkspace(workspace.Id, group.Id, CancellationToken.None);
        await connection.SetWorkspaceSettings(workspace.Id, new WorkspaceSettings { Capability = AiCapability.Strong, Effort = AiEffort.Low }, CancellationToken.None);
        WorkspaceCatalog catalog = await connection.ListWorkspaces(CancellationToken.None);

        Assert.Equal("Clients", Assert.Single(catalog.Groups).Name);
        WorkspaceInfo listed = Assert.Single(catalog.Workspaces);
        Assert.Equal(group.Id, listed.GroupId);
        WorkspaceSettings settings = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        Assert.Equal((AiCapability.Strong, AiEffort.Low), (settings.Capability, settings.Effort));

        WorkspaceGroup nested = await connection.CreateGroup("Acme", group.Id, CancellationToken.None);
        WorkspaceGroup other = await connection.CreateGroup("Archive", null, CancellationToken.None);
        Assert.Equal(other.Id, (await connection.MoveGroup(group.Id, other.Id, CancellationToken.None)).ParentId);
        Assert.Equal(group.Id, (await connection.ListWorkspaces(CancellationToken.None)).Groups.Single(listedGroup => listedGroup.Id == nested.Id).ParentId);

        WorkspaceGroup extra = await connection.CreateGroup("Extra", null, CancellationToken.None);
        await connection.ShiftGroup(extra.Id, -1, CancellationToken.None);
        Assert.Equal([extra.Id, other.Id], (await connection.ListWorkspaces(CancellationToken.None)).Groups.Where(listedGroup => listedGroup.ParentId is null).Select(listedGroup => listedGroup.Id));

        await connection.DeleteGroup(group.Id, CancellationToken.None);
        catalog = await connection.ListWorkspaces(CancellationToken.None);
        Assert.Equal(other.Id, Assert.Single(catalog.Workspaces).GroupId);
        Assert.Equal(other.Id, catalog.Groups.Single(listedGroup => listedGroup.Id == nested.Id).ParentId);
    }

    [Fact]
    public async Task Environment_IsKeptByTheServerAndNeverGivesKeysBack()
    {
        AiEnvironment first = await connection.GetAiEnvironment(CancellationToken.None);
        Assert.Equal((AiService.ClaudeCode, "claude", false), (first.DefaultService, first.ClaudeCommand, first.HasCodexApiKey));

        AiEnvironment saved = await connection.SetAiEnvironment(first with { DefaultService = AiService.Codex, CodexApiKey = "sk-secret", CodexCommand = "/opt/codex" }, CancellationToken.None);
        AiEnvironment again = await connection.GetAiEnvironment(CancellationToken.None);

        Assert.Equal((AiService.Codex, "/opt/codex", true, null), (saved.DefaultService, saved.CodexCommand, saved.HasCodexApiKey, saved.CodexApiKey));
        Assert.Equal(saved, again);
        await Assert.ThrowsAsync<RemoteException>(() => connection.SetAiEnvironment(first with { ClaudeCommand = " " }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateWorkspace_LocksTheProviderAndUsesTheKeyGivenForItsSession()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "keyed", CancellationToken.None);
        await connection.SetAiEnvironment((await connection.GetAiEnvironment(CancellationToken.None)) with { DefaultService = AiService.Codex }, CancellationToken.None);

        WorkspaceInfo defaulted = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        FolderListing other = await connection.CreateFolder(temp.Path, "claude", CancellationToken.None);
        WorkspaceInfo keyed = await connection.CreateWorkspace(other.Path, AiService.ClaudeCode, "sk-one", null, CancellationToken.None);
        WorkspaceSettings settings = await connection.GetWorkspaceSettings(keyed.Id, CancellationToken.None);

        Assert.Equal(AiService.Codex, defaulted.Service);
        Assert.Equal(AiService.ClaudeCode, keyed.Service);
        Assert.Equal(AiService.ClaudeCode, settings.Service);
        Assert.Equal("sk-one", Assert.IsType<ClaudeSetup>(prompterOf.Created.Single(setup => setup.WorkspacePath == other.Path)).ApiKey);
    }

    [Fact]
    public async Task WorkspaceSettings_CoverEverythingTheSessionIsConfiguredWith()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "settings", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        WorkspaceSettings saved = await connection.SetWorkspaceSettings(workspace.Id, new WorkspaceSettings { Capability = AiCapability.Max, Effort = AiEffort.Medium, IsSandboxed = true, IsWebAllowed = false, IsImageGenerationAllowed = false, Instructions = "be brief" }, CancellationToken.None);
        WorkspaceSettings read = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);

        Assert.Equal((AiCapability.Max, AiEffort.Medium, true, false, "be brief"), (read.Capability, read.Effort, read.IsSandboxed, read.IsWebAllowed, read.Instructions));
        Assert.Equal((false, false), (prompterOf.Sessions.Last().Configuration.IsShellAllowed, prompterOf.Sessions.Last().Configuration.IsSystemAccessAllowed));
        Assert.True(Assert.Single((await connection.ListWorkspaces(CancellationToken.None)).Workspaces).IsSandboxed);

        await connection.SetWorkspaceSettings(workspace.Id, read with { IsSandboxed = false }, CancellationToken.None);

        Assert.False((await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None)).IsSandboxed);
        Assert.Equal((true, true), (prompterOf.Sessions.Last().Configuration.IsShellAllowed, prompterOf.Sessions.Last().Configuration.IsSystemAccessAllowed));
        Assert.Equal(saved.Capability, read.Capability);
        Assert.Equal("Fake", read.Descriptors["Provider"]);
    }

    [Fact]
    public async Task MoveWorkspaceToFolder_PointsTheWorkspaceAndItsSessionAtTheFolderEvenWhenTheOldOneIsGone()
    {
        FolderListing source = await connection.CreateFolder(temp.Path, "from", CancellationToken.None);
        FolderListing target = await connection.CreateFolder(temp.Path, "to", CancellationToken.None);
        FolderListing app = await connection.CreateFolder(source.Path, "app", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(app.Path, "notes.txt"), "keep");
        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, null, null, null, CancellationToken.None);
        string movedApp = Path.Combine(target.Path, "app2");
        Directory.Move(app.Path, movedApp);

        WorkspaceInfo moved = await connection.MoveWorkspaceToFolder(workspace.Id, movedApp, CancellationToken.None);

        Assert.Equal((workspace.Id, workspace.SessionId, movedApp), (moved.Id, moved.SessionId, moved.Folder));
        Assert.Equal("keep", (await connection.ReadFile(workspace.Id, "notes.txt", CancellationToken.None)).Text);
        Assert.Equal(moved, Assert.Single((await connection.ListWorkspaces(CancellationToken.None)).Workspaces));
        Assert.Equal(movedApp, prompterOf.Sessions.Single().WorkspacePath);
        await Assert.ThrowsAsync<RemoteException>(() => connection.MoveWorkspaceToFolder(workspace.Id, Path.Combine(temp.Path, "missing"), CancellationToken.None));
        FolderListing other = await connection.CreateFolder(temp.Path, "other", CancellationToken.None);
        await connection.CreateWorkspace(other.Path, null, null, null, CancellationToken.None);
        await Assert.ThrowsAsync<RemoteException>(() => connection.MoveWorkspaceToFolder(workspace.Id, other.Path, CancellationToken.None));
    }

    [Fact]
    public async Task NewWorkspaceDefaults_AreKeptByTheServerOverTheWireAndStartTheSessionsOfNewWorkspaces()
    {
        AiEnvironment first = await connection.GetAiEnvironment(CancellationToken.None);
        Assert.Equal(new WorkspaceDefaults(), first.NewWorkspaces);
        Assert.Equal((false, false, true, string.Empty), (first.NewWorkspaces.IsSandboxed, first.NewWorkspaces.IsImageGenerationAllowed, first.NewWorkspaces.IsWebAllowed, first.NewWorkspaces.Instructions));

        AiEnvironment saved = await connection.SetAiEnvironment(first with { NewWorkspaces = new WorkspaceDefaults { Capability = AiCapability.Strong, Effort = AiEffort.Low, IsSandboxed = true, IsWebAllowed = true, IsImageGenerationAllowed = false, Instructions = string.Empty } }, CancellationToken.None);
        FolderListing folder = await connection.CreateFolder(temp.Path, "defaults", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        WorkspaceSettings settings = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);

        Assert.Equal(string.Empty, saved.NewWorkspaces.Instructions);
        Assert.Equal(saved, await connection.GetAiEnvironment(CancellationToken.None));
        Assert.Equal((AiCapability.Strong, AiEffort.Low, true, true, false), (settings.Capability, settings.Effort, settings.IsSandboxed, settings.IsWebAllowed, settings.IsImageGenerationAllowed));
        Assert.Equal((false, false), (prompterOf.Sessions.Last().Configuration.IsShellAllowed, prompterOf.Sessions.Last().Configuration.IsSystemAccessAllowed));
        Assert.True(string.IsNullOrEmpty(settings.Instructions));
    }
}
