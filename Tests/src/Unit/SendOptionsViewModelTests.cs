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
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
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

    private async Task<(WorkspaceViewModel View, SendOptionsViewModel Options)> Opened(string typed = "do it")
    {
        WorkspaceViewModel view = await Started();
        view.Chat.Input = typed;
        await view.SendOptions.Open(SubmissionKind.Custom);
        return (view, view.SendOptions);
    }

    private void Confirm(SendOptionsViewModel options) => options.ConfirmCommand.Execute(null);

    private void Server(WorkspaceState state) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = workspace.Id, State = state });

    // The client learns from the git status that the server does git work.
    private async Task GitWorks(WorkspaceViewModel view)
    {
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main", Busy = true });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.GitChanged, WorkspaceId = workspace.Id });

        for (int attempt = 0; attempt < 500 && view.State != WorkspaceState.Managing; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(WorkspaceState.Managing, view.State);
    }

    [Fact]
    public async Task ItOpensOnSendingNowToThisChatKeepingTheChangesAndShowsWhatItSubmits()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened(" do it ");

        Assert.Equal((true, true, true, true, "Send"), (options.IsOpen, options.IsNow, options.IsThisChat, options.KeepsChanges, options.ConfirmText));
        Assert.Equal(("Custom", "do it"), (options.SubmissionTitle, options.SubmissionText));
        Assert.Equal((true, false, false, false), (options.ShowCommitChoice, options.ShowLoop, options.IsNewSession, options.HasUnavailableNote));

        Confirm(options);

        Assert.False(options.IsOpen);
        connection.Verify(x => x.Prompt(workspace.Id, "do it", It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.PromptWithCommit(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(string.Empty, view.Chat.Input);
    }

    [Fact]
    public async Task Now_AskedToCommitSendsThePromptSoThatItIs()
    {
        (_, SendOptionsViewModel options) = await Opened();
        options.CommitsAfter = true;

        Confirm(options);

        connection.Verify(x => x.PromptWithCommit(workspace.Id, "do it", false, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Now_AskedToCommitStillIsOnceTheUserConfirmsStartingOverTheUsageLimit()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened();
        connection.Setup(x => x.PromptWithCommit(workspace.Id, "do it", false, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("over", new LimitPause { Window = "5 hours", PercentUsed = 99, MaxPercent = 95, PausedAt = DateTimeOffset.UnixEpoch, NextCheckAt = DateTimeOffset.UnixEpoch }));
        options.CommitsAfter = true;
        Confirm(options);
        await Task.Delay(20);

        view.Chat.ConfirmOverLimitCommand.Execute(null);
        await Task.Delay(20);

        connection.Verify(x => x.PromptWithCommit(workspace.Id, "do it", true, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.PromptOverLimit(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenDone_IsKeptForEachChoiceOfWhenAndStartsAsCommitOnlyForNext()
    {
        (_, SendOptionsViewModel options) = await Opened();
        List<bool> commits = [options.CommitsAfter];
        options.IsNext = true;
        commits.Add(options.CommitsAfter);
        options.IsRepeat = true;
        commits.Add(options.CommitsAfter);
        options.CommitsAfter = true;
        options.IsNow = true;
        commits.Add(options.CommitsAfter);
        options.IsNext = true;
        options.KeepsChanges = true;
        options.IsRepeat = true;
        commits.Add(options.CommitsAfter);
        options.IsNext = true;
        commits.Add(options.CommitsAfter);

        Assert.Equal([false, true, false, false, true, false], commits);
    }

    [Fact]
    public async Task WhenDone_IsOnlyOfferedInARepositoryAndForThisChat()
    {
        (_, SendOptionsViewModel options) = await Opened();
        options.IsNewSession = true;
        bool forSession = options.ShowCommitChoice;

        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        (_, SendOptionsViewModel plain) = await Opened();

        Assert.Equal((false, false), (forSession, plain.ShowCommitChoice));
    }

    [Fact]
    public async Task Now_SaysThatItJoinsWorkThatIsCommittedAnyway()
    {
        (_, SendOptionsViewModel options) = await Opened();
        bool before = options.ShowJoinsCommit;

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.AutoCommitChanged, WorkspaceId = workspace.Id, AutoCommit = AutoCommitState.On });

        Assert.Equal((false, true), (before, options.ShowJoinsCommit));
        options.IsNext = true;
        Assert.False(options.ShowJoinsCommit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Next_QueuesWithTheChosenCommitSettingWhichStartsAsCommit(bool commit)
    {
        (_, SendOptionsViewModel options) = await Opened("later");
        options.IsNext = true;
        Assert.Equal((true, true, "Queue"), (options.ShowCommitChoice, options.CommitsAfter, options.ConfirmText));
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

        await view.SendOptions.Open(SubmissionKind.Custom);
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
    public async Task Repeat_StartsALoopWithWhatIsTypedAndEmptiesTheBoxCommittingOnlyWhenAsked(bool commit)
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("check the logs");
        options.IsRepeat = true;
        Assert.True(options.KeepsChanges);

        if (commit)
        {
            options.CommitsAfter = true;
        }

        LoopSettings? sent = null;
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).Callback((Guid _, LoopSettings settings, CancellationToken _) => sent = settings).Returns(Task.CompletedTask);
        Assert.Equal("Start Loop", options.ConfirmText);

        Confirm(options);

        Assert.Equal(("check the logs", commit), (sent!.Prompt, sent.AutoCommit));
        Assert.Equal((false, string.Empty), (options.IsOpen, view.Chat.Input));
    }

    [Fact]
    public async Task Repeat_KeepsThePanelWithTheReasonWhenItCannotStart()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("go");
        options.IsRepeat = true;
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("A loop already runs in this workspace."));

        Confirm(options);

        Assert.Equal((true, "A loop already runs in this workspace.", "go"), (options.IsOpen, options.Error, view.Chat.Input));
    }

    [Fact]
    public async Task WithAttachedFilesOnlyThisChatNowOrNextIsOfferedAndTheFilesAreShown()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("go");

        view.Chat.Attach("a.txt", new byte[] { 1 });
        options.IsRepeat = true;
        options.IsNewSession = true;

        Assert.Equal((false, false, true, true), (options.CanRepeat, options.CanChooseNewSession, options.IsNow, options.IsThisChat));
        Assert.Equal(("Attached: a.txt", "Attached files can only be sent to this chat, now or next."), (options.SubmissionFiles, options.UnavailableNote));
        options.IsNext = true;
        Assert.True(options.ConfirmCommand.CanExecute(null));
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
        await options.Open(SubmissionKind.Custom);

        Assert.Equal(["This Server"], options.Targets.Select(target => target.Name));
        Assert.True(options.Target.IsLocal);
    }

    [Theory]
    [InlineData(1, "a")]
    [InlineData(2, null)]
    public async Task ARemoteNewSessionDelegatesATaskThatIsBroughtInUnlessKeptSeparate(int target, string? peer)
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened(" port the parser ");
        options.IsNewSession = true;
        options.Target = options.Targets[target];

        Assert.Equal(("Delegate Task", "Title", true, false), (options.ConfirmText, options.NameLabel, options.BringsIn, options.ShowStartingFiles));
        Confirm(options);
        Assert.Equal("Enter a title.", options.Error);
        options.Name = " port it ";

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
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ANewSessionIsGivenWhatIsSubmittedAndBroughtInUnlessKeptSeparate(bool? chosen, bool expected)
    {
        (_, SendOptionsViewModel options) = await Opened(" work ");
        options.IsNewSession = true;
        options.Name = "second";

        if (chosen is { } bringIn)
        {
            options.BringsIn = bringIn;
            options.KeepsSeparate = !bringIn;
        }

        Assert.Equal(expected, options.BringsIn);
        Confirm(options);

        connection.Verify(x => x.CreateSideSession(workspace.Id, "second", SideSessionStart.Files, "work", expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANewSessionIsNotMadeWithAttachedFilesNorAfterASettingsSaveThatFailed()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened("work");
        options.IsNewSession = true;
        options.Name = "second";
        view.Chat.Attach("a.txt", new byte[] { 1 });

        Assert.False(options.ConfirmCommand.CanExecute(null));
        Confirm(options);

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
    [InlineData("Review", "review it")]
    [InlineData("Expand", "expand it")]
    public async Task OpenedForAPromptOfTheWorkspaceItSubmitsThatPromptWhateverIsChosen(string name, string prompt)
    {
        SubmissionKind kind = Enum.Parse<SubmissionKind>(name);
        WorkspaceViewModel view = await Started();
        SendOptionsViewModel options = view.SendOptions;
        List<LoopSettings> loops = [];
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).Callback((Guid _, LoopSettings settings, CancellationToken _) => loops.Add(settings)).Returns(Task.CompletedTask);

        async Task Open()
        {
            view.OpenSendOptionsCommand.Execute(kind);
            await Task.Delay(20);
        }

        await Open();
        Assert.Equal((true, true, name, prompt), (options.IsOpen, options.IsNow, options.SubmissionTitle, options.SubmissionText));
        Confirm(options);
        await Open();
        options.CommitsAfter = true;
        Confirm(options);
        await Open();
        options.IsNext = true;
        Confirm(options);
        await Open();
        options.IsRepeat = true;
        Confirm(options);
        await Open();
        options.IsNewSession = true;
        options.Name = "second";
        Assert.True(options.BringsIn);
        Confirm(options);

        connection.Verify(x => x.Prompt(workspace.Id, prompt, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.PromptWithCommit(workspace.Id, prompt, false, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.QueueInput(workspace.Id, prompt, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal((prompt, false), (Assert.Single(loops).Prompt, loops[0].AutoCommit));
        connection.Verify(x => x.CreateSideSession(workspace.Id, "second", SideSessionStart.Files, prompt, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, "The prompt of this button is empty.")]
    [InlineData(true, "The prompt could not be read: down")]
    public async Task APromptThatIsEmptyOrCannotBeReadIsNotSubmitted(bool fails, string reason)
    {
        WorkspaceViewModel view = await Started();

        if (fails)
        {
            connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));
        }
        else
        {
            connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { ReviewPrompt = " ", GlobalReviewPrompt = "global" });
        }

        view.OpenSendOptionsCommand.Execute(SubmissionKind.Review);
        await Task.Delay(30);

        Assert.Equal((reason, false), (view.SendOptions.Error, view.SendOptions.ConfirmCommand.CanExecute(null)));
    }

    [Theory]
    [InlineData(WorkspaceState.Managing, "This chat cannot take it now: git is working, wait for it to finish.")]
    [InlineData(WorkspaceState.Canceling, "This chat cannot take it now: cancelling and reverting, wait for it to finish.")]
    public async Task TheSendButtonOpensItWithWhatIsNotOfferedClosedWhileTheChatTakesNothingAtOnce(WorkspaceState state, string reason)
    {
        WorkspaceViewModel view = await Started();
        view.Chat.Input = "do it";

        if (state == WorkspaceState.Managing)
        {
            await GitWorks(view);
        }
        else
        {
            Server(state);
        }

        Assert.Equal((true, true, false), (view.ShowSendButton, view.SubmitCommand.CanExecute(null), view.Chat.SendCommand.CanExecute(null)));

        view.SubmitCommand.Execute(null);
        await Task.Delay(20);
        SendOptionsViewModel options = view.SendOptions;

        Assert.Equal((true, true, false, false), (options.IsOpen, options.IsNext, options.IsNow, options.CanRepeat));
        Assert.Equal(reason, options.UnavailableNote);
        connection.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(x => x.QueueInput(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        // A new session is still made of it, which makes Now about that alone.
        options.IsNow = true;
        options.IsThisChat = true;
        Assert.Equal((true, true, false, true), (options.IsNow, options.IsNewSession, options.CanChooseThisChat, options.CanChooseNewSession));
        options.IsNext = true;
        Assert.Equal((true, false), (options.IsThisChat, options.IsNewSession));
    }

    [Fact]
    public async Task DuringACommitThatFollowsWorkALoopCanStillBeStarted()
    {
        WorkspaceViewModel view = await Started();
        view.Chat.Input = "do it";
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.AutoCommitChanged, WorkspaceId = workspace.Id, AutoCommit = AutoCommitState.Committing });
        Assert.Equal(WorkspaceState.Managing, view.State);

        view.SubmitCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal((true, true, true), (view.SendOptions.IsOpen, view.SendOptions.IsNext, view.SendOptions.CanRepeat));
    }

    [Fact]
    public async Task WhileFilesAreAttachedAndTheChatTakesNothingAtOnceOnlyNextIsOffered()
    {
        WorkspaceViewModel view = await Started();
        view.Chat.Input = "do it";
        view.Chat.Attach("a.txt", new byte[] { 1 });
        await GitWorks(view);

        view.SubmitCommand.Execute(null);
        await Task.Delay(20);
        view.SendOptions.IsNow = true;

        Assert.Equal((true, false, false), (view.SendOptions.IsNext, view.SendOptions.CanChooseNow, view.SendOptions.CanRepeat));
    }

    [Fact]
    public async Task WhatWasChosenIsNotChangedWhenItStopsBeingOfferedButCannotBeConfirmed()
    {
        (_, SendOptionsViewModel options) = await Opened();

        Server(WorkspaceState.Canceling);

        Assert.Equal((true, false), (options.IsNow, options.ConfirmCommand.CanExecute(null)));
        Server(WorkspaceState.Ready);
        Assert.True(options.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSendButtonSendsAtOnceWhileTheChatTakesIt()
    {
        WorkspaceViewModel view = await Started();
        view.Chat.Input = "do it";

        view.SubmitCommand.Execute(null);
        await Task.Delay(20);

        Assert.False(view.SendOptions.IsOpen);
        connection.Verify(x => x.Prompt(workspace.Id, "do it", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InADelegatedWorkspaceItIsNotOffered()
    {
        (WorkspaceViewModel view, SendOptionsViewModel options) = await Opened();

        view.Delegation = new WorkspaceDelegation { WorkspaceId = workspace.Id, From = "laptop", Title = "t" };

        Assert.Equal((false, false, false), (options.IsOpen, view.OpenSendOptionsCommand.CanExecute(null), view.OpenSendOptionsCommand.CanExecute(SubmissionKind.Review)));
    }
}
