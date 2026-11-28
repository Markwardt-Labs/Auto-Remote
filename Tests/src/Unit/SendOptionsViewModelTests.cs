namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SendOptionsViewModelTests
{
    public SendOptionsViewModelTests()
    {
        connection.Setup(x => x.GetSavedInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspace.Id, It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it", GlobalExpandPrompt = "expand it" });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(() => swarm);
    }

    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private SwarmStatus swarm = new() { Server = new SwarmServer(), Peers = [Peer("a", "box", true), Peer("b", "listener", false)] };

    private static SwarmPeer Peer(string id, string name, bool sends) => new() { Id = id, Name = name, Fingerprint = "f", Mode = SwarmMode.Direct, SendWork = sends };

    private async Task<WorkspaceViewModel> Started()
    {
        WorkspaceViewModel view = new(connection.Object, workspace);
        await view.Start();
        await Task.Delay(20);
        return view;
    }

    private async Task<(WorkspaceViewModel View, SendOptionsViewModel Options)> Opened(string typed = "")
    {
        WorkspaceViewModel view = await Started();
        view.Chat.Input = typed;
        await view.SendOptions.Open();
        return (view, view.SendOptions);
    }

    private void Confirm(SendOptionsViewModel options) => options.ConfirmCommand.Execute(null);

    [Fact]
    public async Task ItOpensOnSendingNowToThisChatAndSaysSendNeverTurnsOnAutoCommit()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened();

        Assert.Equal((true, true, true, true, "Send"), (options.IsOpen, options.IsNow, options.IsThisChat, options.ShowSendNote, options.ConfirmText));
        Assert.Equal((false, false, false), (options.ShowQueueChoice, options.ShowLoop, options.IsNewSession));
        Assert.False(options.ConfirmCommand.CanExecute(null));

        view.Chat.Input = "do it";

        Assert.True(options.ConfirmCommand.CanExecute(null));
        Confirm(options);

        Assert.False(options.IsOpen);
        connection.Verify(x => x.Prompt(workspace.Id, "do it", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Next_QueuesWithTheChosenCommitSettingWhichStartsAsCommit(bool commit)
    {
        (_, SendOptionsViewModel options) = await Opened("later");
        options.IsNext = true;
        Assert.Equal((true, true, "Queue"), (options.ShowQueueChoice, options.CommitsAfter, options.ConfirmText));
        options.CommitsAfter = commit;
        options.KeepsChanges = !commit;

        Confirm(options);

        connection.Verify(x => x.QueueInput(workspace.Id, "later", commit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Next_StartsFromWhatALoadedDraftKeeps()
    {
        WorkspaceViewModel view = await Started();
        SavedInput draft = new() { Id = Guid.NewGuid(), Text = "kept", Time = DateTimeOffset.UtcNow, AutoCommit = false };
        connection.Setup(x => x.TakeSavedInput(workspace.Id, draft.Id, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        view.Chat.LoadSavedInputCommand.Execute(draft);

        await view.SendOptions.Open();
        view.SendOptions.IsNext = true;

        Assert.True(view.SendOptions.KeepsChanges);
    }

    [Fact]
    public async Task NewSession_CanOnlyBeChosenForNowAndLeavingNowGoesBackToThisChat()
    {
        (_, SendOptionsViewModel options) = await Opened();
        options.IsNext = true;

        options.IsNewSession = true;

        Assert.Equal((false, false), (options.CanChooseNewSession, options.IsNewSession));
        options.IsNow = true;
        options.IsNewSession = true;
        Assert.True(options.IsNewSession);
        options.IsRepeat = true;
        Assert.Equal((true, false), (options.IsThisChat, options.IsNewSession));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeat_StartsALoopWithWhatIsTypedAndEmptiesTheBoxOnlyForThat(bool custom)
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("check the logs");
        options.IsRepeat = true;
        Assert.True(options.Loop.UseCustom);
        options.Loop.UseCustom = custom;
        options.Loop.UseReview = !custom;
        options.Loop.CommitEachRun = true;
        LoopSettings? sent = null;
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).Callback((Guid _, LoopSettings settings, CancellationToken _) => sent = settings).Returns(Task.CompletedTask);
        Assert.Equal("Start Loop", options.ConfirmText);

        Confirm(options);

        Assert.Equal((custom ? "check the logs" : "review it", true), (sent!.Prompt, sent.AutoCommit));
        Assert.Equal((false, custom ? string.Empty : "check the logs"), (options.IsOpen, view.Chat.Input));
    }

    [Fact]
    public async Task Repeat_KeepsThePanelWithTheReasonWhenItCannotStartAndRefusesAttachmentsInTheLoopsPrompt()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("go");
        options.IsRepeat = true;
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("A loop already runs in this workspace."));

        Confirm(options);

        Assert.Equal((true, "A loop already runs in this workspace.", "go"), (options.IsOpen, options.Error, view.Chat.Input));

        view.Chat.Input = " ";
        Confirm(options);
        Assert.Equal("Write the prompt the loop sends.", options.Error);

        view.Chat.Attach("a.txt", new byte[] { 1 });
        Confirm(options);
        Assert.Equal("A loop cannot send attached files. Remove them first.", options.Error);
    }

    [Fact]
    public async Task Repeat_IsNotOfferedWhileALoopRuns()
    {
        (_, SendOptionsViewModel options) = await Opened("go");
        options.IsRepeat = true;

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UtcNow } });

        Assert.Equal((false, false), (options.CanRepeat, options.ConfirmCommand.CanExecute(null)));
        options.IsNow = true;
        options.IsRepeat = true;
        Assert.True(options.IsNow);
    }

    [Fact]
    public async Task RunOn_OffersThisServerEachPeerThatReceivesWorkAndAnyOfThem()
    {
        (_, SendOptionsViewModel options) = await Opened();

        Assert.Equal(["This Server", "box", "Any Server"], options.Targets.Select(target => target.Name));

        swarm = new SwarmStatus { Server = new SwarmServer(), Peers = [Peer("b", "listener", false)] };
        await options.Open();

        Assert.Equal(["This Server"], options.Targets.Select(target => target.Name));
        Assert.True(options.Target.IsLocal);
    }

    [Theory]
    [InlineData(1, "a")]
    [InlineData(2, null)]
    public async Task ARemoteNewSessionDelegatesATaskThatIsBroughtInUnlessKeptSeparate(int target, string? peer)
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened();
        options.IsNewSession = true;
        options.Target = options.Targets[target];

        Assert.Equal(("Delegate Task", "Title", "Instructions", true, false), (options.ConfirmText, options.NameLabel, options.TaskLabel, options.BringsIn, options.ShowStartingFiles));
        Confirm(options);
        Assert.Equal("Enter a title.", options.Error);
        options.Name = " port it ";
        Confirm(options);
        Assert.Equal("Write the instructions of the task.", options.Error);

        view.Chat.Input = " port the parser ";
        options.ExpectedResult = "it builds";
        options.SelectedCapability = options.Capabilities.Single(choice => choice.Value == AiCapability.Strong);
        SwarmTaskInfo added = new() { Id = Guid.NewGuid(), Title = "port it", State = SwarmTaskState.Pending };
        SwarmTaskRequest? sent = null;
        connection.Setup(x => x.DelegateTask(workspace.Id, It.IsAny<SwarmTaskRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, SwarmTaskRequest request, CancellationToken _) =>
        {
            sent = request;
            return swarm with { Tasks = [added] };
        });
        IReadOnlyList<Guid>? followed = null;
        view.Delegated += ids => followed = ids;

        Confirm(options);

        Assert.Equal(("port it", "port the parser", "it builds", AiCapability.Strong, peer, true), (sent!.Title, sent.Instructions, sent.ExpectedResult, sent.Capability, sent.PeerId, sent.BringIn));
        Assert.Equal([added.Id], followed);
        Assert.Equal((false, string.Empty, "The task was delegated."), (options.IsOpen, view.Chat.Input, view.Chat.Notice));
        connection.Verify(x => x.CreateSideSession(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<SideSessionStart>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("", null, false)]
    [InlineData("work", null, true)]
    [InlineData("work", false, false)]
    [InlineData("", true, true)]
    public async Task ALocalNewSessionIsBroughtInByDefaultWhenItIsGivenWork(string typed, bool? chosen, bool expected)
    {
        (_, SendOptionsViewModel options) = await Opened(typed);
        options.IsNewSession = true;
        options.Name = "second";

        if (chosen is { } bringIn)
        {
            options.BringsIn = bringIn;
            options.KeepsSeparate = !bringIn;
        }

        Assert.Equal(expected, options.BringsIn);
        Confirm(options);

        connection.Verify(x => x.CreateSideSession(workspace.Id, "second", SideSessionStart.Files, typed.Length > 0 ? typed : null, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANewSessionRefusesAttachedFilesAndASettingsSaveThatFailed()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("work");
        options.IsNewSession = true;
        options.Name = "second";
        view.Chat.Attach("a.txt", new byte[] { 1 });

        Confirm(options);
        Assert.Equal("Attached files cannot go to a new session. Remove them first.", options.Error);

        view.Chat.Attachments.Clear();
        options.PrepareMain = _ => Task.FromResult(false);
        Confirm(options);

        Assert.Equal("The workspace's settings could not be saved.", options.Error);
        connection.Verify(x => x.CreateSideSession(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<SideSessionStart>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFailureOfARequestWhosePanelWasClosedShowsInTheChat()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("go");
        TaskCompletionSource slow = new();
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).Returns(slow.Task);
        options.IsRepeat = true;
        Confirm(options);

        options.CancelCommand.Execute(null);
        slow.SetException(new RemoteException("it did not work"));
        await Task.Delay(50);

        Assert.Equal("it did not work", view.Chat.Error);
    }

    [Theory]
    [InlineData("Review")]
    [InlineData("Expand")]
    public async Task AskedForWithAPromptOfTheWorkspaceItOpensOnRepeatingThatPromptAndAsksForNothingElseLater(string name)
    {
        LoopPromptKind prompt = Enum.Parse<LoopPromptKind>(name);
        WorkspaceViewModel view = await Started();

        view.OpenSendOptionsCommand.Execute(prompt);
        SendOptionsViewModel options = view.SendOptions;

        Assert.Equal((true, true, true, "Start Loop"), (options.IsOpen, options.IsRepeat, options.ShowLoop, options.ConfirmText));
        Assert.Equal((prompt == LoopPromptKind.Review, prompt == LoopPromptKind.Expand), (options.Loop.UseReview, options.Loop.UseExpand));

        options.CancelCommand.Execute(null);
        view.OpenSendOptionsCommand.Execute(null);

        Assert.Equal((true, true, false), (options.IsOpen, options.IsNow, options.ShowLoop));
    }

    [Fact]
    public async Task AskedForWithAPromptWhileALoopRunsItOpensOnSendingNow()
    {
        WorkspaceViewModel view = await Started();
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch } });

        view.OpenSendOptionsCommand.Execute(LoopPromptKind.Expand);

        Assert.Equal((true, true, false), (view.SendOptions.IsOpen, view.SendOptions.IsNow, view.SendOptions.CanRepeat));
    }

    [Fact]
    public async Task InADelegatedWorkspaceItIsNotOffered()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened();

        view.Delegation = new WorkspaceDelegation { WorkspaceId = workspace.Id, From = "laptop", Title = "t" };

        Assert.Equal((false, false, false), (options.IsOpen, view.OpenSendOptionsCommand.CanExecute(null), view.OpenSendOptionsCommand.CanExecute(LoopPromptKind.Review)));
    }
}
