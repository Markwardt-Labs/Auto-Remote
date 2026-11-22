namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly CancellationTokenSource stopping = new();

    private Task running = Task.CompletedTask;

    private IServerConnection connection = null!;

    private readonly List<RemoteEvent> received = [];

    private ConnectionSettings settings = null!;

    private FakePrompter prompterOf = null!;

    private Mock<IUsageService> usageOf = null!;

    private readonly Mock<IServerUpdater> updater = new();

    public async Task InitializeAsync()
    {
        string fakeDotnet = temp.Combine("fake-dotnet");
        await File.WriteAllTextAsync(fakeDotnet, "#!/bin/sh\necho \"display=$DISPLAY wayland=$WAYLAND_DISPLAY other=$AUTO_REMOTE_PROBE\"\n");
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
        FakePrompter prompter = prompterOf = new() { OnCreate = session => session.ReplyWith(Answer) };
        PrompterHost host = new(_ => prompter);
        WorkspaceManager workspaces = new(store, folders, hub, host);
        PromptService prompts = new(workspaces, store, host, hub, activity, snapshots);
        WorkspaceVersioning versioning = new(git, prompts, hub, activity);
        Mock<IUsageService> usage = usageOf = new();
        WorkspaceLoops loops = new(workspaces, prompts, activity, usage.Object, hub);
        ScriptRunner scripts = new(hub);
        RequestHandler handler = new(workspaces, folders, new WorkspaceFileBrowser(), git, versioning, new SavedInputs(store, workspaces, hub), new ErrorHistory(store, workspaces, hub), new WindowGuard(workspaces, prompts, usage.Object, host, hub) { CheckEvery = TimeSpan.FromMilliseconds(50), RecheckEvery = TimeSpan.FromMilliseconds(1200) }, prompts, new DiagramService(), scripts, usage.Object, activity, new WorkspaceControl(activity, prompts, loops, versioning, snapshots), new SpeechService(Mock.Of<IWhisperEngine>()), host, loops, new WorkspaceWatcher(hub, Mock.Of<ILog>(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300)), updater.Object, Mock.Of<ILog>());
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

    private IEnumerable<PromptEvent> Answer(FakePrompt prompt)
    {
        if (prompt.Text.StartsWith("fail", StringComparison.Ordinal))
        {
            return [new PromptFailureEvent { Message = "boom: " + prompt.Text }];
        }

        if (prompt.Text.StartsWith("stream", StringComparison.Ordinal))
        {
            return Writing();
        }

        if (prompt.Text.StartsWith("work", StringComparison.Ordinal))
        {
            return [new PromptToolCallEvent { Id = "t1", Name = "shell", Input = "{\"command\":\"ls\"}" }, new PromptToolResultEvent { Id = "t1", Output = "a.txt" }, new PromptMessageEvent { Text = "reply to " + prompt.Text }];
        }

        if (prompt.Text == "long")
        {
            Thread.Sleep(600);
        }

        return [new PromptMessageEvent { Text = "reply to " + prompt.Text }];
    }

    private IEnumerable<PromptEvent> Writing()
    {
        foreach (string piece in (string[])["The ", "answer ", "is ", "streamed."])
        {
            yield return new PromptDeltaEvent { Text = piece };
            Thread.Sleep(150);
        }

        yield return new PromptMessageEvent { Text = "The answer is streamed." };
    }

    private async Task<List<RemoteEvent>> Received(Func<List<RemoteEvent>, bool> done)
    {
        for (int attempt = 0; attempt < 250; attempt++)
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
        await connection.DisposeAsync();
        await stopping.CancelAsync();
        await running;
        temp.Dispose();
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

        await connection.SaveInput(workspace.Id, "first", CancellationToken.None);
        IReadOnlyList<SavedInput> saved = await connection.SaveInput(workspace.Id, "second\nline", CancellationToken.None);

        Assert.Equal(["second\nline", "first"], saved.Select(input => input.Text).ToList());
        Assert.Equal(["second\nline", "first"], (await other.GetSavedInputs(workspace.Id, CancellationToken.None)).Select(input => input.Text).ToList());
        SavedInput taken = await other.TakeSavedInput(workspace.Id, saved[0].Id, CancellationToken.None);
        Assert.Equal("second\nline", taken.Text);
        await Assert.ThrowsAsync<RemoteException>(() => connection.TakeSavedInput(workspace.Id, saved[0].Id, CancellationToken.None));
        Assert.Equal(["first"], (await connection.GetSavedInputs(workspace.Id, CancellationToken.None)).Select(input => input.Text).ToList());
        await Assert.ThrowsAsync<RemoteException>(() => connection.SaveInput(workspace.Id, "  ", CancellationToken.None));

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
    public async Task Transcribe_SendsTheRecordingToTheSpeechCommandAndAnswersItsText()
    {
        Assert.Equal("said 3", await connection.Transcribe(new byte[] { 1, 2, 3 }, CancellationToken.None));
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
        await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && events.Last().Kind == RemoteEventKind.WorkingChanged);
        await connection.SetWorkspaceSettings(workspace.Id, new WorkspaceSettings { Capability = AiCapability.Fast, Effort = AiEffort.Low }, CancellationToken.None);
        await connection.Prompt(workspace.Id, "again", CancellationToken.None);
        List<RemoteEvent> events = await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && events.Last().Kind == RemoteEventKind.WorkingChanged);

        Assert.Equal(["hello", "reply to hello", "again", "reply to again"], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
        ChatHistory history = await connection.GetHistory(workspace.Id, null, null, CancellationToken.None);
        Assert.Equal(["hello", "reply to hello", "again", "reply to again"], history.Messages.Select(message => message.Text));
        Assert.False(history.Working);
    }

    [Fact]
    public async Task Prompt_SentWhileTheAiWorksWaitsAndIsAnsweredRightAfterInTheSameWorkingSpan()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "slow", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        await connection.Prompt(workspace.Id, "long", CancellationToken.None);
        await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged && remoteEvent.Working == true));
        Assert.True((await connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).Working);
        await connection.Prompt(workspace.Id, "short", CancellationToken.None);
        List<RemoteEvent> events = await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && events.Last().Working == false);

        Assert.Equal(["long", "short", "reply to long", "reply to short"], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
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
        Assert.Equal(pause.Window, (await connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).LimitPause?.Window);

        usageOf.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(40));
        usageOf.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(40));
        List<RemoteEvent> events = await Received(items => items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.LimitPauseChanged && remoteEvent.LimitPause is null) && items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message?.Role == MessageRole.Assistant) >= 3);

        string[] said = [.. events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message?.Role == MessageRole.User).Select(remoteEvent => remoteEvent.Message!.Text)];
        Assert.Equal(["long", "pause current work", "resume unfinished work"], said);
        Assert.Null((await connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).LimitPause);
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
        Assert.DoesNotContain(received, remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded);

        await connection.PromptOverLimit(workspace.Id, "long", CancellationToken.None);
        List<RemoteEvent> events = await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && items.Last().Working == false);

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
    public async Task RunScript_WhileTheAiWorksRunsBesideItAndNeverChangesTheStateOfTheWorkspace()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "beside", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "a.cs"), "// not run by the fake");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "b.cs"), "// not run by the fake");
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        await connection.Prompt(workspace.Id, "long", CancellationToken.None);
        await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged && remoteEvent.Working == true));
        await connection.RunScript(workspace.Id, "a.cs", CancellationToken.None);
        await connection.RunScript(workspace.Id, "b.cs", CancellationToken.None);
        WorkspaceState during = await connection.GetWorkspaceState(workspace.Id, CancellationToken.None);
        List<RemoteEvent> events = await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.ScriptChanged && remoteEvent.Script?.State == ScriptState.Succeeded) == 2 && items.Last().Working == false);

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
        await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && events.Last().Working == false);
        ChatHistory hidden = await connection.GetHistory(workspace.Id, null, null, CancellationToken.None);

        await connection.SetShowWork(workspace.Id, true, CancellationToken.None);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ChatHistory shown = await other.GetHistory(workspace.Id, null, null, CancellationToken.None);

        Assert.False(hidden.ShowWork);
        Assert.Equal(["work please", "reply to work please"], hidden.Messages.Select(message => message.Text));
        Assert.True(shown.ShowWork);
        Assert.Equal(["work please", "shell: ls", "a.txt", "reply to work please"], shown.Messages.Select(message => message.Text));
        Assert.Equal([false, true, true, false], shown.Messages.Select(message => message.IsWork));
        Assert.Contains(workspace.Id, (await connection.ListWorkspaces(CancellationToken.None)).Workspaces.Where(item => item.ShowWork).Select(item => item.Id));

        await connection.SetShowWork(workspace.Id, false, CancellationToken.None);

        Assert.Equal(["work please", "reply to work please"], (await connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task TheAnswerOfTheAi_IsStreamedOnlyWhileTheWorkspaceShowsTheWorkAndEndsAsTheFinishedMessage()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "streaming", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        await connection.Prompt(workspace.Id, "stream quietly", CancellationToken.None);
        List<RemoteEvent> quiet = await Received(items => items.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && items.Last().Working == false);

        Assert.DoesNotContain(quiet, remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta);

        await connection.SetShowWork(workspace.Id, true, CancellationToken.None);
        await Received(items => items.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.ShowWorkChanged));
        int before;

        lock (received)
        {
            before = received.Count;
        }

        await connection.Prompt(workspace.Id, "stream aloud", CancellationToken.None);
        List<RemoteEvent> loud = await Received(items => items.Skip(before).Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && items.Last().Working == false);
        List<RemoteEvent> events = [.. loud.Skip(before)];
        string[] pieces = [.. events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta).Select(remoteEvent => remoteEvent.Text!)];
        int final = events.FindIndex(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message!.Role == MessageRole.Assistant);

        Assert.True(pieces.Length >= 2);
        Assert.StartsWith(string.Concat(pieces), "The answer is streamed.", StringComparison.Ordinal);
        Assert.DoesNotContain(events.Skip(final + 1), remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta);
        Assert.Equal("The answer is streamed.", events[final].Message!.Text);
        Assert.Equal(["stream quietly", "The answer is streamed.", "stream aloud", "The answer is streamed."], (await connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task History_IsReadInChunksFromTheNewestBackToTheBeginning()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "chunks", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);

        for (int index = 0; index < 3; index++)
        {
            await connection.Prompt(workspace.Id, $"q{index}", CancellationToken.None);
            await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == (index + 1) * 2 && events.Last().Working == false);
        }

        ChatHistory newest = await connection.GetHistory(workspace.Id, null, 4, CancellationToken.None);
        ChatHistory older = await connection.GetHistory(workspace.Id, newest.Older, 4, CancellationToken.None);

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

        WorkspaceSettings saved = await connection.SetWorkspaceSettings(workspace.Id, new WorkspaceSettings { Capability = AiCapability.Max, Effort = AiEffort.Medium, IsShellAllowed = false, IsSystemAccessAllowed = false, IsWebAllowed = false, IsImageGenerationAllowed = false, Instructions = "be brief" }, CancellationToken.None);
        WorkspaceSettings read = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);

        Assert.Equal((AiCapability.Max, AiEffort.Medium, false, false, false, "be brief"), (read.Capability, read.Effort, read.IsShellAllowed, read.IsSystemAccessAllowed, read.IsWebAllowed, read.Instructions));
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
        Assert.Equal((false, false, false, true, string.Empty), (first.NewWorkspaces.IsShellAllowed, first.NewWorkspaces.IsSystemAccessAllowed, first.NewWorkspaces.IsImageGenerationAllowed, first.NewWorkspaces.IsWebAllowed, first.NewWorkspaces.Instructions));

        AiEnvironment saved = await connection.SetAiEnvironment(first with { NewWorkspaces = new WorkspaceDefaults { Capability = AiCapability.Strong, Effort = AiEffort.Low, IsShellAllowed = false, IsSystemAccessAllowed = false, IsWebAllowed = true, IsImageGenerationAllowed = false, Instructions = string.Empty } }, CancellationToken.None);
        FolderListing folder = await connection.CreateFolder(temp.Path, "defaults", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, null, null, null, CancellationToken.None);
        WorkspaceSettings settings = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);

        Assert.Equal(string.Empty, saved.NewWorkspaces.Instructions);
        Assert.Equal(saved, await connection.GetAiEnvironment(CancellationToken.None));
        Assert.Equal((AiCapability.Strong, AiEffort.Low, false, false, true, false), (settings.Capability, settings.Effort, settings.IsShellAllowed, settings.IsSystemAccessAllowed, settings.IsWebAllowed, settings.IsImageGenerationAllowed));
        Assert.True(string.IsNullOrEmpty(settings.Instructions));
    }
}
